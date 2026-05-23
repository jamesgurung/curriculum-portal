using Azure;
using Azure.Data.Tables;
using Azure.Storage.Blobs;
using System.Globalization;
using System.Text.Json;

namespace CurriculumPortal;

public class AssignmentService
{
  private static readonly TimeSpan CorrectAnswerDelay = TimeSpan.FromSeconds(1 + 4);
  private static readonly TimeSpan IncorrectAnswerDelay = TimeSpan.FromSeconds(5 + 4);
  private static readonly TimeSpan StaffAssignmentCacheLifetime = TimeSpan.FromDays(7);
  private readonly ConfigService _config;
  private readonly CourseService _courseService;
  private readonly BlobContainerClient _cacheClient;
  private readonly TableClient _assignmentsClient;
  private readonly TableClient _questionsClient;
  private readonly TableClient _submissionsClient;

  public AssignmentService(AppOptions options, ConfigService config, CourseService courseService)
  {
    ArgumentNullException.ThrowIfNull(options);
    _cacheClient = new BlobServiceClient(options.StorageAccountConnectionString).GetBlobContainerClient("cache");
    var tableServiceClient = new TableServiceClient(options.StorageAccountConnectionString);
    _assignmentsClient = tableServiceClient.GetTableClient("assignments");
    _questionsClient = tableServiceClient.GetTableClient("questions");
    _submissionsClient = tableServiceClient.GetTableClient("submissions");
    _config = config;
    _courseService = courseService;
  }

  public async Task<HashSet<string>> GenerateAssignments(DateOnly dueDate)
  {
    if (dueDate.DayOfWeek != DayOfWeek.Monday) throw new InvalidOperationException("Assignments must be due on Mondays");
    var assignmentPartitionKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var dueDateText = FormatDate(dueDate);
    var courses = await _courseService.ListCoursesAsync();
    foreach (var course in courses.Where(o => o.AssignmentLength > 0))
    {
      var units = await _courseService.ListUnitsAsync(course.RowKey);
      var yearGroups = units.Select(u => u.YearGroup).Order().Distinct();
      foreach (var yearGroup in yearGroups)
      {
        var currentTerm = $"{yearGroup:D2}{(dueDate.Month < 4 ? "Spring" : dueDate.Month < 9 ? "Summer" : "Autumn")}";
        var pastUnits = units.Where(u => !string.IsNullOrEmpty(u.Term) && string.Compare($"{u.YearGroup:D2}{u.Term}", currentTerm, StringComparison.OrdinalIgnoreCase) < 0).ToList();
        if (pastUnits.Count == 0) continue;

        var assignment = new AssignmentEntity
        {
          PartitionKey = dueDateText,
          RowKey = BuildAssignmentRowKey(yearGroup, course.RowKey),
          Length = course.AssignmentLength
        };
        var existing = await _assignmentsClient.GetEntityIfExistsAsync<AssignmentEntity>(assignment.PartitionKey, assignment.RowKey);
        if (existing.HasValue)
        {
          assignmentPartitionKeys.Add($"{yearGroup:D2}{course.SubjectCode}");
          continue;
        }

        var questions = new List<QuestionBankQuestionWithUnit>();
        foreach (var unit in pastUnits)
        {
          var unitQuestionBank = await _courseService.GetBlobAsync<QuestionBank>(unit.RowKey);
          questions.AddRange(unitQuestionBank.Questions.Select(q => new QuestionBankQuestionWithUnit(q, unit.RowKey, unit.Title)));
        }

        var questionPrefix = BuildQuestionRowKeyPrefix(yearGroup, course.RowKey);
        var pastQuestions = await _questionsClient.QueryAsync<AssignmentQuestionEntity>(
          filter: $"{BuildPartitionKeyLessThanFilter(dueDateText)} and {BuildRowKeyPrefixFilter(questionPrefix)}",
          select: ["PartitionKey", "RowKey", "Question"]).ToListAsync();
        var pastQuestionCounts = pastQuestions
          .GroupBy(q => NormalizeQuestionText(q.Question), StringComparer.OrdinalIgnoreCase)
          .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        var uniqueQuestions = questions
          .GroupBy(q => NormalizeQuestionText(q.Question), StringComparer.OrdinalIgnoreCase)
          .Select(group => group.First())
          .ToList();
        var selectedQuestions = uniqueQuestions.Select(q => new { Question = q, Count = pastQuestionCounts.TryGetValue(NormalizeQuestionText(q.Question), out var c) ? c : 0 })
          .OrderBy(o => o.Count).ThenBy(o => Random.Shared.Next()).Take(course.AssignmentLength).ToList();

        if (selectedQuestions.Count < course.AssignmentLength) continue;
        await _assignmentsClient.AddEntityAsync(assignment);
        assignmentPartitionKeys.Add($"{yearGroup:D2}{course.SubjectCode}");

        var questionEntities = new List<AssignmentQuestionEntity>(selectedQuestions.Count);
        for (var i = 0; i < selectedQuestions.Count; i++)
        {
          var q = selectedQuestions[i].Question;
          questionEntities.Add(new AssignmentQuestionEntity
          {
            PartitionKey = assignment.PartitionKey,
            RowKey = BuildQuestionRowKey(yearGroup, course.RowKey, i),
            Question = q.Question,
            CorrectAnswer = q.CorrectAnswer,
            IncorrectAnswer1 = q.IncorrectAnswer1,
            IncorrectAnswer2 = q.IncorrectAnswer2,
            IncorrectAnswer3 = q.IncorrectAnswer3,
            UnitId = q.UnitId,
            UnitTitle = q.UnitTitle
          });
        }
        await _questionsClient.BatchAddAsync(questionEntities);
      }
    }
    return assignmentPartitionKeys;
  }

  public DateOnly ResolveDueDate(DateOnly dueDate)
  {
    while (_config.Holidays.Any(holiday => dueDate >= holiday.Start && dueDate <= holiday.End))
    {
      dueDate = dueDate.AddDays(7);
    }
    return dueDate;
  }

  public async Task<List<StudentWithCompletion>> GetStudentsWithCompletionAsync(DateOnly deadline)
  {
    var studentsWithClasses = _config.Students
      .Select(student => new { Student = student, Classes = ParseClasses(student.Classes) })
      .Where(o => o.Classes.Count > 0)
      .ToList();
    if (studentsWithClasses.Count == 0) return [];

    var assignmentCourses = (await _courseService.ListCoursesAsync())
      .Where(o => o.AssignmentLength > 0 && !string.IsNullOrWhiteSpace(o.SubjectCode))
      .ToList();
    var coursesByKeyStageAndSubjectCode = BuildCoursesByKeyStageAndSubjectCode(assignmentCourses);
    var assignmentKeys = NormalizeKeys(studentsWithClasses
      .SelectMany(o => o.Classes)
      .Select(cls => GetAssignmentKey(cls, coursesByKeyStageAndSubjectCode))
      .Where(o => o is not null));
    if (assignmentKeys.Count == 0) return [];

    var assignmentsByKey = await LoadAssignmentsByDueDateAsync(assignmentKeys, deadline);
    if (assignmentsByKey.Values.All(o => o.Count == 0)) return [];

    var submissionsByKey = await LoadSubmissionsByDueDateAsync(assignmentKeys, deadline);
    var partitionData = BuildPartitionData(assignmentKeys, assignmentsByKey, submissionsByKey);
    var students = new List<StudentWithCompletion>();

    foreach (var studentWithClasses in studentsWithClasses)
    {
      var completionGroups = studentWithClasses.Classes
        .Select(cls => new
        {
          BehaviourCode = cls.YearGroup is >= 7 and <= 9 ? "KS3" : cls.YearGroup is >= 10 and <= 13 ? cls.SubjectCode : null,
          AssignmentKey = GetAssignmentKey(cls, coursesByKeyStageAndSubjectCode)
        })
        .Where(o => o.BehaviourCode is not null && o.AssignmentKey is not null && partitionData.TryGetValue(o.AssignmentKey, out var data) && data.AssignmentsByDate.ContainsKey(deadline))
        .DistinctBy(o => o.AssignmentKey, StringComparer.OrdinalIgnoreCase)
        .GroupBy(o => o.BehaviourCode, StringComparer.OrdinalIgnoreCase);

      foreach (var group in completionGroups)
      {
        var progress = group
          .Select(o =>
          {
            var data = partitionData[o.AssignmentKey];
            return GetAssignmentProgress(data.AssignmentsByDate[deadline], data, studentWithClasses.Student.Id);
          })
          .Aggregate(new AssignmentProgressTotals(0, 0), (current, next) => new AssignmentProgressTotals(current.Completed + next.Completed, current.Total + next.Total));
        if (progress.Total <= 0) continue;

        students.Add(new StudentWithCompletion
        {
          BehaviourCode = group.Key,
          Student = studentWithClasses.Student,
          CompletedQuestions = progress.Completed,
          TotalQuestions = progress.Total,
          CompletionRate = progress.Completed * 100d / progress.Total
        });
      }
    }

    return students;
  }

  public async Task<AssignmentsStudentData> GetStudentAssignmentsAsync(User student)
  {
    ArgumentNullException.ThrowIfNull(student);

    var today = DateOnly.FromDateTime(DateTime.UtcNow);
    var classes = ParseClasses(student.Classes);
    if (classes.Count == 0) return new AssignmentsStudentData();

    var coursesByKeyStageAndSubjectCode = (await _courseService.ListCoursesAsync())
      .Where(o => !string.IsNullOrWhiteSpace(o.SubjectCode))
      .GroupBy(o => BuildCourseLookupKey(o.KeyStage, o.SubjectCode), StringComparer.OrdinalIgnoreCase)
      .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
    var visibleDueDates = GetVisibleDueDates(today);
    var assignmentKeys = NormalizeKeys(classes.Select(cls => GetAssignmentKey(cls, coursesByKeyStageAndSubjectCode)).Where(o => o is not null));
    var assignmentsByPartitionTask = LoadAssignmentsByDueDatesAsync(assignmentKeys, visibleDueDates);
    var submissionsByPartitionTask = LoadStudentSubmissionsAsync(assignmentKeys, visibleDueDates, student.Id);
    await Task.WhenAll(assignmentsByPartitionTask, submissionsByPartitionTask);
    var assignmentsByPartition = await assignmentsByPartitionTask;
    var submissionsByPartition = await submissionsByPartitionTask;
    var partitionData = BuildPartitionData(assignmentKeys, assignmentsByPartition, submissionsByPartition);
    var cards = classes
      .SelectMany(cls =>
      {
        var assignmentKey = GetAssignmentKey(cls, coursesByKeyStageAndSubjectCode);
        if (assignmentKey is null || !partitionData.TryGetValue(assignmentKey, out var data)) return [];

        return data.AssignmentsByDate.Values.Select(o => new
        {
          o.DueDate,
          Card = CreateStudentCard(o, data, student.Id,
            coursesByKeyStageAndSubjectCode.GetValueOrDefault(BuildCourseLookupKey(GetKeyStage(o.YearGroup), cls.SubjectCode)))
        });
      })
      .DistinctBy(o => (o.Card.CourseId, o.Card.DueDate))
      .OrderBy(o => o.DueDate)
      .ThenBy(o => o.Card.CourseName, StringComparer.OrdinalIgnoreCase)
      .ToList();

    return new AssignmentsStudentData
    {
      ToDo = cards.Where(o => !o.Card.IsComplete && o.DueDate >= today).Select(o => o.Card).ToList(),
      Past = cards
        .Where(o => o.Card.IsComplete || o.DueDate < today)
        .OrderByDescending(o => o.DueDate)
        .ThenBy(o => o.Card.CourseName, StringComparer.OrdinalIgnoreCase)
        .Select(o => o.Card)
        .ToList()
    };
  }

  public async Task<AssignmentDetailPageData> GetStudentAssignmentDetailAsync(User student, CourseEntity course, int yearGroup, DateOnly dueDate, string className)
  {
    ArgumentNullException.ThrowIfNull(student);
    ArgumentNullException.ThrowIfNull(course);
    ArgumentException.ThrowIfNullOrWhiteSpace(className);

    var context = await LoadStudentAssignmentContextAsync(student, course, yearGroup, dueDate, className);
    if (context is null) return null;

    var currentQuestion = LoadNextQuestion(context.Submission.Progress, context.Questions);
    var totalQuestions = context.Questions.Count;
    var completedQuestions = Math.Min(context.Submission.Completed, totalQuestions);

    return new AssignmentDetailPageData
    {
      CourseId = course.RowKey,
      CourseName = course.Name,
      YearGroup = yearGroup,
      DueDate = context.DueDateText,
      DueDateLabel = FormatLongDate(dueDate),
      CompletedQuestions = completedQuestions,
      TotalQuestions = totalQuestions,
      IsComplete = currentQuestion is null,
      CurrentQuestion = currentQuestion
    };
  }

  public async Task<AssignmentAnswerResponse> SubmitStudentAssignmentAnswerAsync(User student, CourseEntity course, int yearGroup, DateOnly dueDate, string className, AssignmentAnswerRequest request)
  {
    ArgumentNullException.ThrowIfNull(student);
    ArgumentNullException.ThrowIfNull(course);
    ArgumentException.ThrowIfNullOrWhiteSpace(className);
    ArgumentNullException.ThrowIfNull(request);

    if (request.QuestionNumber < 1) throw new ArgumentException("Question number must be at least 1.");
    if (request.Answer is < 0 or > 3) throw new ArgumentException("Answer must be between 0 and 3.");

    var context = await LoadStudentAssignmentContextAsync(student, course, yearGroup, dueDate, className);
    if (context is null) return null;

    var submission = context.Submission;
    EnsureSubmissionDelaySatisfied(submission);
    var entries = ParseProgress(submission.Progress, context.Questions.Count);
    var entry = GetCurrentQueueEntry(entries) ?? throw new InvalidOperationException("This assignment has already been completed.");
    if (request.QuestionNumber != entry.QuestionNumber) throw new InvalidOperationException("This is not the current question.");

    var correctAnswer = GetCorrectAnswerIndex(entry.AnswerOrder);
    var isCorrect = request.Answer == correctAnswer;
    var acceptedAt = DateTimeOffset.UtcNow;
    UpdateQueueAfterAnswer(entries, isCorrect);

    submission.Progress = BuildProgress(entries);
    submission.Completed = entries.Count(item => item.IsCorrect);
    submission.LockedUntil = acceptedAt.Add(isCorrect ? CorrectAnswerDelay : IncorrectAnswerDelay);

    try
    {
      await _submissionsClient.UpdateEntityAsync(submission, submission.ETag, TableUpdateMode.Replace);
    }
    catch (RequestFailedException ex) when (ex.Status == 412)
    {
      throw new InvalidOperationException("The submission was updated by another request. Please try again.", ex);
    }

    return new AssignmentAnswerResponse
    {
      CorrectAnswer = correctAnswer,
      CompletedQuestions = Math.Min(submission.Completed, context.Questions.Count),
      TotalQuestions = context.Questions.Count,
      NextQuestion = LoadNextQuestion(submission.Progress, context.Questions)
    };
  }

  public async Task<AssignmentsStaffData> GetStaffAssignmentsAsync(User teacher)
  {
    ArgumentNullException.ThrowIfNull(teacher);

    var coursesTask = _courseService.ListCoursesAsync();
    var unitsTask = _courseService.ListUnitsAsync();
    await Task.WhenAll(coursesTask, unitsTask);
    var courses = await coursesTask;
    var assignmentCourses = courses
      .Where(o => o.AssignmentLength > 0)
      .OrderBy(o => o.Name, StringComparer.OrdinalIgnoreCase)
      .ToList();
    if (assignmentCourses.Count == 0) return new AssignmentsStaffData();
    var assignmentCourseIds = assignmentCourses
      .Select(o => o.RowKey)
      .ToHashSet(StringComparer.OrdinalIgnoreCase);
    var assignmentCourseYearGroups = (await unitsTask)
      .Where(o => assignmentCourseIds.Contains(o.PartitionKey))
      .GroupBy(o => o.PartitionKey, StringComparer.OrdinalIgnoreCase)
      .ToDictionary(
        g => g.Key,
        g => g.Select(o => o.YearGroup).ToHashSet(),
        StringComparer.OrdinalIgnoreCase);
    var assignmentSubjectCodes = assignmentCourses
      .Select(o => o.SubjectCode)
      .Where(o => !string.IsNullOrWhiteSpace(o))
      .ToHashSet(StringComparer.OrdinalIgnoreCase);
    var coursesByKeyStageAndSubjectCode = BuildCoursesByKeyStageAndSubjectCode(assignmentCourses);

    var teacherClasses = ParseClasses(teacher.Classes)
      .Where(o => assignmentSubjectCodes.Contains(o.SubjectCode))
      .OrderBy(o => o.YearGroup)
      .ThenBy(o => o.SubjectCode, StringComparer.OrdinalIgnoreCase)
      .ThenBy(o => o.Name, StringComparer.OrdinalIgnoreCase)
      .ToList();

    var studentClassesById = _config.Students.ToDictionary(student => student.Id, student => ParseClasses(student.Classes));
    var classRosters = BuildClassRosters(studentClassesById);
    var tutorGroupRosters = BuildTutorGroupRosters();
    var schoolClasses = studentClassesById.Values
      .SelectMany(o => o)
      .Where(o => assignmentSubjectCodes.Contains(o.SubjectCode))
      .DistinctBy(o => o.Name, StringComparer.OrdinalIgnoreCase)
      .OrderBy(o => o.YearGroup)
      .ThenBy(o => o.SubjectCode, StringComparer.OrdinalIgnoreCase)
      .ThenBy(o => o.Name, StringComparer.OrdinalIgnoreCase)
      .ToList();

    var relevantPartitions = teacherClasses
      .Concat(schoolClasses)
      .Select(o => GetAssignmentKey(o, coursesByKeyStageAndSubjectCode))
      .Where(o => o is not null)
      .Distinct(StringComparer.OrdinalIgnoreCase)
      .ToList();

    if (relevantPartitions.Count == 0) return new AssignmentsStaffData();

    var partitionKeys = NormalizeKeys(relevantPartitions);
    var today = DateOnly.FromDateTime(DateTime.UtcNow);
    var visibleDueDates = GetVisibleDueDates(today);
    var upcomingDueDate = visibleDueDates.First();
    var questionDueDate = visibleDueDates.Where(o => o <= today).DefaultIfEmpty(visibleDueDates.Last()).Max();
    var questionsTitle = $"Questions ({FormatShortDate(questionDueDate)})";
    var staffDateColumns = visibleDueDates
      .Take(5)
      .Select(date =>
      {
        var value = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return new StaffDateColumn(date, value, new AssignmentsDateColumn
        {
          Value = value,
          Label = FormatShortDate(date)
        });
      }).ToList();
    var dateColumns = staffDateColumns.Select(o => o.Column).ToList();
    var completionData = await LoadStaffCompletionDataAsync(partitionKeys, visibleDueDates, upcomingDueDate);
    var partitionData = BuildPartitionData(partitionKeys, completionData.Assignments, completionData.Submissions);
    var questionSummariesByContext = await LoadStaffQuestionSummariesAsync(
      partitionKeys,
      questionDueDate,
      upcomingDueDate,
      schoolClasses,
      classRosters,
      assignmentCourses,
      assignmentCourseYearGroups,
      coursesByKeyStageAndSubjectCode,
      partitionData);
    var progressCache = new Dictionary<(string AssignmentKey, DateOnly DueDate, int StudentId), AssignmentProgressTotals>();

    var details = new List<AssignmentsStaffDetail>();
    var classRowsByName = new Dictionary<string, AssignmentsStaffRow>(StringComparer.OrdinalIgnoreCase);
    var classDetailIndex = 0;
    foreach (var cls in schoolClasses)
    {
      var assignmentKey = GetAssignmentKey(cls, coursesByKeyStageAndSubjectCode);
      if (assignmentKey is null || !partitionData.TryGetValue(assignmentKey, out var data)) continue;

      classRosters.TryGetValue(cls.Name, out var roster);
      roster ??= [];
      var studentIds = roster.Select(o => o.Id).ToList();
      var pupilPremiumStudentIds = roster.Where(o => o.PupilPremium).Select(o => o.Id).ToList();
      var cells = staffDateColumns.Select(date => BuildAggregateCell(data, studentIds, date, progressCache, pupilPremiumStudentIds)).ToList();
      if (!cells.Any(o => o.HasAssignment)) continue;

      var detailId = $"class-{++classDetailIndex}";

      classRowsByName[cls.Name] = new AssignmentsStaffRow
      {
        Title = cls.Name,
        DetailId = detailId,
        Cells = cells
      };

      details.Add(new AssignmentsStaffDetail
      {
        Id = detailId,
        Title = cls.Name,
        FirstColumnTitle = "Student",
        Rows = BuildClassStudentRows(roster, data, staffDateColumns, progressCache),
        QuestionsTitle = questionsTitle,
        Questions = GetQuestionSummaries(questionSummariesByContext, BuildClassQuestionCacheKey(cls.Name))
      });
    }

    var classRows = teacherClasses
      .Where(o => classRowsByName.ContainsKey(o.Name))
      .Select(o => classRowsByName[o.Name])
      .ToList();

    var yearGroupRows = new List<AssignmentsStaffRow>();
    var yearGroupDetailIndex = 0;
    var tutorGroupDetailIndex = 0;
    foreach (var yearGroup in tutorGroupRosters
      .Where(o => GetYearGroup(o.Key) > 0)
      .GroupBy(o => GetYearGroup(o.Key))
      .OrderBy(o => o.Key))
    {
      var yearStudents = yearGroup
        .SelectMany(o => o.Value)
        .DistinctBy(o => o.Id)
        .ToList();
      var yearStudentIdsByPartition = BuildStudentIdsByPartition(yearStudents, coursesByKeyStageAndSubjectCode, partitionData, studentClassesById, yearGroup.Key);
      var yearPupilPremiumStudentIdsByPartition = BuildStudentIdsByPartition(yearStudents.Where(o => o.PupilPremium), coursesByKeyStageAndSubjectCode, partitionData, studentClassesById, yearGroup.Key);
      if (yearStudentIdsByPartition.Count == 0) continue;

      var yearCells = staffDateColumns.Select(date => BuildAggregateCell(partitionData, yearStudentIdsByPartition, date, progressCache, yearPupilPremiumStudentIdsByPartition)).ToList();
      if (!yearCells.Any(o => o.HasAssignment)) continue;

      var tutorGroupRows = new List<AssignmentsStaffRow>();
      foreach (var tutorGroup in yearGroup.OrderBy(o => o.Key, StringComparer.OrdinalIgnoreCase))
      {
        var tutorStudents = tutorGroup.Value;
        var tutorStudentIdsByPartition = BuildStudentIdsByPartition(tutorStudents, coursesByKeyStageAndSubjectCode, partitionData, studentClassesById, yearGroup.Key);
        var tutorPupilPremiumStudentIdsByPartition = BuildStudentIdsByPartition(tutorStudents.Where(o => o.PupilPremium), coursesByKeyStageAndSubjectCode, partitionData, studentClassesById, yearGroup.Key);
        if (tutorStudentIdsByPartition.Count == 0) continue;

        var tutorCells = staffDateColumns.Select(date => BuildAggregateCell(partitionData, tutorStudentIdsByPartition, date, progressCache, tutorPupilPremiumStudentIdsByPartition)).ToList();
        if (!tutorCells.Any(o => o.HasAssignment)) continue;

        var tutorDetailId = $"tutor-{++tutorGroupDetailIndex}";
        tutorGroupRows.Add(new AssignmentsStaffRow
        {
          Title = tutorGroup.Key,
          DetailId = tutorDetailId,
          Cells = tutorCells
        });

        details.Add(new AssignmentsStaffDetail
        {
          Id = tutorDetailId,
          Title = tutorGroup.Key,
          FirstColumnTitle = "Student",
          Rows = BuildAggregateStudentRows(tutorStudents, partitionData, staffDateColumns, coursesByKeyStageAndSubjectCode, studentClassesById, progressCache, yearGroup.Key)
        });
      }

      if (tutorGroupRows.Count == 0) continue;

      var yearDetailId = $"year-{++yearGroupDetailIndex}";
      yearGroupRows.Add(new AssignmentsStaffRow
      {
        Title = $"Year {yearGroup.Key}",
        DetailId = yearDetailId,
        Cells = yearCells
      });

      details.Add(new AssignmentsStaffDetail
      {
        Id = yearDetailId,
        Title = $"Year {yearGroup.Key}",
        FirstColumnTitle = "Tutor Group",
        ClickableRows = true,
        Rows = tutorGroupRows
      });
    }

    var courseRows = new List<AssignmentsStaffRow>();
    var courseDetailIndex = 0;
    foreach (var course in assignmentCourses)
    {
      if (!assignmentCourseYearGroups.TryGetValue(course.RowKey, out var courseYearGroups) || courseYearGroups.Count == 0) continue;

      foreach (var yearGroup in courseYearGroups.Order())
      {
        var partitionKey = BuildAssignmentRowKey(yearGroup, course.RowKey);
        if (!partitionData.TryGetValue(partitionKey, out var data)) continue;

        var courseClasses = schoolClasses
          .Where(o => o.YearGroup == yearGroup && o.SubjectCode.Equals(course.SubjectCode, StringComparison.OrdinalIgnoreCase))
          .Where(o => classRowsByName.ContainsKey(o.Name))
          .ToList();
        if (courseClasses.Count == 0) continue;

        var students = courseClasses
          .SelectMany(cls => classRosters.TryGetValue(cls.Name, out var roster) ? roster : [])
          .DistinctBy(o => o.Id)
          .ToList();
        var studentIds = students.Select(o => o.Id).ToList();
        var pupilPremiumStudentIds = students.Where(o => o.PupilPremium).Select(o => o.Id).ToList();
        var cells = staffDateColumns.Select(date => BuildAggregateCell(data, studentIds, date, progressCache, pupilPremiumStudentIds)).ToList();
        if (!cells.Any(o => o.HasAssignment)) continue;

        var title = $"{course.Name} – Year {yearGroup}";
        var detailId = $"course-{++courseDetailIndex}";
        courseRows.Add(new AssignmentsStaffRow
        {
          Title = title,
          DetailId = detailId,
          Cells = cells
        });

        details.Add(new AssignmentsStaffDetail
        {
          Id = detailId,
          Title = title,
          FirstColumnTitle = "Class",
          ClickableRows = true,
          Rows = courseClasses.Select(o => classRowsByName[o.Name]).ToList(),
          QuestionsTitle = questionsTitle,
          Questions = GetQuestionSummaries(questionSummariesByContext, BuildCourseQuestionCacheKey(partitionKey))
        });
      }
    }

    return new AssignmentsStaffData
    {
      Dates = dateColumns,
      Classes = classRows,
      YearGroups = yearGroupRows,
      Courses = courseRows,
      Details = details
    };
  }

  public async Task<WeeklyCompletionReports> GetWeeklyCompletionReportsAsync(DateOnly dueDate)
  {
    var reports = new WeeklyCompletionReports
    {
      DueDate = dueDate,
      DueDateLabel = FormatLongDate(dueDate)
    };

    var assignmentCourses = (await _courseService.ListCoursesAsync())
      .Where(o => o.AssignmentLength > 0 && !string.IsNullOrWhiteSpace(o.SubjectCode))
      .ToList();
    var assignmentSubjectCodes = assignmentCourses
      .Select(o => o.SubjectCode)
      .ToHashSet(StringComparer.OrdinalIgnoreCase);
    if (assignmentSubjectCodes.Count == 0) return reports;

    var coursesByKeyStageAndSubjectCode = assignmentCourses
      .GroupBy(o => BuildCourseLookupKey(o.KeyStage, o.SubjectCode), StringComparer.OrdinalIgnoreCase)
      .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
    var classRosters = BuildClassRosters();
    var tutorGroupRosters = BuildTutorGroupRosters();
    var schoolClasses = ParseClasses(classRosters.Keys)
      .Where(o => assignmentSubjectCodes.Contains(o.SubjectCode))
      .ToList();
    var partitionKeys = NormalizeKeys(schoolClasses.Select(o => GetAssignmentKey(o, coursesByKeyStageAndSubjectCode)).Where(o => o is not null));
    if (partitionKeys.Count == 0) return reports;

    var assignmentsByPartition = await LoadAssignmentsByDueDateAsync(partitionKeys, dueDate);
    if (assignmentsByPartition.Values.All(o => o.Count == 0)) return reports;

    var submissionsByPartition = await LoadSubmissionsByDueDateAsync(partitionKeys, dueDate);

    var partitionData = BuildPartitionData(partitionKeys, assignmentsByPartition, submissionsByPartition);
    var dueDateText = dueDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    var classReportsByName = new Dictionary<string, ClassCompletionReport>(StringComparer.OrdinalIgnoreCase);

    foreach (var cls in schoolClasses)
    {
      var assignmentKey = GetAssignmentKey(cls, coursesByKeyStageAndSubjectCode);
      if (assignmentKey is null || !partitionData.TryGetValue(assignmentKey, out var data) || !data.AssignmentsByDate.ContainsKey(dueDate)) continue;

      classRosters.TryGetValue(cls.Name, out var roster);
      roster ??= [];
      var students = BuildCompletionStudentRows(roster, student => BuildStudentCell(data, student.Id, dueDateText));
      var totalQuestions = students.Sum(o => o.TotalQuestions);
      if (totalQuestions <= 0) continue;

      classReportsByName[cls.Name] = new ClassCompletionReport
      {
        ClassName = cls.Name,
        CourseName = coursesByKeyStageAndSubjectCode.TryGetValue(BuildCourseLookupKey(GetKeyStage(cls.YearGroup), cls.SubjectCode), out var course)
          ? course.Name
          : cls.SubjectCode,
        CompletedQuestions = students.Sum(o => o.CompletedQuestions),
        TotalQuestions = totalQuestions,
        CompletionPercentage = GetCompletionPercentage(students.Sum(o => o.CompletedQuestions), totalQuestions),
        Students = students
      };
    }

    reports.Teachers = _config.Teachers
      .OrderBy(o => o.LastName, StringComparer.OrdinalIgnoreCase)
      .ThenBy(o => o.FirstName, StringComparer.OrdinalIgnoreCase)
      .Select(teacher => new TeacherCompletionReport
      {
        Teacher = teacher,
        DueDateLabel = reports.DueDateLabel,
        Classes = ParseClasses(teacher.Classes)
          .Select(cls => classReportsByName.GetValueOrDefault(cls.Name))
          .Where(o => o is not null)
          .DistinctBy(o => o.ClassName, StringComparer.OrdinalIgnoreCase)
          .OrderBy(o => o.ClassName, StringComparer.OrdinalIgnoreCase)
          .ToList()
      })
      .Where(o => o.Classes.Count > 0)
      .ToList();

    var tutorGroupRowsByYear = tutorGroupRosters
      .Select(tutorGroup =>
      {
        var studentIdsByPartition = BuildStudentIdsByPartition(tutorGroup.Value, coursesByKeyStageAndSubjectCode, partitionData);
        var cell = BuildAggregateCell(partitionData, studentIdsByPartition, dueDateText);
        return new TutorGroupCompletionRow
        {
          TutorGroup = tutorGroup.Key,
          CompletedQuestions = cell.Completed,
          TotalQuestions = cell.Total,
          CompletionPercentage = GetCompletionPercentage(cell.Completed, cell.Total)
        };
      })
      .Where(o => o.TotalQuestions > 0 && GetYearGroup(o.TutorGroup) > 0)
      .GroupBy(o => GetYearGroup(o.TutorGroup))
      .ToDictionary(
        g => g.Key,
        g => g
          .OrderByDescending(o => o.CompletionPercentage)
          .ThenBy(o => o.TutorGroup, StringComparer.OrdinalIgnoreCase)
          .Select((row, index) =>
          {
            row.Rank = index + 1;
            return row;
          })
          .ToList());

    foreach (var tutor in _config.Teachers
      .Where(o => !string.IsNullOrWhiteSpace(o.TutorGroup))
      .OrderBy(o => o.LastName, StringComparer.OrdinalIgnoreCase)
      .ThenBy(o => o.FirstName, StringComparer.OrdinalIgnoreCase))
    {
      var tutorGroup = tutor.TutorGroup.Trim();
      if (!tutorGroupRosters.TryGetValue(tutorGroup, out var roster)) continue;

      var students = BuildCompletionStudentRows(roster, student =>
      {
        var partitionKeysForStudent = GetStudentPartitionKeys(student, coursesByKeyStageAndSubjectCode, partitionData);
        return BuildAggregateStudentCell(partitionData, partitionKeysForStudent, student.Id, dueDateText);
      });
      var totalQuestions = students.Sum(o => o.TotalQuestions);
      if (totalQuestions <= 0) continue;

      var yearGroup = GetYearGroup(tutorGroup);
      var tutorGroupLeaderboard = tutorGroupRowsByYear.GetValueOrDefault(yearGroup)?
        .Select(o => new TutorGroupCompletionRow
        {
          Rank = o.Rank,
          TutorGroup = o.TutorGroup,
          CompletedQuestions = o.CompletedQuestions,
          TotalQuestions = o.TotalQuestions,
          CompletionPercentage = o.CompletionPercentage,
          IsCurrentTutorGroup = o.TutorGroup.Equals(tutorGroup, StringComparison.OrdinalIgnoreCase)
        })
        .ToList() ?? [];

      reports.Tutors.Add(new TutorCompletionReport
      {
        Tutor = tutor,
        DueDateLabel = reports.DueDateLabel,
        TutorGroup = tutorGroup,
        CompletedQuestions = students.Sum(o => o.CompletedQuestions),
        TotalQuestions = totalQuestions,
        CompletionPercentage = GetCompletionPercentage(students.Sum(o => o.CompletedQuestions), totalQuestions),
        Students = students,
        TutorGroups = tutorGroupLeaderboard
      });
    }

    return reports;
  }

  private static List<string> NormalizeKeys(IEnumerable<string> keys)
  {
    return keys
      .Where(o => !string.IsNullOrWhiteSpace(o))
      .Select(o => o.Trim())
      .Distinct(StringComparer.OrdinalIgnoreCase)
      .ToList();
  }

  private List<DateOnly> GetVisibleDueDates(DateOnly today)
  {
    var upcomingDueDate = ResolveDueDate(GetNextMonday(today));
    var dueDates = new List<DateOnly> { upcomingDueDate };
    var candidateDueDate = upcomingDueDate.AddDays(-7);

    while (dueDates.Count < 5)
    {
      if (ResolveDueDate(candidateDueDate) == candidateDueDate)
      {
        dueDates.Add(candidateDueDate);
      }

      candidateDueDate = candidateDueDate.AddDays(-7);
    }

    return dueDates.OrderByDescending(o => o).ToList();
  }

  private static DateOnly GetNextMonday(DateOnly date)
  {
    var daysUntilMonday = ((int)DayOfWeek.Monday - (int)date.DayOfWeek + 7) % 7;
    if (daysUntilMonday == 0) daysUntilMonday = 7;
    return date.AddDays(daysUntilMonday);
  }

  private async Task<(Dictionary<string, List<AssignmentEntity>> Assignments, Dictionary<string, List<AssignmentSubmissionEntity>> Submissions)> LoadStaffCompletionDataAsync(
    List<string> assignmentKeys,
    IEnumerable<DateOnly> dueDates,
    DateOnly upcomingDueDate)
  {
    var assignments = CreateBuckets<AssignmentEntity>(assignmentKeys);
    var submissions = CreateBuckets<AssignmentSubmissionEntity>(assignmentKeys);
    var results = await Task.WhenAll(dueDates.Distinct().Select(dueDate => LoadStaffCompletionDateDataAsync(assignmentKeys, dueDate, upcomingDueDate)));

    foreach (var result in results.OrderBy(o => o.DueDate))
    {
      AddBuckets(assignments, result.Assignments);
      AddBuckets(submissions, result.Submissions);
    }

    return (assignments, submissions);
  }

  private async Task<(DateOnly DueDate, Dictionary<string, List<AssignmentEntity>> Assignments, Dictionary<string, List<AssignmentSubmissionEntity>> Submissions)> LoadStaffCompletionDateDataAsync(
    List<string> assignmentKeys,
    DateOnly dueDate,
    DateOnly upcomingDueDate)
  {
    var cached = dueDate == upcomingDueDate ? null : await TryDownloadAssignmentCacheAsync<AssignmentCompletionCache>(BuildCompletionCacheBlobName(dueDate));
    if (cached is not null)
    {
      var assignments = CreateBuckets<AssignmentEntity>(assignmentKeys);
      var submissions = CreateBuckets<AssignmentSubmissionEntity>(assignmentKeys);
      AddCompletionCacheToBuckets(cached, assignmentKeys, assignments, submissions);
      return (dueDate, assignments, submissions);
    }

    var assignmentsTask = LoadAssignmentsByDueDateAsync(assignmentKeys, dueDate);
    var submissionsTask = LoadSubmissionsByDueDateAsync(assignmentKeys, dueDate);
    await Task.WhenAll(assignmentsTask, submissionsTask);
    var dateAssignments = await assignmentsTask;
    var dateSubmissions = await submissionsTask;
    if (dueDate != upcomingDueDate)
    {
      await UploadAssignmentCacheAsync(BuildCompletionCacheBlobName(dueDate), BuildCompletionCache(dueDate, dateAssignments, dateSubmissions));
    }

    return (dueDate, dateAssignments, dateSubmissions);
  }

  private async Task<Dictionary<string, List<AssignmentsStaffQuestion>>> LoadStaffQuestionSummariesAsync(
    List<string> partitionKeys,
    DateOnly dueDate,
    DateOnly upcomingDueDate,
    IReadOnlyList<ParsedClass> schoolClasses,
    IReadOnlyDictionary<string, List<User>> classRosters,
    IReadOnlyList<CourseEntity> assignmentCourses,
    IReadOnlyDictionary<string, HashSet<int>> assignmentCourseYearGroups,
    IReadOnlyDictionary<string, CourseEntity> coursesByKeyStageAndSubjectCode,
    IReadOnlyDictionary<string, PartitionAssignmentData> partitionData)
  {
    var cached = dueDate == upcomingDueDate ? null : await TryDownloadAssignmentCacheAsync<AssignmentQuestionsCache>(BuildQuestionsCacheBlobName(dueDate));
    if (cached is not null)
    {
      return new Dictionary<string, List<AssignmentsStaffQuestion>>(cached.Contexts, StringComparer.OrdinalIgnoreCase);
    }

    var liveCache = await BuildQuestionsCacheAsync(
      partitionKeys,
      dueDate,
      schoolClasses,
      classRosters,
      assignmentCourses,
      assignmentCourseYearGroups,
      coursesByKeyStageAndSubjectCode,
      partitionData);

    if (dueDate != upcomingDueDate)
    {
      await UploadAssignmentCacheAsync(BuildQuestionsCacheBlobName(dueDate), liveCache);
    }

    return new Dictionary<string, List<AssignmentsStaffQuestion>>(liveCache.Contexts, StringComparer.OrdinalIgnoreCase);
  }

  private async Task<AssignmentQuestionsCache> BuildQuestionsCacheAsync(
    List<string> partitionKeys,
    DateOnly dueDate,
    IReadOnlyList<ParsedClass> schoolClasses,
    IReadOnlyDictionary<string, List<User>> classRosters,
    IReadOnlyList<CourseEntity> assignmentCourses,
    IReadOnlyDictionary<string, HashSet<int>> assignmentCourseYearGroups,
    IReadOnlyDictionary<string, CourseEntity> coursesByKeyStageAndSubjectCode,
    IReadOnlyDictionary<string, PartitionAssignmentData> partitionData)
  {
    var questionsByPartitionTask = LoadQuestionsByDueDateAsync(partitionKeys, dueDate);
    var submissionsByPartitionTask = LoadSubmissionsByDueDatesAsync(partitionKeys, [dueDate], true);
    await Task.WhenAll(questionsByPartitionTask, submissionsByPartitionTask);
    var questionsByPartition = await questionsByPartitionTask;
    var submissionsByPartition = await submissionsByPartitionTask;
    var assignmentsByPartition = CreateBuckets<AssignmentEntity>(partitionKeys);

    foreach (var partitionKey in partitionKeys)
    {
      if (partitionData.TryGetValue(partitionKey, out var data) && data.AssignmentsByDate.TryGetValue(dueDate, out var assignment))
      {
        assignmentsByPartition[partitionKey].Add(assignment);
      }
    }

    var questionData = BuildPartitionData(partitionKeys, assignmentsByPartition, submissionsByPartition, questionsByPartition);
    var contexts = BuildQuestionContexts(schoolClasses, classRosters, assignmentCourses, assignmentCourseYearGroups, coursesByKeyStageAndSubjectCode, partitionData, dueDate);
    var progressCache = new Dictionary<(string AssignmentKey, DateOnly DueDate, int StudentId), List<AssignmentProgressEntry>>();
    var generatedAt = RoundedNow();
    var cache = new AssignmentQuestionsCache
    {
      DueDate = FormatDate(dueDate),
      GeneratedAtUtc = generatedAt,
      ExpiresAtUtc = generatedAt.Add(StaffAssignmentCacheLifetime)
    };

    foreach (var context in contexts)
    {
      cache.Contexts[context.Key] = questionData.TryGetValue(context.AssignmentKey, out var data)
        ? BuildQuestionSummaries(data, context.StudentIds, dueDate, progressCache)
        : [];
    }

    return cache;
  }

  private static List<AssignmentQuestionContext> BuildQuestionContexts(
    IReadOnlyList<ParsedClass> schoolClasses,
    IReadOnlyDictionary<string, List<User>> classRosters,
    IReadOnlyList<CourseEntity> assignmentCourses,
    IReadOnlyDictionary<string, HashSet<int>> assignmentCourseYearGroups,
    IReadOnlyDictionary<string, CourseEntity> coursesByKeyStageAndSubjectCode,
    IReadOnlyDictionary<string, PartitionAssignmentData> partitionData,
    DateOnly dueDate)
  {
    var contexts = new List<AssignmentQuestionContext>();

    foreach (var cls in schoolClasses)
    {
      var assignmentKey = GetAssignmentKey(cls, coursesByKeyStageAndSubjectCode);
      if (assignmentKey is null
        || !partitionData.TryGetValue(assignmentKey, out var data)
        || !data.AssignmentsByDate.ContainsKey(dueDate)) continue;

      classRosters.TryGetValue(cls.Name, out var roster);
      roster ??= [];
      contexts.Add(new AssignmentQuestionContext(BuildClassQuestionCacheKey(cls.Name), assignmentKey, roster.Select(o => o.Id).ToList()));
    }

    foreach (var course in assignmentCourses)
    {
      if (!assignmentCourseYearGroups.TryGetValue(course.RowKey, out var courseYearGroups) || courseYearGroups.Count == 0) continue;

      foreach (var yearGroup in courseYearGroups.Order())
      {
        var partitionKey = BuildAssignmentRowKey(yearGroup, course.RowKey);
        if (!partitionData.TryGetValue(partitionKey, out var data) || !data.AssignmentsByDate.ContainsKey(dueDate)) continue;

        var studentIds = schoolClasses
          .Where(o => o.YearGroup == yearGroup && o.SubjectCode.Equals(course.SubjectCode, StringComparison.OrdinalIgnoreCase))
          .SelectMany(cls => classRosters.TryGetValue(cls.Name, out var roster) ? roster : [])
          .Select(o => o.Id)
          .Distinct()
          .ToList();
        contexts.Add(new AssignmentQuestionContext(BuildCourseQuestionCacheKey(partitionKey), partitionKey, studentIds));
      }
    }

    return contexts
      .GroupBy(o => o.Key, StringComparer.OrdinalIgnoreCase)
      .Select(o => o.First())
      .ToList();
  }

  private static AssignmentCompletionCache BuildCompletionCache(
    DateOnly dueDate,
    Dictionary<string, List<AssignmentEntity>> assignmentsByPartition,
    Dictionary<string, List<AssignmentSubmissionEntity>> submissionsByPartition)
  {
    var generatedAt = RoundedNow();
    var cache = new AssignmentCompletionCache
    {
      DueDate = FormatDate(dueDate),
      GeneratedAtUtc = generatedAt,
      ExpiresAtUtc = generatedAt.Add(StaffAssignmentCacheLifetime)
    };

    foreach (var assignment in assignmentsByPartition.Values.SelectMany(o => o).OrderBy(o => o.RowKey, StringComparer.OrdinalIgnoreCase))
    {
      submissionsByPartition.TryGetValue(assignment.RowKey, out var submissions);
      cache.Assignments.Add(new AssignmentCompletionCacheItem
      {
        AssignmentKey = assignment.RowKey,
        Length = assignment.Length,
        Students = (submissions ?? [])
          .OrderBy(o => o.StudentId)
          .Select(o => new AssignmentCompletionStudentCacheItem
          {
            StudentId = o.StudentId,
            Completed = o.Completed
          })
          .ToList()
      });
    }

    return cache;
  }

  private static void AddCompletionCacheToBuckets(
    AssignmentCompletionCache cache,
    List<string> assignmentKeys,
    Dictionary<string, List<AssignmentEntity>> assignments,
    Dictionary<string, List<AssignmentSubmissionEntity>> submissions)
  {
    var allowedKeys = assignmentKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);
    foreach (var item in cache.Assignments.Where(o => allowedKeys.Contains(o.AssignmentKey)))
    {
      var assignment = new AssignmentEntity
      {
        PartitionKey = cache.DueDate,
        RowKey = item.AssignmentKey,
        Length = item.Length
      };
      assignments[item.AssignmentKey].Add(assignment);

      foreach (var student in item.Students)
      {
        submissions[item.AssignmentKey].Add(new AssignmentSubmissionEntity
        {
          PartitionKey = cache.DueDate,
          RowKey = BuildSubmissionRowKey(student.StudentId, assignment.YearGroup, assignment.CourseId),
          Completed = student.Completed
        });
      }
    }
  }

  private async Task<T> TryDownloadAssignmentCacheAsync<T>(string blobName) where T : AssignmentCacheFile
  {
    try
    {
      var response = await _cacheClient.GetBlobClient(blobName).DownloadContentAsync();
      var cache = JsonSerializer.Deserialize<T>(response.Value.Content.ToString(), JsonDefaults.CamelCase);
      return cache is not null && cache.ExpiresAtUtc > DateTimeOffset.UtcNow ? cache : null;
    }
    catch (RequestFailedException ex) when (ex.Status == 404)
    {
      return null;
    }
    catch (JsonException)
    {
      return null;
    }
  }

  private async Task UploadAssignmentCacheAsync<T>(string blobName, T cache) where T : AssignmentCacheFile
  {
    var blobClient = _cacheClient.GetBlobClient(blobName);
    var binaryData = new BinaryData(JsonSerializer.Serialize(cache, JsonDefaults.CamelCase));
    await blobClient.UploadAsync(binaryData, overwrite: true);
  }

  private static void AddBuckets<T>(Dictionary<string, List<T>> target, Dictionary<string, List<T>> source)
  {
    foreach (var item in source)
    {
      if (target.TryGetValue(item.Key, out var bucket))
      {
        bucket.AddRange(item.Value);
      }
    }
  }

  private static List<AssignmentsStaffQuestion> GetQuestionSummaries(Dictionary<string, List<AssignmentsStaffQuestion>> summariesByContext, string key)
    => summariesByContext.TryGetValue(key, out var summaries) ? summaries : [];

  private static string BuildCompletionCacheBlobName(DateOnly dueDate) => $"{FormatDate(dueDate)}-completion.json";

  private static string BuildQuestionsCacheBlobName(DateOnly dueDate) => $"{FormatDate(dueDate)}-questions.json";

  private static string BuildClassQuestionCacheKey(string className) => $"class:{className}";

  private static string BuildCourseQuestionCacheKey(string assignmentKey) => $"course:{assignmentKey}";

  private static DateTimeOffset RoundedNow()
  {
    var now = DateTimeOffset.UtcNow;
    return now.AddTicks(-now.Ticks % TimeSpan.TicksPerSecond);
  }

  private async Task<Dictionary<string, List<AssignmentEntity>>> LoadAssignmentsByDueDateAsync(List<string> assignmentKeys, DateOnly dueDate)
    => await LoadAssignmentsByDueDatesAsync(assignmentKeys, [dueDate]);

  private async Task<Dictionary<string, List<AssignmentEntity>>> LoadAssignmentsByDueDatesAsync(List<string> assignmentKeys, IEnumerable<DateOnly> dueDates)
  {
    var buckets = CreateBuckets<AssignmentEntity>(assignmentKeys);
    foreach (var dueDate in dueDates.Distinct().OrderBy(o => o))
    {
      await AddQueryResultsAsync(
        _assignmentsClient,
        BuildPartitionKeyFilter(FormatDate(dueDate)),
        ["PartitionKey", "RowKey", "Length"],
        buckets,
        entity => entity.RowKey);
    }

    return buckets;
  }

  private async Task<Dictionary<string, List<AssignmentSubmissionEntity>>> LoadStudentSubmissionsAsync(List<string> assignmentKeys, IEnumerable<DateOnly> dueDates, int studentId)
  {
    var buckets = CreateBuckets<AssignmentSubmissionEntity>(assignmentKeys);
    var prefix = BuildStudentSubmissionRowKeyPrefix(studentId);
    foreach (var dueDate in dueDates.Distinct().OrderBy(o => o))
    {
      await AddQueryResultsAsync(
        _submissionsClient,
        $"{BuildPartitionKeyFilter(FormatDate(dueDate))} and {BuildRowKeyPrefixFilter(prefix)}",
        ["PartitionKey", "RowKey", "Completed"],
        buckets,
        entity => BuildAssignmentRowKey(entity.YearGroup, entity.CourseId));
    }

    return buckets;
  }

  private async Task<Dictionary<string, List<AssignmentSubmissionEntity>>> LoadSubmissionsByDueDateAsync(List<string> assignmentKeys, DateOnly dueDate)
    => await LoadSubmissionsByDueDatesAsync(assignmentKeys, [dueDate]);

  private async Task<Dictionary<string, List<AssignmentSubmissionEntity>>> LoadSubmissionsByDueDatesAsync(List<string> assignmentKeys, IEnumerable<DateOnly> dueDates, bool includeProgress = false)
  {
    var buckets = CreateBuckets<AssignmentSubmissionEntity>(assignmentKeys);
    foreach (var dueDate in dueDates.Distinct().OrderBy(o => o))
    {
      await AddQueryResultsAsync(
        _submissionsClient,
        BuildPartitionKeyFilter(FormatDate(dueDate)),
        includeProgress ? ["PartitionKey", "RowKey", "Completed", "Progress"] : ["PartitionKey", "RowKey", "Completed"],
        buckets,
        entity => BuildAssignmentRowKey(entity.YearGroup, entity.CourseId));
    }

    return buckets;
  }

  private async Task<Dictionary<string, List<AssignmentQuestionEntity>>> LoadQuestionsByDueDateAsync(List<string> assignmentKeys, DateOnly dueDate)
  {
    var buckets = CreateBuckets<AssignmentQuestionEntity>(assignmentKeys);
    await AddQueryResultsAsync(
      _questionsClient,
      BuildPartitionKeyFilter(FormatDate(dueDate)),
      ["PartitionKey", "RowKey", "Question", "CorrectAnswer", "IncorrectAnswer1", "IncorrectAnswer2", "IncorrectAnswer3", "UnitTitle"],
      buckets,
      entity => BuildAssignmentRowKey(entity.YearGroup, entity.CourseId));
    return buckets;
  }

  private static Dictionary<string, PartitionAssignmentData> BuildPartitionData(
    List<string> partitionKeys,
    Dictionary<string, List<AssignmentEntity>> assignmentsByPartition,
    Dictionary<string, List<AssignmentSubmissionEntity>> submissionsByPartition,
    Dictionary<string, List<AssignmentQuestionEntity>> questionsByPartition = null)
  {
    var results = new Dictionary<string, PartitionAssignmentData>(partitionKeys.Count, StringComparer.OrdinalIgnoreCase);

    foreach (var partitionKey in partitionKeys)
    {
      assignmentsByPartition.TryGetValue(partitionKey, out var assignments);
      submissionsByPartition.TryGetValue(partitionKey, out var submissions);
      List<AssignmentQuestionEntity> questions = null;
      questionsByPartition?.TryGetValue(partitionKey, out questions);
      assignments ??= [];
      submissions ??= [];
      questions ??= [];

      results[partitionKey] = new PartitionAssignmentData
      {
        AssignmentsByDate = assignments
          .OrderBy(o => o.DueDate)
          .ToDictionary(o => o.DueDate, o => o),
        SubmissionsByStudentAndDate = submissions.ToDictionary(o => (o.DueDate, o.StudentId), o => o),
        QuestionsByDate = questions
          .GroupBy(o => o.DueDate)
          .ToDictionary(
            g => g.Key,
            g => g.OrderBy(o => o.QuestionNumber).ToList())
      };
    }

    return results;
  }

  private static async Task AddQueryResultsAsync<T>(TableClient client, string filter, IEnumerable<string> select, Dictionary<string, List<T>> buckets, Func<T, string> getBucketKey)
    where T : class, ITableEntity, new()
  {
    await foreach (var entity in client.QueryAsync<T>(filter: filter, select: select))
    {
      var bucketKey = getBucketKey(entity);
      if (!string.IsNullOrEmpty(bucketKey) && buckets.TryGetValue(bucketKey, out var bucket))
      {
        bucket.Add(entity);
      }
    }
  }

  private static Dictionary<string, List<T>> CreateBuckets<T>(IReadOnlyList<string> partitionKeys)
  {
    return partitionKeys.ToDictionary(o => o, _ => new List<T>(), StringComparer.OrdinalIgnoreCase);
  }

  private static string BuildPartitionKeyFilter(string partitionKey) => $"PartitionKey eq '{EscapeODataValue(partitionKey)}'";

  private static string BuildPartitionKeyLessThanFilter(string partitionKeyExclusiveUpperBound) => $"PartitionKey lt '{EscapeODataValue(partitionKeyExclusiveUpperBound)}'";

  private static string BuildRowKeyPrefixFilter(string prefix) => $"RowKey ge '{EscapeODataValue(prefix)}' and RowKey lt '{EscapeODataValue(prefix + "~")}'";

  private static string EscapeODataValue(string value) => value.Replace("'", "''", StringComparison.Ordinal);

  private static AssignmentsStudentCard CreateStudentCard(AssignmentEntity assignment, PartitionAssignmentData data, int studentId, CourseEntity course)
  {
    var progress = GetAssignmentProgress(assignment, data, studentId);
    var courseId = course?.RowKey ?? assignment.CourseId;
    var courseName = course?.Name ?? assignment.CourseId;

    return new AssignmentsStudentCard
    {
      CourseId = courseId,
      CourseName = courseName,
      YearGroup = assignment.YearGroup,
      DueDate = assignment.DueDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
      DueDateLabel = FormatShortDate(assignment.DueDate),
      Completed = progress.Completed,
      TotalQuestions = progress.Total,
      IsComplete = progress.Total > 0 && progress.Completed >= progress.Total,
      Href = $"/assignments/{Uri.EscapeDataString(courseId)}/year-{assignment.YearGroup}/{assignment.DueDate:yyyy-MM-dd}"
    };
  }

  private static AssignmentsProgressCell BuildAggregateCell(PartitionAssignmentData data, IEnumerable<int> studentIds, string dueDate, IEnumerable<int> pupilPremiumStudentIds = null)
  {
    if (string.IsNullOrWhiteSpace(dueDate)) return new AssignmentsProgressCell();
    var date = DateOnly.ParseExact(dueDate, "yyyy-MM-dd");
    if (!data.AssignmentsByDate.TryGetValue(date, out var assignment)) return new AssignmentsProgressCell { DueDate = dueDate };

    var completed = 0;
    var total = 0;
    foreach (var studentId in studentIds)
    {
      var progress = GetAssignmentProgress(assignment, data, studentId);
      completed += progress.Completed;
      total += progress.Total;
    }

    var pupilPremiumCompleted = 0;
    var pupilPremiumTotal = 0;
    if (pupilPremiumStudentIds is not null)
    {
      foreach (var studentId in pupilPremiumStudentIds)
      {
        var progress = GetAssignmentProgress(assignment, data, studentId);
        pupilPremiumCompleted += progress.Completed;
        pupilPremiumTotal += progress.Total;
      }
    }

    return new AssignmentsProgressCell
    {
      DueDate = dueDate,
      HasAssignment = true,
      Completed = completed,
      Total = total,
      PupilPremiumCompleted = pupilPremiumCompleted,
      PupilPremiumTotal = pupilPremiumTotal
    };
  }

  private static AssignmentsProgressCell BuildAggregateCell(
    PartitionAssignmentData data,
    IReadOnlyList<int> studentIds,
    StaffDateColumn date,
    Dictionary<(string AssignmentKey, DateOnly DueDate, int StudentId), AssignmentProgressTotals> progressCache,
    IReadOnlyList<int> pupilPremiumStudentIds = null)
  {
    if (!data.AssignmentsByDate.TryGetValue(date.DueDate, out var assignment)) return new AssignmentsProgressCell { DueDate = date.Value };

    var completed = 0;
    var total = 0;
    foreach (var studentId in studentIds)
    {
      var progress = GetAssignmentProgress(assignment, data, studentId, progressCache);
      completed += progress.Completed;
      total += progress.Total;
    }

    var pupilPremiumCompleted = 0;
    var pupilPremiumTotal = 0;
    if (pupilPremiumStudentIds is not null)
    {
      foreach (var studentId in pupilPremiumStudentIds)
      {
        var progress = GetAssignmentProgress(assignment, data, studentId, progressCache);
        pupilPremiumCompleted += progress.Completed;
        pupilPremiumTotal += progress.Total;
      }
    }

    return new AssignmentsProgressCell
    {
      DueDate = date.Value,
      HasAssignment = true,
      Completed = completed,
      Total = total,
      PupilPremiumCompleted = pupilPremiumCompleted,
      PupilPremiumTotal = pupilPremiumTotal
    };
  }

  private static AssignmentsProgressCell BuildAggregateCell(
    IReadOnlyDictionary<string, PartitionAssignmentData> partitionData,
    IReadOnlyDictionary<string, List<int>> studentIdsByPartition,
    string dueDate,
    IReadOnlyDictionary<string, List<int>> pupilPremiumStudentIdsByPartition = null)
  {
    var cell = new AssignmentsProgressCell { DueDate = dueDate };
    foreach (var entry in studentIdsByPartition)
    {
      if (!partitionData.TryGetValue(entry.Key, out var data)) continue;

      var partitionCell = BuildAggregateCell(data, entry.Value, dueDate, pupilPremiumStudentIdsByPartition?.GetValueOrDefault(entry.Key));
      if (!partitionCell.HasAssignment) continue;

      cell.HasAssignment = true;
      cell.Completed += partitionCell.Completed;
      cell.Total += partitionCell.Total;
      cell.PupilPremiumCompleted += partitionCell.PupilPremiumCompleted;
      cell.PupilPremiumTotal += partitionCell.PupilPremiumTotal;
    }

    return cell;
  }

  private static AssignmentsProgressCell BuildAggregateCell(
    IReadOnlyDictionary<string, PartitionAssignmentData> partitionData,
    IReadOnlyDictionary<string, List<int>> studentIdsByPartition,
    StaffDateColumn date,
    Dictionary<(string AssignmentKey, DateOnly DueDate, int StudentId), AssignmentProgressTotals> progressCache,
    IReadOnlyDictionary<string, List<int>> pupilPremiumStudentIdsByPartition = null)
  {
    var cell = new AssignmentsProgressCell { DueDate = date.Value };
    foreach (var entry in studentIdsByPartition)
    {
      if (!partitionData.TryGetValue(entry.Key, out var data)) continue;

      var partitionCell = BuildAggregateCell(data, entry.Value, date, progressCache, pupilPremiumStudentIdsByPartition?.GetValueOrDefault(entry.Key));
      if (!partitionCell.HasAssignment) continue;

      cell.HasAssignment = true;
      cell.Completed += partitionCell.Completed;
      cell.Total += partitionCell.Total;
      cell.PupilPremiumCompleted += partitionCell.PupilPremiumCompleted;
      cell.PupilPremiumTotal += partitionCell.PupilPremiumTotal;
    }

    return cell;
  }

  private static AssignmentsProgressCell BuildStudentCell(PartitionAssignmentData data, int studentId, string dueDate)
  {
    if (string.IsNullOrWhiteSpace(dueDate)) return new AssignmentsProgressCell();
    var date = DateOnly.ParseExact(dueDate, "yyyy-MM-dd");
    if (!data.AssignmentsByDate.TryGetValue(date, out var assignment)) return new AssignmentsProgressCell { DueDate = dueDate };
    var progress = GetAssignmentProgress(assignment, data, studentId);

    return new AssignmentsProgressCell
    {
      DueDate = dueDate,
      HasAssignment = true,
      Completed = progress.Completed,
      Total = progress.Total
    };
  }

  private static AssignmentsProgressCell BuildStudentCell(
    PartitionAssignmentData data,
    int studentId,
    StaffDateColumn date,
    Dictionary<(string AssignmentKey, DateOnly DueDate, int StudentId), AssignmentProgressTotals> progressCache)
  {
    if (!data.AssignmentsByDate.TryGetValue(date.DueDate, out var assignment)) return new AssignmentsProgressCell { DueDate = date.Value };
    var progress = GetAssignmentProgress(assignment, data, studentId, progressCache);

    return new AssignmentsProgressCell
    {
      DueDate = date.Value,
      HasAssignment = true,
      Completed = progress.Completed,
      Total = progress.Total
    };
  }

  private static AssignmentsProgressCell BuildAggregateStudentCell(
    IReadOnlyDictionary<string, PartitionAssignmentData> partitionData,
    IEnumerable<string> partitionKeys,
    int studentId,
    string dueDate)
  {
    var cell = new AssignmentsProgressCell { DueDate = dueDate };
    foreach (var partitionKey in partitionKeys)
    {
      if (!partitionData.TryGetValue(partitionKey, out var data)) continue;

      var partitionCell = BuildStudentCell(data, studentId, dueDate);
      if (!partitionCell.HasAssignment) continue;

      cell.HasAssignment = true;
      cell.Completed += partitionCell.Completed;
      cell.Total += partitionCell.Total;
    }

    return cell;
  }

  private static AssignmentsProgressCell BuildAggregateStudentCell(
    IReadOnlyDictionary<string, PartitionAssignmentData> partitionData,
    IEnumerable<string> partitionKeys,
    int studentId,
    StaffDateColumn date,
    Dictionary<(string AssignmentKey, DateOnly DueDate, int StudentId), AssignmentProgressTotals> progressCache)
  {
    var cell = new AssignmentsProgressCell { DueDate = date.Value };
    foreach (var partitionKey in partitionKeys)
    {
      if (!partitionData.TryGetValue(partitionKey, out var data)) continue;

      var partitionCell = BuildStudentCell(data, studentId, date, progressCache);
      if (!partitionCell.HasAssignment) continue;

      cell.HasAssignment = true;
      cell.Completed += partitionCell.Completed;
      cell.Total += partitionCell.Total;
    }

    return cell;
  }

  private static List<AssignmentsStaffRow> BuildClassStudentRows(
    IEnumerable<User> students,
    PartitionAssignmentData data,
    IReadOnlyList<StaffDateColumn> dateColumns,
    Dictionary<(string AssignmentKey, DateOnly DueDate, int StudentId), AssignmentProgressTotals> progressCache)
  {
    return students
      .OrderBy(o => o.LastName, StringComparer.OrdinalIgnoreCase)
      .ThenBy(o => o.FirstName, StringComparer.OrdinalIgnoreCase)
      .Select(student => new AssignmentsStaffRow
      {
        Title = $"{student.LastName}, {student.FirstName}",
        PupilPremium = student.PupilPremium,
        Cells = dateColumns.Select(date => BuildStudentCell(data, student.Id, date, progressCache)).ToList()
      })
      .ToList();
  }

  private static List<AssignmentsStaffRow> BuildAggregateStudentRows(
    IEnumerable<User> students,
    IReadOnlyDictionary<string, PartitionAssignmentData> partitionData,
    IReadOnlyList<StaffDateColumn> dateColumns,
    IReadOnlyDictionary<string, CourseEntity> coursesByKeyStageAndSubjectCode,
    IReadOnlyDictionary<int, List<ParsedClass>> studentClassesById,
    Dictionary<(string AssignmentKey, DateOnly DueDate, int StudentId), AssignmentProgressTotals> progressCache,
    int yearGroup = 0)
  {
    return students
      .OrderBy(o => o.LastName, StringComparer.OrdinalIgnoreCase)
      .ThenBy(o => o.FirstName, StringComparer.OrdinalIgnoreCase)
      .Select(student =>
      {
        var partitionKeys = GetStudentPartitionKeys(student, coursesByKeyStageAndSubjectCode, partitionData, studentClassesById, yearGroup);
        return new AssignmentsStaffRow
        {
          Title = $"{student.LastName}, {student.FirstName}",
          PupilPremium = student.PupilPremium,
          Cells = dateColumns.Select(date => BuildAggregateStudentCell(partitionData, partitionKeys, student.Id, date, progressCache)).ToList()
        };
      })
      .Where(o => o.Cells.Any(cell => cell.HasAssignment))
      .ToList();
  }

  private static List<CompletionStudentRow> BuildCompletionStudentRows(IEnumerable<User> students, Func<User, AssignmentsProgressCell> buildCell)
  {
    return students
      .Select(student => new { Student = student, Cell = buildCell(student) })
      .Where(o => o.Cell.HasAssignment)
      .Select(o => new CompletionStudentRow
      {
        Name = o.Student.DisplayName,
        FirstName = o.Student.FirstName,
        LastName = o.Student.LastName,
        CompletedQuestions = o.Cell.Completed,
        TotalQuestions = o.Cell.Total,
        CompletionPercentage = GetCompletionPercentage(o.Cell.Completed, o.Cell.Total)
      })
      .OrderByDescending(o => o.CompletionPercentage)
      .ThenBy(o => o.LastName, StringComparer.OrdinalIgnoreCase)
      .ThenBy(o => o.FirstName, StringComparer.OrdinalIgnoreCase)
      .ToList();
  }

  private static Dictionary<string, List<int>> BuildStudentIdsByPartition(
    IEnumerable<User> students,
    IReadOnlyDictionary<string, CourseEntity> coursesByKeyStageAndSubjectCode,
    IReadOnlyDictionary<string, PartitionAssignmentData> partitionData,
    int yearGroup = 0)
  {
    return students
      .SelectMany(student => GetStudentPartitionKeys(student, coursesByKeyStageAndSubjectCode, partitionData, yearGroup)
        .Select(partitionKey => new { partitionKey, student.Id }))
      .GroupBy(o => o.partitionKey, StringComparer.OrdinalIgnoreCase)
      .ToDictionary(
        g => g.Key,
        g => g.Select(o => o.Id).Distinct().ToList(),
        StringComparer.OrdinalIgnoreCase);
  }

  private static Dictionary<string, List<int>> BuildStudentIdsByPartition(
    IEnumerable<User> students,
    IReadOnlyDictionary<string, CourseEntity> coursesByKeyStageAndSubjectCode,
    IReadOnlyDictionary<string, PartitionAssignmentData> partitionData,
    IReadOnlyDictionary<int, List<ParsedClass>> studentClassesById,
    int yearGroup = 0)
  {
    return students
      .SelectMany(student => GetStudentPartitionKeys(student, coursesByKeyStageAndSubjectCode, partitionData, studentClassesById, yearGroup)
        .Select(partitionKey => new { partitionKey, student.Id }))
      .GroupBy(o => o.partitionKey, StringComparer.OrdinalIgnoreCase)
      .ToDictionary(
        g => g.Key,
        g => g.Select(o => o.Id).Distinct().ToList(),
        StringComparer.OrdinalIgnoreCase);
  }

  private static List<string> GetStudentPartitionKeys(
    User student,
    IReadOnlyDictionary<string, CourseEntity> coursesByKeyStageAndSubjectCode,
    IReadOnlyDictionary<string, PartitionAssignmentData> partitionData,
    int yearGroup = 0)
  {
    return ParseClasses(student.Classes)
      .Where(o => yearGroup <= 0 || o.YearGroup == yearGroup)
      .Select(o => GetAssignmentKey(o, coursesByKeyStageAndSubjectCode))
      .Where(o => o is not null && partitionData.ContainsKey(o))
      .Distinct(StringComparer.OrdinalIgnoreCase)
      .ToList();
  }

  private static List<string> GetStudentPartitionKeys(
    User student,
    IReadOnlyDictionary<string, CourseEntity> coursesByKeyStageAndSubjectCode,
    IReadOnlyDictionary<string, PartitionAssignmentData> partitionData,
    IReadOnlyDictionary<int, List<ParsedClass>> studentClassesById,
    int yearGroup = 0)
  {
    if (!studentClassesById.TryGetValue(student.Id, out var classes)) return [];

    return classes
      .Where(o => yearGroup <= 0 || o.YearGroup == yearGroup)
      .Select(o => GetAssignmentKey(o, coursesByKeyStageAndSubjectCode))
      .Where(o => o is not null && partitionData.ContainsKey(o))
      .Distinct(StringComparer.OrdinalIgnoreCase)
      .ToList();
  }

  private static List<AssignmentsStaffQuestion> BuildQuestionSummaries(
    PartitionAssignmentData data,
    IReadOnlyList<int> studentIds,
    DateOnly dueDate,
    Dictionary<(string AssignmentKey, DateOnly DueDate, int StudentId), List<AssignmentProgressEntry>> progressCache)
  {
    if (!data.QuestionsByDate.TryGetValue(dueDate, out var questions) || questions.Count == 0) return [];

    return questions
      .Select((question, index) => BuildQuestionSummary(data, question, index + 1, studentIds, progressCache))
      .ToList();
  }

  private static AssignmentsStaffQuestion BuildQuestionSummary(
    PartitionAssignmentData data,
    AssignmentQuestionEntity question,
    int questionNumber,
    IReadOnlyList<int> studentIds,
    Dictionary<(string AssignmentKey, DateOnly DueDate, int StudentId), List<AssignmentProgressEntry>> progressCache)
  {
    var attempted = 0;
    var firstTimeCorrect = 0;
    var totalQuestions = data.QuestionsByDate.TryGetValue(question.DueDate, out var questions) ? questions.Count : 0;

    foreach (var studentId in studentIds)
    {
      if (!data.SubmissionsByStudentAndDate.TryGetValue((question.DueDate, studentId), out var submission)) continue;

      var progress = GetAssignmentQuestionProgress(submission, totalQuestions, progressCache);
      var entry = progress.FirstOrDefault(o => o.QuestionNumber == questionNumber);
      if (entry is null || entry.Attempts <= 0) continue;

      attempted++;
      if (entry.IsCorrect && entry.Attempts == 1)
      {
        firstTimeCorrect++;
      }
    }

    return new AssignmentsStaffQuestion
    {
      QuestionNumber = questionNumber,
      UnitTitle = question.UnitTitle ?? string.Empty,
      QuestionText = question.Question ?? string.Empty,
      CorrectAnswer = question.CorrectAnswer ?? string.Empty,
      IncorrectAnswers = [question.IncorrectAnswer1 ?? string.Empty, question.IncorrectAnswer2 ?? string.Empty, question.IncorrectAnswer3 ?? string.Empty],
      Attempted = attempted,
      FirstTimeCorrect = firstTimeCorrect,
      Percentage = GetCompletionPercentage(firstTimeCorrect, attempted)
    };
  }

  private static List<AssignmentProgressEntry> GetAssignmentQuestionProgress(
    AssignmentSubmissionEntity submission,
    int questionCount,
    Dictionary<(string AssignmentKey, DateOnly DueDate, int StudentId), List<AssignmentProgressEntry>> progressCache)
  {
    var key = (BuildAssignmentRowKey(submission.YearGroup, submission.CourseId), submission.DueDate, submission.StudentId);
    if (!progressCache.TryGetValue(key, out var progress))
    {
      progress = ParseProgress(submission.Progress, questionCount);
      progressCache[key] = progress;
    }

    return progress;
  }

  private static int GetAssignmentTotal(AssignmentEntity assignment) => Math.Max(assignment.Length, 0);

  private static AssignmentProgressTotals GetAssignmentProgress(AssignmentEntity assignment, PartitionAssignmentData data, int studentId)
  {
    var total = GetAssignmentTotal(assignment);
    data.SubmissionsByStudentAndDate.TryGetValue((assignment.DueDate, studentId), out var submission);
    return new AssignmentProgressTotals(Math.Min(submission?.Completed ?? 0, total), total);
  }

  private static AssignmentProgressTotals GetAssignmentProgress(
    AssignmentEntity assignment,
    PartitionAssignmentData data,
    int studentId,
    Dictionary<(string AssignmentKey, DateOnly DueDate, int StudentId), AssignmentProgressTotals> progressCache)
  {
    var key = (assignment.RowKey, assignment.DueDate, studentId);
    if (!progressCache.TryGetValue(key, out var progress))
    {
      progress = GetAssignmentProgress(assignment, data, studentId);
      progressCache[key] = progress;
    }

    return progress;
  }

  private static int GetCompletionPercentage(int completed, int total)
  {
    return total <= 0 ? 0 : (int)Math.Round(completed * 100d / total);
  }

  private async Task<StudentAssignmentContext> LoadStudentAssignmentContextAsync(User student, CourseEntity course, int yearGroup, DateOnly dueDate, string className)
  {
    if (course.KeyStage != GetKeyStage(yearGroup)) return null;

    var dueDateText = FormatDate(dueDate);
    var assignmentKey = BuildAssignmentRowKey(yearGroup, course.RowKey);
    var assignment = await _assignmentsClient.GetEntityIfExistsAsync<AssignmentEntity>(dueDateText, assignmentKey);
    if (!assignment.HasValue) return null;

    var questionPrefix = BuildQuestionRowKeyPrefix(yearGroup, course.RowKey);
    var questions = (await _questionsClient.QueryAsync<AssignmentQuestionEntity>(
      filter: $"{BuildPartitionKeyFilter(dueDateText)} and {BuildRowKeyPrefixFilter(questionPrefix)}").ToListAsync())
      .OrderBy(question => question.QuestionNumber).ToList();

    if (questions.Count == 0) return null;

    return new StudentAssignmentContext
    {
      DueDateText = dueDateText,
      Questions = questions,
      Submission = await GetOrCreateSubmissionAsync(student, yearGroup, course.RowKey, dueDateText, className, questions)
    };
  }

  private async Task<AssignmentSubmissionEntity> GetOrCreateSubmissionAsync(User student, int yearGroup, string courseId, string dueDateText, string className, IReadOnlyList<AssignmentQuestionEntity> questions)
  {
    var rowKey = BuildSubmissionRowKey(student.Id, yearGroup, courseId);
    var existing = await _submissionsClient.GetEntityIfExistsAsync<AssignmentSubmissionEntity>(dueDateText, rowKey);
    if (existing.HasValue) return existing.Value;

    var submission = new AssignmentSubmissionEntity
    {
      PartitionKey = dueDateText,
      RowKey = rowKey,
      ClassName = className,
      Progress = BuildInitialProgress(questions),
      Completed = 0,
      LockedUntil = DateTimeOffset.UtcNow
    };

    try
    {
      await _submissionsClient.AddEntityAsync(submission);
      return await ReloadSubmissionAsync(dueDateText, rowKey);
    }
    catch (RequestFailedException ex) when (ex.Status == 409)
    {
      existing = await _submissionsClient.GetEntityIfExistsAsync<AssignmentSubmissionEntity>(dueDateText, rowKey);
      if (existing.HasValue) return existing.Value;
      throw;
    }
  }

  private async Task<AssignmentSubmissionEntity> ReloadSubmissionAsync(string partitionKey, string rowKey)
  {
    var submission = await _submissionsClient.GetEntityIfExistsAsync<AssignmentSubmissionEntity>(partitionKey, rowKey);
    if (submission.HasValue) return submission.Value;
    throw new InvalidOperationException("The submission could not be reloaded.");
  }

  private static string BuildInitialProgress(IReadOnlyList<AssignmentQuestionEntity> questions)
  {
    var questionNumbers = Enumerable.Range(1, questions.Count).ToArray();
    Shuffle(questionNumbers);
    return string.Join(";", questionNumbers.Select(questionNumber => $"{questionNumber},{CreateAnswerOrder()},0,0"));
  }

  private static string BuildProgress(IEnumerable<AssignmentProgressEntry> entries)
  {
    return string.Join(";", entries.Select(entry => $"{entry.QuestionNumber},{entry.AnswerOrder},{entry.Attempts},{(entry.IsCorrect ? 1 : 0)}"));
  }

  private static string NormalizeQuestionText(string question)
  {
    return (question ?? string.Empty).Trim();
  }

  private static void EnsureSubmissionDelaySatisfied(AssignmentSubmissionEntity submission)
  {
    ArgumentNullException.ThrowIfNull(submission);

    if (submission.LockedUntil > DateTimeOffset.UtcNow)
    {
      throw new TooManyRequestsException("Please wait before submitting another answer.");
    }
  }

  private static string CreateAnswerOrder()
  {
    var digits = new[] { '0', '1', '2', '3' };
    Shuffle(digits);
    return new string(digits);
  }

  private static string CreateAnswerOrder(string currentOrder)
  {
    string nextOrder;
    do
    {
      nextOrder = CreateAnswerOrder();
    }
    while (string.Equals(nextOrder, currentOrder, StringComparison.Ordinal));

    return nextOrder;
  }

  private static AssignmentQuestionDto LoadNextQuestion(string progress, List<AssignmentQuestionEntity> questions)
  {
    var entries = ParseProgress(progress, questions.Count);
    var nextEntry = GetCurrentQueueEntry(entries);
    if (nextEntry is null) return null;

    var question = questions[nextEntry.QuestionNumber - 1];

    return new AssignmentQuestionDto
    {
      QuestionNumber = nextEntry.QuestionNumber,
      UnitId = question.UnitId ?? string.Empty,
      UnitTitle = question.UnitTitle ?? string.Empty,
      QuestionText = question.Question,
      Answers = BuildAnswers(question, nextEntry.AnswerOrder)
    };
  }

  private static AssignmentProgressEntry GetCurrentQueueEntry(List<AssignmentProgressEntry> entries)
  {
    if (entries.Count == 0) return null;

    var incompleteCount = GetIncompletePrefixCount(entries);
    if (incompleteCount == 0) return null;

    return entries[0];
  }

  private static AssignmentProgressEntry UpdateQueueAfterAnswer(List<AssignmentProgressEntry> entries, bool isCorrect)
  {
    var entry = GetCurrentQueueEntry(entries) ?? throw new InvalidOperationException("This assignment has already been completed.");

    entries.RemoveAt(0);

    var updatedEntry = entry with
    {
      AnswerOrder = isCorrect ? entry.AnswerOrder : CreateAnswerOrder(entry.AnswerOrder),
      Attempts = entry.Attempts + 1,
      IsCorrect = isCorrect
    };

    if (updatedEntry.IsCorrect)
    {
      entries.Add(updatedEntry);
    }
    else
    {
      entries.Insert(GetIncorrectInsertIndex(GetIncompletePrefixCount(entries)), updatedEntry);
    }

    return updatedEntry;
  }

  private static int GetIncompletePrefixCount(List<AssignmentProgressEntry> entries)
  {
    var incompleteCount = 0;
    var seenCorrect = false;

    foreach (var entry in entries)
    {
      if (entry.IsCorrect)
      {
        seenCorrect = true;
        continue;
      }

      if (seenCorrect)
      {
        throw new InvalidOperationException("Assignment progress is invalid.");
      }

      incompleteCount++;
    }

    return incompleteCount;
  }

  private static int GetIncorrectInsertIndex(int remainingIncompleteCount)
  {
    if (remainingIncompleteCount <= 0) return 0;
    if (remainingIncompleteCount == 1) return 1;
    return Random.Shared.Next(2, remainingIncompleteCount + 1);
  }

  private static int GetCorrectAnswerIndex(string answerOrder)
  {
    var index = answerOrder.IndexOf('0', StringComparison.Ordinal);
    if (index < 0) throw new InvalidOperationException("Assignment answer order is invalid.");
    return index;
  }

  private static List<AssignmentProgressEntry> ParseProgress(string progress, int questionCount)
  {
    var entries = string.IsNullOrWhiteSpace(progress)
      ? [] : progress.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(ParseProgressEntry).ToList();

    if (entries.Count != questionCount || entries.Any(entry => entry.QuestionNumber < 1 || entry.QuestionNumber > questionCount) ||
      entries.Select(entry => entry.QuestionNumber).Distinct().Count() != questionCount)
    {
      throw new InvalidOperationException("Assignment progress does not match question count.");
    }

    return entries;
  }

  private static AssignmentProgressEntry ParseProgressEntry(string value)
  {
    var parts = value.Split(',', StringSplitOptions.TrimEntries);
    if (parts.Length != 4
      || !int.TryParse(parts[0], out var questionNumber)
      || parts[1].Length != 4
      || !parts[1].All(ch => ch is >= '0' and <= '3')
      || parts[1].Distinct().Count() != 4
      || !int.TryParse(parts[2], out var attempts)
      || attempts < 0
      || !int.TryParse(parts[3], out var isCorrectValue)
      || isCorrectValue is < 0 or > 1)
    {
      throw new InvalidOperationException("Assignment progress is invalid.");
    }

    return new AssignmentProgressEntry(questionNumber, parts[1], attempts, isCorrectValue == 1);
  }

  private static void Shuffle<T>(T[] values)
  {
    for (var index = values.Length - 1; index > 0; index--)
    {
      var swapIndex = Random.Shared.Next(index + 1);
      (values[index], values[swapIndex]) = (values[swapIndex], values[index]);
    }
  }

  private static string[] BuildAnswers(AssignmentQuestionEntity question, string answerOrder)
  {
    return answerOrder.Select(digit => digit switch
    {
      '0' => question.CorrectAnswer,
      '1' => question.IncorrectAnswer1,
      '2' => question.IncorrectAnswer2,
      '3' => question.IncorrectAnswer3,
      _ => throw new InvalidOperationException("Assignment answer order is invalid.")
    }).ToArray();
  }

  private Dictionary<string, List<User>> BuildClassRosters()
  {
    return _config.Students
      .SelectMany(student => student.Classes.Select(className => new { ClassName = className, Student = student }))
      .GroupBy(o => o.ClassName, StringComparer.OrdinalIgnoreCase)
      .ToDictionary(
        g => g.Key,
        g => g.Select(o => o.Student).DistinctBy(o => o.Id).ToList(),
        StringComparer.OrdinalIgnoreCase);
  }

  private Dictionary<string, List<User>> BuildClassRosters(IReadOnlyDictionary<int, List<ParsedClass>> studentClassesById)
  {
    return _config.Students
      .SelectMany(student => studentClassesById.TryGetValue(student.Id, out var classes)
        ? classes.Select(cls => (ClassName: cls.Name, Student: student))
        : [])
      .GroupBy(o => o.ClassName, StringComparer.OrdinalIgnoreCase)
      .ToDictionary(
        g => g.Key,
        g => g.Select(o => o.Student).DistinctBy(o => o.Id).ToList(),
        StringComparer.OrdinalIgnoreCase);
  }

  private Dictionary<string, List<User>> BuildTutorGroupRosters()
  {
    return _config.Students
      .Where(o => !string.IsNullOrWhiteSpace(o.TutorGroup))
      .GroupBy(o => o.TutorGroup.Trim(), StringComparer.OrdinalIgnoreCase)
      .ToDictionary(
        g => g.Key,
        g => g.DistinctBy(o => o.Id).ToList(),
        StringComparer.OrdinalIgnoreCase);
  }

  private static List<ParsedClass> ParseClasses(IEnumerable<string> classes)
  {
    if (classes is null) return [];

    return classes
      .Select(ParseClass)
      .Where(o => o is not null)
      .DistinctBy(o => o.Name, StringComparer.OrdinalIgnoreCase)
      .OrderBy(o => o.YearGroup)
      .ThenBy(o => o.SubjectCode, StringComparer.OrdinalIgnoreCase)
      .ThenBy(o => o.Name, StringComparer.OrdinalIgnoreCase)
      .ToList();
  }

  private static ParsedClass ParseClass(string className)
  {
    if (string.IsNullOrWhiteSpace(className)) return null;

    var trimmed = className.Trim();
    var slashIndex = trimmed.IndexOf('/', StringComparison.Ordinal);
    if (slashIndex <= 0 || slashIndex + 2 >= trimmed.Length) return null;

    var yearDigits = new string(trimmed.TakeWhile(char.IsDigit).ToArray());
    if (!int.TryParse(yearDigits, out var yearGroup)) return null;

    var subjectCode = trimmed.Substring(slashIndex + 1, 2);
    return new ParsedClass(trimmed, yearGroup, subjectCode);
  }

  private static int GetYearGroup(string tutorGroup)
  {
    if (string.IsNullOrWhiteSpace(tutorGroup)) return 0;

    var digits = new string(tutorGroup.Trim().TakeWhile(char.IsDigit).ToArray());
    return int.TryParse(digits, out var yearGroup) ? yearGroup : 0;
  }

  private static int GetKeyStage(int yearGroup) => yearGroup >= 12 ? 5 : yearGroup >= 10 ? 4 : 3;

  private static string BuildCourseLookupKey(int keyStage, string subjectCode) => $"{keyStage}:{subjectCode}";

  private static Dictionary<string, CourseEntity> BuildCoursesByKeyStageAndSubjectCode(IEnumerable<CourseEntity> courses)
  {
    return courses
      .Where(o => !string.IsNullOrWhiteSpace(o.SubjectCode))
      .GroupBy(o => BuildCourseLookupKey(o.KeyStage, o.SubjectCode), StringComparer.OrdinalIgnoreCase)
      .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
  }

  private static string GetAssignmentKey(ParsedClass cls, IReadOnlyDictionary<string, CourseEntity> coursesByKeyStageAndSubjectCode)
  {
    if (cls is null) return null;
    return coursesByKeyStageAndSubjectCode.TryGetValue(BuildCourseLookupKey(GetKeyStage(cls.YearGroup), cls.SubjectCode), out var course)
      ? BuildAssignmentRowKey(cls.YearGroup, course.RowKey)
      : null;
  }

  private static string BuildAssignmentRowKey(int yearGroup, string courseId) => $"{yearGroup:D2}_{courseId}";

  private static string BuildQuestionRowKeyPrefix(int yearGroup, string courseId) => $"{BuildAssignmentRowKey(yearGroup, courseId)}_";

  private static string BuildQuestionRowKey(int yearGroup, string courseId, int questionNumber) => $"{BuildQuestionRowKeyPrefix(yearGroup, courseId)}{questionNumber:D3}";

  private static string BuildStudentSubmissionRowKeyPrefix(int studentId) => $"{studentId:D6}_";

  private static string BuildSubmissionRowKey(int studentId, int yearGroup, string courseId) => $"{BuildStudentSubmissionRowKeyPrefix(studentId)}{BuildAssignmentRowKey(yearGroup, courseId)}";

  private static string FormatDate(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

  private static string FormatLongDate(DateOnly date) => date.ToString("dddd d MMMM", CultureInfo.InvariantCulture);

  private static string FormatShortDate(DateOnly date) => date.ToString("d MMM", CultureInfo.InvariantCulture);

  private abstract class AssignmentCacheFile
  {
    public string DueDate { get; set; } = string.Empty;
    public DateTimeOffset GeneratedAtUtc { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }
  }

  private sealed class AssignmentCompletionCache : AssignmentCacheFile
  {
    public List<AssignmentCompletionCacheItem> Assignments { get; set; } = [];
  }

  private sealed class AssignmentCompletionCacheItem
  {
    public string AssignmentKey { get; set; } = string.Empty;
    public int Length { get; set; }
    public List<AssignmentCompletionStudentCacheItem> Students { get; set; } = [];
  }

  private sealed class AssignmentCompletionStudentCacheItem
  {
    public int StudentId { get; set; }
    public int Completed { get; set; }
  }

  private sealed class AssignmentQuestionsCache : AssignmentCacheFile
  {
    public Dictionary<string, List<AssignmentsStaffQuestion>> Contexts { get; set; } = new(StringComparer.OrdinalIgnoreCase);
  }

  private sealed record AssignmentQuestionContext(string Key, string AssignmentKey, List<int> StudentIds);

  private sealed record ParsedClass(string Name, int YearGroup, string SubjectCode)
  {
    public string PartitionKey => $"{YearGroup:D2}{SubjectCode}";
  }

  private sealed record StaffDateColumn(DateOnly DueDate, string Value, AssignmentsDateColumn Column);

  private sealed class PartitionAssignmentData
  {
    public required Dictionary<DateOnly, AssignmentEntity> AssignmentsByDate { get; init; }
    public required Dictionary<(DateOnly DueDate, int StudentId), AssignmentSubmissionEntity> SubmissionsByStudentAndDate { get; init; }
    public required Dictionary<DateOnly, List<AssignmentQuestionEntity>> QuestionsByDate { get; init; }
  }

  private sealed record AssignmentProgressTotals(int Completed, int Total);

  private sealed class StudentAssignmentContext
  {
    public required string DueDateText { get; init; }
    public required List<AssignmentQuestionEntity> Questions { get; init; }
    public required AssignmentSubmissionEntity Submission { get; init; }
  }

  private sealed record AssignmentProgressEntry(int QuestionNumber, string AnswerOrder, int Attempts, bool IsCorrect);
}

public sealed class TooManyRequestsException : InvalidOperationException
{
  public TooManyRequestsException() { }
  public TooManyRequestsException(string message) : base(message) { }
  public TooManyRequestsException(string message, Exception innerException) : base(message, innerException) { }
}
