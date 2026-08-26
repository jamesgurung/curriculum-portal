using Azure;
using Azure.Data.Tables;
using Azure.Storage.Blobs;
using System.Globalization;

namespace CurriculumPortal;

public partial class AssignmentService
{
  private static readonly TimeSpan CorrectAnswerDelay = TimeSpan.FromSeconds(1 + 4);
  private static readonly TimeSpan IncorrectAnswerDelay = TimeSpan.FromSeconds(5 + 4);
  private static readonly TimeSpan StaffAssignmentCacheLifetime = TimeSpan.FromDays(7);
  private readonly ConfigService _config;
  private readonly CourseService _courseService;
  private readonly XpService _xpService;
  private readonly BonusQuizService _bonusQuizService;
  private readonly BlobContainerClient _cacheClient;
  private readonly TableClient _assignmentsClient;
  private readonly TableClient _questionsClient;
  private readonly TableClient _submissionsClient;

  public AssignmentService(BlobServiceClient blobServiceClient, TableServiceClient tableServiceClient, ConfigService config, CourseService courseService, XpService xpService, BonusQuizService bonusQuizService)
  {
    ArgumentNullException.ThrowIfNull(blobServiceClient);
    ArgumentNullException.ThrowIfNull(tableServiceClient);
    _cacheClient = blobServiceClient.GetBlobContainerClient("cache");
    _assignmentsClient = tableServiceClient.GetTableClient("assignments");
    _questionsClient = tableServiceClient.GetTableClient("questions");
    _submissionsClient = tableServiceClient.GetTableClient("submissions");
    _config = config;
    _courseService = courseService;
    _xpService = xpService;
    _bonusQuizService = bonusQuizService;
  }

  public async Task<HashSet<string>> GenerateAssignmentsAsync(DateOnly dueDate, CancellationToken cancellationToken = default)
  {
    if (dueDate.DayOfWeek != DayOfWeek.Monday) throw new InvalidOperationException("Assignments must be due on Mondays");
    var assignmentPartitionKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var dueDateText = FormatDate(dueDate);
    var courses = await _courseService.ListCoursesAsync(cancellationToken);
    foreach (var course in courses.Where(o => o.AssignmentLength > 0))
    {
      var units = await _courseService.ListUnitsAsync(course.RowKey, cancellationToken);
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
        var existing = await _assignmentsClient.GetEntityIfExistsAsync<AssignmentEntity>(assignment.PartitionKey, assignment.RowKey, cancellationToken: cancellationToken);
        if (existing.HasValue)
        {
          assignmentPartitionKeys.Add($"{yearGroup:D2}{course.SubjectCode}");
          continue;
        }

        var questions = new List<QuestionBankQuestionWithUnit>();
        foreach (var unit in pastUnits)
        {
          var unitQuestionBank = await _courseService.GetBlobAsync<QuestionBank>(unit.RowKey, cancellationToken);
          questions.AddRange(unitQuestionBank.Questions.Select(q => new QuestionBankQuestionWithUnit(q, unit.RowKey, unit.Title)));
        }

        var questionPrefix = BuildQuestionRowKeyPrefix(yearGroup, course.RowKey);
        var pastQuestions = await _questionsClient.QueryAsync<AssignmentQuestionEntity>(
          filter: $"{BuildPartitionKeyLessThanFilter(dueDateText)} and {BuildRowKeyPrefixFilter(questionPrefix)}",
          select: ["PartitionKey", "RowKey", "Question"],
          cancellationToken: cancellationToken).ToListAsync(cancellationToken);
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
        await _assignmentsClient.AddEntityAsync(assignment, cancellationToken);
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
    await _xpService.CreateWeeklyRowsAsync(dueDate, cancellationToken);
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
    var today = DateOnly.FromDateTime(DateTime.UtcNow);
    var studentsWithClasses = _config.Students
      .Select(student => new { Student = student, Classes = ParseClasses(student.Classes) })
      .Where(o => o.Classes.Count > 0)
      .ToList();
    if (studentsWithClasses.Count == 0) return [];

    var assignmentCourses = (await _courseService.ListCoursesAsync())
      .Where(o => !string.IsNullOrWhiteSpace(o.SubjectCode))
      .ToList();
    var coursesByKeyStageAndSubjectCode = BuildCoursesByKeyStageAndSubjectCode(assignmentCourses);
    var assignmentKeys = NormalizeKeys(studentsWithClasses
      .SelectMany(o => o.Classes)
      .Select(cls => GetAssignmentKey(cls, coursesByKeyStageAndSubjectCode, deadline, today))
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
        .Select(cls =>
        {
          var yearGroup = ClassNameParser.GetCohortYearGroup(cls.YearGroup, deadline, today);
          return new
          {
            BehaviourCode = yearGroup is >= 7 and <= 9 ? "KS3" : yearGroup is >= 10 and <= 13 ? cls.SubjectCode : null,
            AssignmentKey = GetAssignmentKey(cls, coursesByKeyStageAndSubjectCode, deadline, today)
          };
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
    var nowUtc = DateTimeOffset.UtcNow;
    var gamificationTask = _xpService.GetProgressAsync(student.Id, nowUtc, CancellationToken.None);
    var bonusQuizTask = _bonusQuizService.GetAvailabilityAsync(student, nowUtc, CancellationToken.None);
    var classes = ParseClasses(student.Classes);
    if (classes.Count == 0)
      return new AssignmentsStudentData { BonusQuiz = await bonusQuizTask, Gamification = await gamificationTask };

    var coursesByKeyStageAndSubjectCode = (await _courseService.ListCoursesAsync())
      .Where(o => !string.IsNullOrWhiteSpace(o.SubjectCode))
      .GroupBy(o => BuildCourseLookupKey(o.KeyStage, o.SubjectCode), StringComparer.OrdinalIgnoreCase)
      .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
    var visibleDueDates = GetVisibleDueDates(today);
    var assignmentKeys = NormalizeKeys(classes
      .SelectMany(cls => visibleDueDates.Select(dueDate => GetAssignmentKey(cls, coursesByKeyStageAndSubjectCode, dueDate, today)))
      .Where(o => o is not null));
    var assignmentsByPartitionTask = LoadAssignmentsByDueDatesAsync(assignmentKeys, visibleDueDates);
    var submissionsByPartitionTask = LoadStudentSubmissionsAsync(assignmentKeys, visibleDueDates, student.Id);
    await Task.WhenAll(assignmentsByPartitionTask, submissionsByPartitionTask);
    var assignmentsByPartition = await assignmentsByPartitionTask;
    var submissionsByPartition = await submissionsByPartitionTask;
    var partitionData = BuildPartitionData(assignmentKeys, assignmentsByPartition, submissionsByPartition);
    var cards = classes
      .SelectMany(cls => visibleDueDates.Select(dueDate =>
      {
        var yearGroup = ClassNameParser.GetCohortYearGroup(cls.YearGroup, dueDate, today);
        var course = coursesByKeyStageAndSubjectCode.GetValueOrDefault(BuildCourseLookupKey(GetKeyStage(yearGroup), cls.SubjectCode));
        var assignmentKey = course is null ? null : BuildAssignmentRowKey(yearGroup, course.RowKey);
        partitionData.TryGetValue(assignmentKey ?? string.Empty, out var data);
        AssignmentEntity assignment = null;
        data?.AssignmentsByDate.TryGetValue(dueDate, out assignment);
        return new
        {
          DueDate = dueDate,
          Course = course,
          Data = data,
          Assignment = assignment
        };
      }))
      .Where(o => o.Course is not null && o.Data is not null && o.Assignment is not null)
      .Select(o => new { o.DueDate, Card = CreateStudentCard(o.Assignment, o.Data, student.Id, o.Course) })
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
        .ToList(),
      BonusQuiz = await bonusQuizTask,
      Gamification = await gamificationTask
    };
  }

  public async Task<AssignmentDetailPageData> GetStudentAssignmentDetailAsync(User student, CourseEntity course, int yearGroup, DateOnly dueDate, string className)
  {
    ArgumentNullException.ThrowIfNull(student);
    ArgumentNullException.ThrowIfNull(course);
    ArgumentException.ThrowIfNullOrWhiteSpace(className);

    var contextTask = LoadStudentAssignmentContextAsync(student, course, yearGroup, dueDate, className);
    var gamificationTask = _xpService.GetProgressAsync(student.Id, DateTimeOffset.UtcNow, CancellationToken.None);
    await Task.WhenAll(contextTask, gamificationTask);
    var context = await contextTask;
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
      CurrentQuestion = currentQuestion,
      Gamification = await gamificationTask
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
    var wasComplete = submission.CompletedAt.HasValue;
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
    if (submission.Completed == context.Questions.Count && submission.CompletedAt is null)
      submission.CompletedAt = acceptedAt;
    submission.LockedUntil = acceptedAt.Add(isCorrect ? CorrectAnswerDelay : IncorrectAnswerDelay);

    try
    {
      await _submissionsClient.UpdateEntityAsync(submission, submission.ETag, TableUpdateMode.Replace);
    }
    catch (RequestFailedException ex) when (ex.Status == 412)
    {
      throw new InvalidOperationException("The submission was updated by another request. Please try again.", ex);
    }

    XpAwardResult award = null;
    if (!wasComplete && submission.CompletedAt.HasValue)
    {
      award = await _xpService.RecordAssignmentCompletionAsync(
        student,
        dueDate,
        BuildAssignmentRowKey(yearGroup, course.RowKey),
        submission.CompletedAt.Value,
        context.Questions.Count,
        CancellationToken.None);
    }

    return new AssignmentAnswerResponse
    {
      CorrectAnswer = correctAnswer,
      CompletedQuestions = Math.Min(submission.Completed, context.Questions.Count),
      TotalQuestions = context.Questions.Count,
      NextQuestion = LoadNextQuestion(submission.Progress, context.Questions),
      NewlyAwardedXp = award?.NewlyAwardedXp,
      Gamification = award?.Progress
    };
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

  private Dictionary<string, List<User>> BuildClassRosters(Dictionary<int, List<SubjectClass>> studentClassesById)
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

  private static List<SubjectClass> ParseClasses(IEnumerable<string> classes)
  {
    if (classes is null) return [];

    return classes
      .Select(className => ClassNameParser.TryParseSubjectClass(className, out var parsed) ? parsed : null)
      .Where(o => o is not null)
      .DistinctBy(o => o.Name, StringComparer.OrdinalIgnoreCase)
      .OrderBy(o => o.YearGroup)
      .ThenBy(o => o.SubjectCode, StringComparer.OrdinalIgnoreCase)
      .ThenBy(o => o.Name, StringComparer.OrdinalIgnoreCase)
      .ToList();
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

  private static string GetAssignmentKey(SubjectClass cls, IReadOnlyDictionary<string, CourseEntity> coursesByKeyStageAndSubjectCode, int yearGroupOffset = 0)
  {
    if (cls is null) return null;
    var yearGroup = cls.YearGroup + yearGroupOffset;
    return coursesByKeyStageAndSubjectCode.TryGetValue(BuildCourseLookupKey(GetKeyStage(yearGroup), cls.SubjectCode), out var course)
      ? BuildAssignmentRowKey(yearGroup, course.RowKey)
      : null;
  }

  private static string GetAssignmentKey(SubjectClass cls, IReadOnlyDictionary<string, CourseEntity> coursesByKeyStageAndSubjectCode, DateOnly dueDate, DateOnly currentDate)
    => GetAssignmentKey(cls, coursesByKeyStageAndSubjectCode, ClassNameParser.GetAcademicYear(dueDate) - ClassNameParser.GetAcademicYear(currentDate));

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

  private sealed record StaffDateColumn(DateOnly DueDate, string Value, AssignmentsDateColumn Column, int YearGroupOffset);

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
