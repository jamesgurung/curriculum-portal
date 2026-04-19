using Azure;
using Azure.Data.Tables;
using System.Globalization;

namespace CurriculumPortal;

public class AssignmentService
{
  private const int MaxPartitionKeyComparisonsPerQuery = 15;
  private const int MaxRowKeyComparisonsPerQuery = 15;
  private static readonly TimeSpan CorrectAnswerDelay = TimeSpan.FromSeconds(1 + 4);
  private static readonly TimeSpan IncorrectAnswerDelay = TimeSpan.FromSeconds(5 + 4);
  private readonly ConfigService _config;
  private readonly CourseService _courseService;
  private readonly TableClient _assignmentsClient;
  private readonly TableClient _questionsClient;
  private readonly TableClient _submissionsClient;

  public AssignmentService(AppOptions options, ConfigService config, CourseService courseService)
  {
    ArgumentNullException.ThrowIfNull(options);
    var tableServiceClient = new TableServiceClient(options.StorageAccountConnectionString);
    _assignmentsClient = tableServiceClient.GetTableClient("assignments");
    _questionsClient = tableServiceClient.GetTableClient("questions");
    _submissionsClient = tableServiceClient.GetTableClient("submissions");
    _config = config;
    _courseService = courseService;
  }

  public async Task<HashSet<int>> GenerateAssignments(DateOnly dueDate)
  {
    if (dueDate.DayOfWeek != DayOfWeek.Monday) throw new InvalidOperationException("Assignments must be due on Mondays");
    var yearGroupsWithAssignments = new HashSet<int>();
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
          PartitionKey = $"{yearGroup:D2}{course.SubjectCode}",
          RowKey = dueDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
          Length = course.AssignmentLength
        };
        var existing = await _assignmentsClient.GetEntityIfExistsAsync<AssignmentEntity>(assignment.PartitionKey, assignment.RowKey);
        if (existing.HasValue)
        {
          yearGroupsWithAssignments.Add(yearGroup);
          continue;
        }

        var questions = new List<QuestionBankQuestionWithUnit>();
        foreach (var unit in pastUnits)
        {
          var unitQuestionBank = await _courseService.GetBlobAsync<QuestionBank>(unit.RowKey);
          questions.AddRange(unitQuestionBank.Questions.Select(q => new QuestionBankQuestionWithUnit(q, unit.RowKey, unit.Title)));
        }

        var pastQuestions = await _questionsClient.QueryAsync<AssignmentQuestionEntity>(
          filter: $"{BuildPartitionKeyFilter(assignment.PartitionKey)} and {BuildRowKeyLessThanFilter(assignment.RowKey)}",
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
        yearGroupsWithAssignments.Add(yearGroup);

        var questionEntities = new List<AssignmentQuestionEntity>(selectedQuestions.Count);
        for (var i = 0; i < selectedQuestions.Count; i++)
        {
          var q = selectedQuestions[i].Question;
          questionEntities.Add(new AssignmentQuestionEntity
          {
            PartitionKey = assignment.PartitionKey,
            RowKey = $"{assignment.RowKey}_{i:D2}",
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
    return yearGroupsWithAssignments;
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

    var partitionKeys = NormalizePartitionKeys(studentsWithClasses.SelectMany(o => o.Classes.Select(cls => cls.PartitionKey)));
    if (partitionKeys.Count == 0) return [];

    var assignmentsByPartition = await LoadAssignmentsByDueDateAsync(partitionKeys, deadline);
    if (assignmentsByPartition.Values.All(o => o.Count == 0)) return [];

    var submissionsTask = LoadSubmissionsByDatesAsync(partitionKeys, assignmentsByPartition);
    var questionCountsTask = LoadLegacyQuestionCountsAsync(partitionKeys, assignmentsByPartition);
    await Task.WhenAll(submissionsTask, questionCountsTask);
    var partitionData = BuildPartitionData(partitionKeys, assignmentsByPartition, await questionCountsTask, await submissionsTask);
    var students = new List<StudentWithCompletion>();

    foreach (var studentWithClasses in studentsWithClasses)
    {
      var progress = studentWithClasses.Classes
        .Select(o => o.PartitionKey)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Where(partitionKey => partitionData.TryGetValue(partitionKey, out var data) && data.AssignmentsByDate.ContainsKey(deadline))
        .Select(partitionKey =>
        {
          var data = partitionData[partitionKey];
          return GetAssignmentProgress(data.AssignmentsByDate[deadline], data, studentWithClasses.Student.Id);
        })
        .Aggregate(new AssignmentProgressTotals(0, 0), (current, next) => new AssignmentProgressTotals(current.Completed + next.Completed, current.Total + next.Total));
      if (progress.Total <= 0) continue;

      students.Add(new StudentWithCompletion
      {
        Student = studentWithClasses.Student,
        CompletedQuestions = progress.Completed,
        TotalQuestions = progress.Total,
        CompletionRate = progress.Completed * 100d / progress.Total
      });
    }

    return students;
  }

  public async Task<AssignmentsStudentData> GetStudentAssignmentsAsync(User student)
  {
    ArgumentNullException.ThrowIfNull(student);

    var today = DateOnly.FromDateTime(DateTime.UtcNow);
    var classes = ParseClasses(student.Classes);
    if (classes.Count == 0) return new AssignmentsStudentData();

    var coursesBySubjectCode = (await _courseService.ListCoursesAsync())
      .Where(o => !string.IsNullOrWhiteSpace(o.SubjectCode))
      .GroupBy(o => o.SubjectCode, StringComparer.OrdinalIgnoreCase)
      .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
    var partitionKeys = NormalizePartitionKeys(classes.Select(o => o.PartitionKey));
    var assignmentsByPartition = await LoadAssignmentsByPartitionAsync(partitionKeys);
    var submissionsTask = LoadStudentSubmissionsAsync(partitionKeys, assignmentsByPartition, student.Id);
    var questionCountsTask = LoadLegacyQuestionCountsAsync(partitionKeys, assignmentsByPartition);
    await Task.WhenAll(submissionsTask, questionCountsTask);
    var partitionData = BuildPartitionData(partitionKeys, assignmentsByPartition, await questionCountsTask, await submissionsTask);
    var cards = classes
      .SelectMany(cls =>
      {
        if (!partitionData.TryGetValue(cls.PartitionKey, out var data)) return [];

        return data.AssignmentsByDate.Values.Select(o => new
        {
          o.DueDate,
          Card = CreateStudentCard(o, data, student.Id, coursesBySubjectCode.GetValueOrDefault(o.SubjectCode))
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

    var courses = await _courseService.ListCoursesAsync();
    var assignmentCourses = courses
      .Where(o => o.AssignmentLength > 0)
      .OrderBy(o => o.Name, StringComparer.OrdinalIgnoreCase)
      .ToList();
    if (assignmentCourses.Count == 0) return new AssignmentsStaffData();
    var assignmentSubjectCodes = assignmentCourses
      .Select(o => o.SubjectCode)
      .Where(o => !string.IsNullOrWhiteSpace(o))
      .ToHashSet(StringComparer.OrdinalIgnoreCase);

    var teacherClasses = ParseClasses(teacher.Classes)
      .Where(o => assignmentSubjectCodes.Contains(o.SubjectCode))
      .OrderBy(o => o.YearGroup)
      .ThenBy(o => o.SubjectCode, StringComparer.OrdinalIgnoreCase)
      .ThenBy(o => o.Name, StringComparer.OrdinalIgnoreCase)
      .ToList();

    var classRosters = BuildClassRosters();
    var tutorGroupRosters = BuildTutorGroupRosters();
    var schoolClasses = ParseClasses(classRosters.Keys)
      .Where(o => assignmentSubjectCodes.Contains(o.SubjectCode))
      .ToList();

    var relevantPartitions = teacherClasses.Select(o => o.PartitionKey)
      .Concat(schoolClasses.Select(o => o.PartitionKey))
      .Distinct(StringComparer.OrdinalIgnoreCase)
      .ToList();

    if (relevantPartitions.Count == 0) return new AssignmentsStaffData();

    var partitionKeys = NormalizePartitionKeys(relevantPartitions);
    var allAssignmentsByPartition = await LoadAssignmentsByPartitionAsync(partitionKeys);
    var dateColumns = allAssignmentsByPartition.Values
      .SelectMany(o => o.Select(assignment => assignment.DueDate))
      .Distinct()
      .OrderByDescending(o => o)
      .Take(5)
      .Select(date => new AssignmentsDateColumn
      {
        Value = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        Label = FormatShortDate(date)
      }).ToList();
    while (dateColumns.Count < 5)
    {
      dateColumns.Add(new AssignmentsDateColumn());
    }
    var visibleDates = dateColumns
      .Where(date => !string.IsNullOrWhiteSpace(date.Value))
      .Select(date => DateOnly.ParseExact(date.Value, "yyyy-MM-dd", CultureInfo.InvariantCulture))
      .ToHashSet();
    var assignmentsByPartition = allAssignmentsByPartition.ToDictionary(
      entry => entry.Key,
      entry => entry.Value.Where(assignment => visibleDates.Contains(assignment.DueDate)).ToList(),
      StringComparer.OrdinalIgnoreCase);
    var submissionsTask = LoadSubmissionsByDatesAsync(partitionKeys, assignmentsByPartition);
    var questionCountsTask = LoadLegacyQuestionCountsAsync(partitionKeys, assignmentsByPartition);
    await Task.WhenAll(submissionsTask, questionCountsTask);
    var partitionData = BuildPartitionData(partitionKeys, assignmentsByPartition, await questionCountsTask, await submissionsTask);

    var details = new List<AssignmentsStaffDetail>();
    var classRowsByName = new Dictionary<string, AssignmentsStaffRow>(StringComparer.OrdinalIgnoreCase);
    var classDetailIndex = 0;
    foreach (var cls in schoolClasses)
    {
      if (!partitionData.TryGetValue(cls.PartitionKey, out var data)) continue;

      classRosters.TryGetValue(cls.Name, out var roster);
      roster ??= [];
      var cells = dateColumns.Select(date => BuildAggregateCell(data, roster.Select(o => o.Id), date.Value)).ToList();
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
        Rows = BuildClassStudentRows(roster, data, dateColumns)
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
      var yearStudentIdsByPartition = BuildStudentIdsByPartition(yearStudents, assignmentSubjectCodes, partitionData);
      if (yearStudentIdsByPartition.Count == 0) continue;

      var yearCells = dateColumns.Select(date => BuildAggregateCell(partitionData, yearStudentIdsByPartition, date.Value)).ToList();
      if (!yearCells.Any(o => o.HasAssignment)) continue;

      var tutorGroupRows = new List<AssignmentsStaffRow>();
      foreach (var tutorGroup in yearGroup.OrderBy(o => o.Key, StringComparer.OrdinalIgnoreCase))
      {
        var tutorStudents = tutorGroup.Value;
        var tutorStudentIdsByPartition = BuildStudentIdsByPartition(tutorStudents, assignmentSubjectCodes, partitionData);
        if (tutorStudentIdsByPartition.Count == 0) continue;

        var tutorCells = dateColumns.Select(date => BuildAggregateCell(partitionData, tutorStudentIdsByPartition, date.Value)).ToList();
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
          Rows = BuildAggregateStudentRows(tutorStudents, partitionData, dateColumns, assignmentSubjectCodes)
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
      var courseClasses = schoolClasses
        .Where(o => o.SubjectCode.Equals(course.SubjectCode, StringComparison.OrdinalIgnoreCase))
        .Where(o => classRowsByName.ContainsKey(o.Name))
        .ToList();
      if (courseClasses.Count == 0) continue;

      var courseStudentIdsByPartition = courseClasses
        .GroupBy(o => o.PartitionKey, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(
          g => g.Key,
          g => g.SelectMany(cls => classRosters.TryGetValue(cls.Name, out var roster) ? roster.Select(student => student.Id) : []).Distinct().ToList(),
          StringComparer.OrdinalIgnoreCase);
      var cells = dateColumns.Select(date => BuildAggregateCell(partitionData, courseStudentIdsByPartition, date.Value)).ToList();
      if (!cells.Any(o => o.HasAssignment)) continue;

      var detailId = $"course-{++courseDetailIndex}";
      courseRows.Add(new AssignmentsStaffRow
      {
        Title = course.Name,
        DetailId = detailId,
        Cells = cells
      });

      details.Add(new AssignmentsStaffDetail
      {
        Id = detailId,
        Title = course.Name,
        FirstColumnTitle = "Class",
        ClickableRows = true,
        Rows = courseClasses.Select(o => classRowsByName[o.Name]).ToList()
      });
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

  private static List<string> NormalizePartitionKeys(IEnumerable<string> partitionKeys)
  {
    return partitionKeys
      .Where(o => !string.IsNullOrWhiteSpace(o))
      .Select(o => o.Trim())
      .Distinct(StringComparer.OrdinalIgnoreCase)
      .ToList();
  }

  private async Task<Dictionary<string, List<AssignmentEntity>>> LoadAssignmentsByPartitionAsync(List<string> partitionKeys)
  {
    return await LoadEntitiesByPartitionAsync<AssignmentEntity>(_assignmentsClient, partitionKeys, ["PartitionKey", "RowKey", "Length"]);
  }

  private async Task<Dictionary<string, List<AssignmentEntity>>> LoadAssignmentsByDueDateAsync(List<string> partitionKeys, DateOnly dueDate)
  {
    var rowKey = dueDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    var rowKeysByPartition = partitionKeys.ToDictionary(
      partitionKey => partitionKey,
      _ => new List<string> { rowKey },
      StringComparer.OrdinalIgnoreCase);

    return await LoadEntitiesByExactRowKeysAsync<AssignmentEntity>(_assignmentsClient, partitionKeys, rowKeysByPartition, ["PartitionKey", "RowKey", "Length"]);
  }

  private async Task<Dictionary<string, List<AssignmentSubmissionEntity>>> LoadStudentSubmissionsAsync(
    List<string> partitionKeys,
    Dictionary<string, List<AssignmentEntity>> assignmentsByPartition,
    int studentId)
  {
    var rowKeysByPartition = partitionKeys.ToDictionary(
      partitionKey => partitionKey,
      partitionKey => assignmentsByPartition.TryGetValue(partitionKey, out var assignments)
        ? assignments.Select(assignment => $"{assignment.RowKey}_{studentId}").Distinct(StringComparer.OrdinalIgnoreCase).ToList()
        : [],
      StringComparer.OrdinalIgnoreCase);

    return await LoadEntitiesByExactRowKeysAsync<AssignmentSubmissionEntity>(_submissionsClient, partitionKeys, rowKeysByPartition, ["PartitionKey", "RowKey", "Completed"]);
  }

  private async Task<Dictionary<string, List<AssignmentSubmissionEntity>>> LoadSubmissionsByDatesAsync(
    List<string> partitionKeys,
    Dictionary<string, List<AssignmentEntity>> assignmentsByPartition)
  {
    var dueDatesByPartition = partitionKeys.ToDictionary(
      partitionKey => partitionKey,
      partitionKey => assignmentsByPartition.TryGetValue(partitionKey, out var assignments)
        ? assignments.Select(assignment => assignment.DueDate).Distinct().OrderBy(o => o).ToList()
        : [],
      StringComparer.OrdinalIgnoreCase);

    return await LoadEntitiesByDueDatesAsync<AssignmentSubmissionEntity>(_submissionsClient, partitionKeys, dueDatesByPartition, ["PartitionKey", "RowKey", "Completed"]);
  }

  private async Task<Dictionary<string, Dictionary<DateOnly, int>>> LoadLegacyQuestionCountsAsync(
    List<string> partitionKeys,
    Dictionary<string, List<AssignmentEntity>> assignmentsByPartition)
  {
    var legacyDatesByPartition = partitionKeys.ToDictionary(
      partitionKey => partitionKey,
      partitionKey => assignmentsByPartition.TryGetValue(partitionKey, out var assignments)
        ? assignments.Where(assignment => assignment.Length <= 0).Select(assignment => assignment.DueDate).Distinct().OrderBy(o => o).ToList()
        : [],
      StringComparer.OrdinalIgnoreCase);

    var questionsByPartition = await LoadEntitiesByDueDatesAsync<AssignmentQuestionEntity>(_questionsClient, partitionKeys, legacyDatesByPartition, ["PartitionKey", "RowKey"]);
    var countsByPartition = partitionKeys.ToDictionary(
      partitionKey => partitionKey,
      _ => new Dictionary<DateOnly, int>(),
      StringComparer.OrdinalIgnoreCase);

    foreach (var partitionKey in partitionKeys)
    {
      foreach (var group in questionsByPartition[partitionKey].GroupBy(question => question.DueDate))
      {
        countsByPartition[partitionKey][group.Key] = group.Count();
      }
    }

    return countsByPartition;
  }

  private static Dictionary<string, PartitionAssignmentData> BuildPartitionData(
    List<string> partitionKeys,
    Dictionary<string, List<AssignmentEntity>> assignmentsByPartition,
    Dictionary<string, Dictionary<DateOnly, int>> questionCountsByPartition,
    Dictionary<string, List<AssignmentSubmissionEntity>> submissionsByPartition)
  {
    var results = new Dictionary<string, PartitionAssignmentData>(partitionKeys.Count, StringComparer.OrdinalIgnoreCase);

    foreach (var partitionKey in partitionKeys)
    {
      assignmentsByPartition.TryGetValue(partitionKey, out var assignments);
      questionCountsByPartition.TryGetValue(partitionKey, out var questionCounts);
      submissionsByPartition.TryGetValue(partitionKey, out var submissions);
      assignments ??= [];
      questionCounts ??= new Dictionary<DateOnly, int>();
      submissions ??= [];

      results[partitionKey] = new PartitionAssignmentData
      {
        AssignmentsByDate = assignments
          .OrderBy(o => o.DueDate)
          .ToDictionary(o => o.DueDate, o => o),
        QuestionCountsByDate = new Dictionary<DateOnly, int>(questionCounts),
        SubmissionsByStudentAndDate = submissions.ToDictionary(o => (o.DueDate, o.StudentId), o => o),
      };
    }

    return results;
  }

  private static IEnumerable<string> BuildPartitionFilters(IReadOnlyList<string> partitionKeys)
  {
    foreach (var chunk in partitionKeys.Chunk(MaxPartitionKeyComparisonsPerQuery))
    {
      yield return string.Join(" or ", chunk.Select(partitionKey => $"PartitionKey eq '{EscapeODataValue(partitionKey)}'"));
    }
  }

  private static async Task<Dictionary<string, List<T>>> LoadEntitiesByExactRowKeysAsync<T>(
    TableClient client,
    IReadOnlyList<string> partitionKeys,
    Dictionary<string, List<string>> rowKeysByPartition,
    IEnumerable<string> select)
    where T : class, ITableEntity, new()
  {
    var buckets = CreateBuckets<T>(partitionKeys);

    foreach (var partitionKey in partitionKeys)
    {
      if (!rowKeysByPartition.TryGetValue(partitionKey, out var rowKeys) || rowKeys.Count == 0)
      {
        continue;
      }

      foreach (var rowKeyChunk in rowKeys.Chunk(MaxRowKeyComparisonsPerQuery))
      {
        var filter = $"{BuildPartitionKeyFilter(partitionKey)} and ({BuildRowKeyExactFilter(rowKeyChunk)})";
        await AddQueryResultsAsync(client, filter, select, buckets);
      }
    }

    return buckets;
  }

  private static async Task<Dictionary<string, List<T>>> LoadEntitiesByDueDatesAsync<T>(
    TableClient client,
    IReadOnlyList<string> partitionKeys,
    Dictionary<string, List<DateOnly>> dueDatesByPartition,
    IEnumerable<string> select)
    where T : class, ITableEntity, new()
  {
    var buckets = CreateBuckets<T>(partitionKeys);

    foreach (var partitionKey in partitionKeys)
    {
      if (!dueDatesByPartition.TryGetValue(partitionKey, out var dueDates) || dueDates.Count == 0)
      {
        continue;
      }

      foreach (var dueDateChunk in dueDates.Chunk(MaxRowKeyComparisonsPerQuery))
      {
        var filter = $"{BuildPartitionKeyFilter(partitionKey)} and ({BuildDueDateFilter(dueDateChunk)})";
        await AddQueryResultsAsync(client, filter, select, buckets);
      }
    }

    return buckets;
  }

  private static async Task<Dictionary<string, List<T>>> LoadEntitiesByPartitionAsync<T>(TableClient client, IReadOnlyList<string> partitionKeys, IEnumerable<string> select)
    where T : class, ITableEntity, new()
  {
    var buckets = CreateBuckets<T>(partitionKeys);

    foreach (var filter in BuildPartitionFilters(partitionKeys))
    {
      await AddQueryResultsAsync(client, filter, select, buckets);
    }

    return buckets;
  }

  private static async Task AddQueryResultsAsync<T>(TableClient client, string filter, IEnumerable<string> select, Dictionary<string, List<T>> buckets)
    where T : class, ITableEntity, new()
  {
    await foreach (var entity in client.QueryAsync<T>(filter: filter, select: select))
    {
      if (!string.IsNullOrEmpty(entity.PartitionKey) && buckets.TryGetValue(entity.PartitionKey, out var bucket))
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

  private static string BuildRowKeyExactFilter(IEnumerable<string> rowKeys)
  {
    return string.Join(" or ", rowKeys.Select(rowKey => $"RowKey eq '{EscapeODataValue(rowKey)}'"));
  }

  private static string BuildDueDateFilter(IEnumerable<DateOnly> dueDates)
  {
    return string.Join(" or ", dueDates.Select(BuildDueDateFilterClause));
  }

  private static string BuildDueDateFilterClause(DateOnly dueDate)
  {
    var start = dueDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    var end = dueDate.AddDays(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    return $"(RowKey ge '{start}' and RowKey lt '{end}')";
  }

  private static string BuildRowKeyLessThanFilter(string rowKeyExclusiveUpperBound) => $"RowKey lt '{EscapeODataValue(rowKeyExclusiveUpperBound)}'";

  private static string EscapeODataValue(string value) => value.Replace("'", "''", StringComparison.Ordinal);

  private static AssignmentsStudentCard CreateStudentCard(AssignmentEntity assignment, PartitionAssignmentData data, int studentId, CourseEntity course)
  {
    var progress = GetAssignmentProgress(assignment, data, studentId);
    var courseId = course?.RowKey ?? assignment.SubjectCode;
    var courseName = course?.Name ?? assignment.SubjectCode;

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

  private static AssignmentsProgressCell BuildAggregateCell(PartitionAssignmentData data, IEnumerable<int> studentIds, string dueDate)
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

    return new AssignmentsProgressCell
    {
      DueDate = dueDate,
      HasAssignment = true,
      Completed = completed,
      Total = total
    };
  }

  private static AssignmentsProgressCell BuildAggregateCell(
    IReadOnlyDictionary<string, PartitionAssignmentData> partitionData,
    IReadOnlyDictionary<string, List<int>> studentIdsByPartition,
    string dueDate)
  {
    var cell = new AssignmentsProgressCell { DueDate = dueDate };
    foreach (var entry in studentIdsByPartition)
    {
      if (!partitionData.TryGetValue(entry.Key, out var data)) continue;

      var partitionCell = BuildAggregateCell(data, entry.Value, dueDate);
      if (!partitionCell.HasAssignment) continue;

      cell.HasAssignment = true;
      cell.Completed += partitionCell.Completed;
      cell.Total += partitionCell.Total;
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

  private static List<AssignmentsStaffRow> BuildClassStudentRows(IEnumerable<User> students, PartitionAssignmentData data, IReadOnlyList<AssignmentsDateColumn> dateColumns)
  {
    return students
      .OrderBy(o => o.LastName, StringComparer.OrdinalIgnoreCase)
      .ThenBy(o => o.FirstName, StringComparer.OrdinalIgnoreCase)
      .Select(student => new AssignmentsStaffRow
      {
        Title = $"{student.LastName}, {student.FirstName}",
        Cells = dateColumns.Select(date => BuildStudentCell(data, student.Id, date.Value)).ToList()
      })
      .ToList();
  }

  private static List<AssignmentsStaffRow> BuildAggregateStudentRows(
    IEnumerable<User> students,
    IReadOnlyDictionary<string, PartitionAssignmentData> partitionData,
    IReadOnlyList<AssignmentsDateColumn> dateColumns,
    HashSet<string> assignmentSubjectCodes)
  {
    return students
      .OrderBy(o => o.LastName, StringComparer.OrdinalIgnoreCase)
      .ThenBy(o => o.FirstName, StringComparer.OrdinalIgnoreCase)
      .Select(student =>
      {
        var partitionKeys = GetStudentPartitionKeys(student, assignmentSubjectCodes, partitionData);
        return new AssignmentsStaffRow
        {
          Title = $"{student.LastName}, {student.FirstName}",
          Cells = dateColumns.Select(date => BuildAggregateStudentCell(partitionData, partitionKeys, student.Id, date.Value)).ToList()
        };
      })
      .Where(o => o.Cells.Any(cell => cell.HasAssignment))
      .ToList();
  }

  private static Dictionary<string, List<int>> BuildStudentIdsByPartition(
    IEnumerable<User> students,
    HashSet<string> assignmentSubjectCodes,
    IReadOnlyDictionary<string, PartitionAssignmentData> partitionData)
  {
    return students
      .SelectMany(student => GetStudentPartitionKeys(student, assignmentSubjectCodes, partitionData)
        .Select(partitionKey => new { partitionKey, student.Id }))
      .GroupBy(o => o.partitionKey, StringComparer.OrdinalIgnoreCase)
      .ToDictionary(
        g => g.Key,
        g => g.Select(o => o.Id).Distinct().ToList(),
        StringComparer.OrdinalIgnoreCase);
  }

  private static List<string> GetStudentPartitionKeys(
    User student,
    HashSet<string> assignmentSubjectCodes,
    IReadOnlyDictionary<string, PartitionAssignmentData> partitionData)
  {
    return ParseClasses(student.Classes)
      .Where(o => assignmentSubjectCodes.Contains(o.SubjectCode) && partitionData.ContainsKey(o.PartitionKey))
      .Select(o => o.PartitionKey)
      .Distinct(StringComparer.OrdinalIgnoreCase)
      .ToList();
  }

  private static int GetAssignmentTotal(AssignmentEntity assignment, PartitionAssignmentData data)
  {
    return assignment.Length > 0
      ? assignment.Length
      : data.QuestionCountsByDate.GetValueOrDefault(assignment.DueDate);
  }

  private static AssignmentProgressTotals GetAssignmentProgress(AssignmentEntity assignment, PartitionAssignmentData data, int studentId)
  {
    var total = GetAssignmentTotal(assignment, data);
    data.SubmissionsByStudentAndDate.TryGetValue((assignment.DueDate, studentId), out var submission);
    return new AssignmentProgressTotals(Math.Min(submission?.Completed ?? 0, total), total);
  }

  private async Task<StudentAssignmentContext> LoadStudentAssignmentContextAsync(User student, CourseEntity course, int yearGroup, DateOnly dueDate, string className)
  {
    var partitionKey = $"{yearGroup:D2}{course.SubjectCode}";
    var dueDateText = dueDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    var assignment = await _assignmentsClient.GetEntityIfExistsAsync<AssignmentEntity>(partitionKey, dueDateText);
    if (!assignment.HasValue) return null;

    var questions = (await _questionsClient.QueryAsync<AssignmentQuestionEntity>(
      filter: $"{BuildPartitionKeyFilter(partitionKey)} and {BuildDueDateFilterClause(dueDate)}").ToListAsync())
      .OrderBy(question => question.QuestionNumber).ToList();

    if (questions.Count == 0) return null;

    return new StudentAssignmentContext
    {
      DueDateText = dueDateText,
      Questions = questions,
      Submission = await GetOrCreateSubmissionAsync(student, partitionKey, dueDateText, className, questions)
    };
  }

  private async Task<AssignmentSubmissionEntity> GetOrCreateSubmissionAsync(User student, string partitionKey, string dueDateText, string className, IReadOnlyList<AssignmentQuestionEntity> questions)
  {
    var rowKey = $"{dueDateText}_{student.Id}";
    var existing = await _submissionsClient.GetEntityIfExistsAsync<AssignmentSubmissionEntity>(partitionKey, rowKey);
    if (existing.HasValue) return existing.Value;

    var submission = new AssignmentSubmissionEntity
    {
      PartitionKey = partitionKey,
      RowKey = rowKey,
      ClassName = className,
      Progress = BuildInitialProgress(questions),
      Completed = 0,
      LockedUntil = DateTimeOffset.UtcNow
    };

    try
    {
      await _submissionsClient.AddEntityAsync(submission);
      return await ReloadSubmissionAsync(partitionKey, rowKey);
    }
    catch (RequestFailedException ex) when (ex.Status == 409)
    {
      existing = await _submissionsClient.GetEntityIfExistsAsync<AssignmentSubmissionEntity>(partitionKey, rowKey);
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

  private static string FormatLongDate(DateOnly date) => date.ToString("dddd d MMMM", CultureInfo.InvariantCulture);

  private static string FormatShortDate(DateOnly date) => date.ToString("d MMM", CultureInfo.InvariantCulture);

  private sealed record ParsedClass(string Name, int YearGroup, string SubjectCode)
  {
    public string PartitionKey => $"{YearGroup:D2}{SubjectCode}";
  }

  private sealed class PartitionAssignmentData
  {
    public required Dictionary<DateOnly, AssignmentEntity> AssignmentsByDate { get; init; }
    public required Dictionary<DateOnly, int> QuestionCountsByDate { get; init; }
    public required Dictionary<(DateOnly DueDate, int StudentId), AssignmentSubmissionEntity> SubmissionsByStudentAndDate { get; init; }
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
