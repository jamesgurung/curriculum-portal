using Azure.Data.Tables;
using System.Globalization;
using System.Text.Json;

namespace CurriculumPortal;

public class BonusQuizService
{
  private const int MaxQuizQuestions = 10;
  private const int MaxStartAttempts = 5;
  private static readonly TimeSpan CorrectAnswerDelay = TimeSpan.FromSeconds(1 + 4);
  private static readonly TimeSpan IncorrectAnswerDelay = TimeSpan.FromSeconds(5 + 4);
  private readonly CourseService _courseService;
  private readonly XpService _xpService;
  private readonly TableClient _questionsClient;

  public BonusQuizService(TableServiceClient tableServiceClient, CourseService courseService, XpService xpService)
  {
    ArgumentNullException.ThrowIfNull(tableServiceClient);
    _questionsClient = tableServiceClient.GetTableClient("questions");
    _courseService = courseService;
    _xpService = xpService;
  }

  public async Task<BonusQuizAvailability> GetAvailabilityAsync(User student, DateTimeOffset nowUtc, CancellationToken cancellationToken)
  {
    ArgumentNullException.ThrowIfNull(student);

    var ledger = await _xpService.GetCurrentBonusQuizLedgerAsync(student.Id, nowUtc, cancellationToken);
    if (ledger is null || ledger.CompletionPoints <= 0 || ledger.RemainingBonusXp <= 0) return null;

    if (!string.IsNullOrWhiteSpace(ledger.StateJson))
    {
      var state = ParseState(ledger.StateJson);
      return new BonusQuizAvailability
      {
        InProgress = true,
        QuizXp = state.Questions.Count,
        RemainingBonusXp = ledger.RemainingBonusXp
      };
    }

    var questionCount = Math.Min(MaxQuizQuestions, ledger.RemainingBonusXp);
    var candidates = await LoadCandidatesAsync(student, ledger.DueDate, cancellationToken);
    return candidates.Count < questionCount
      ? null
      : new BonusQuizAvailability { QuizXp = questionCount, RemainingBonusXp = ledger.RemainingBonusXp };
  }

  public async Task<BonusQuizPageData> GetPageAsync(User student, DateTimeOffset nowUtc, CancellationToken cancellationToken)
  {
    ArgumentNullException.ThrowIfNull(student);

    var context = await LoadOrCreateAttemptAsync(student, nowUtc, cancellationToken);
    if (context is null) return null;

    return new BonusQuizPageData
    {
      AttemptId = context.State.AttemptId,
      CompletedQuestions = context.State.Position,
      TotalQuestions = context.State.Questions.Count,
      CurrentQuestion = await LoadQuestionAsync(context.State, cancellationToken),
      RemainingBonusXp = context.Ledger.RemainingBonusXp,
      Gamification = await _xpService.GetProgressAsync(student.Id, nowUtc, cancellationToken)
    };
  }

  public async Task<BonusQuizAnswerResponse> SubmitAnswerAsync(
    User student,
    BonusQuizAnswerRequest request,
    DateTimeOffset nowUtc,
    CancellationToken cancellationToken)
  {
    ArgumentNullException.ThrowIfNull(student);
    ArgumentNullException.ThrowIfNull(request);
    ArgumentException.ThrowIfNullOrWhiteSpace(request.AttemptId);
    if (request.QuestionNumber < 1) throw new ArgumentException("Question number must be at least 1.");
    if (request.Answer is < 0 or > 3) throw new ArgumentException("Answer must be between 0 and 3.");

    var ledger = await _xpService.GetCurrentBonusQuizLedgerAsync(student.Id, nowUtc, cancellationToken);
    if (ledger is null || ledger.CompletionPoints <= 0 || ledger.RemainingBonusXp <= 0 || string.IsNullOrWhiteSpace(ledger.StateJson))
      throw new InvalidOperationException("This bonus quiz is no longer available.");

    var state = ParseState(ledger.StateJson);
    if (!string.Equals(request.AttemptId, state.AttemptId, StringComparison.Ordinal)
      || request.QuestionNumber != state.Position + 1)
    {
      throw new InvalidOperationException("This is not the current bonus quiz question.");
    }
    if (state.LockedUntilUtc > nowUtc) throw new TooManyRequestsException("Please wait before submitting another answer.");

    var correctAnswer = GetCorrectAnswerIndex(state.Questions[state.Position].AnswerOrder);
    if (request.Answer != correctAnswer)
    {
      var candidates = await LoadCandidatesAsync(student, ledger.DueDate, cancellationToken);
      if (candidates.Count < state.Questions.Count)
        throw new InvalidOperationException("There are not enough completed quiz questions to restart this bonus quiz.");

      var restartedState = CreateState(candidates, state.Questions.Count, nowUtc.Add(IncorrectAnswerDelay));
      if (!await _xpService.TryUpdateBonusQuizAsync(
        student.Id,
        ledger.DueDate,
        ledger.StateJson,
        SerializeState(restartedState),
        0,
        nowUtc,
        cancellationToken))
      {
        throw new InvalidOperationException("The bonus quiz was updated by another request. Please try again.");
      }

      return new BonusQuizAnswerResponse
      {
        AttemptId = restartedState.AttemptId,
        Restarted = true,
        CorrectAnswer = correctAnswer,
        CompletedQuestions = 0,
        TotalQuestions = restartedState.Questions.Count,
        NextQuestion = await LoadQuestionAsync(restartedState, cancellationToken),
        NewlyAwardedXp = 0,
        RemainingBonusXp = ledger.RemainingBonusXp,
        Gamification = await _xpService.GetProgressAsync(student.Id, nowUtc, cancellationToken)
      };
    }

    state.Position++;
    if (state.Position < state.Questions.Count)
    {
      state.LockedUntilUtc = nowUtc.Add(CorrectAnswerDelay);
      if (!await _xpService.TryUpdateBonusQuizAsync(
        student.Id,
        ledger.DueDate,
        ledger.StateJson,
        SerializeState(state),
        0,
        nowUtc,
        cancellationToken))
      {
        throw new InvalidOperationException("The bonus quiz was updated by another request. Please try again.");
      }

      return new BonusQuizAnswerResponse
      {
        AttemptId = state.AttemptId,
        CorrectAnswer = correctAnswer,
        CompletedQuestions = state.Position,
        TotalQuestions = state.Questions.Count,
        NextQuestion = await LoadQuestionAsync(state, cancellationToken),
        NewlyAwardedXp = 0,
        RemainingBonusXp = ledger.RemainingBonusXp,
        Gamification = await _xpService.GetProgressAsync(student.Id, nowUtc, cancellationToken)
      };
    }

    var awardedXp = state.Questions.Count;
    if (!await _xpService.TryUpdateBonusQuizAsync(
      student.Id,
      ledger.DueDate,
      ledger.StateJson,
      string.Empty,
      awardedXp,
      nowUtc,
      cancellationToken))
    {
      throw new InvalidOperationException("The bonus quiz was updated by another request. Please try again.");
    }

    return new BonusQuizAnswerResponse
    {
      AttemptId = state.AttemptId,
      CorrectAnswer = correctAnswer,
      CompletedQuestions = state.Questions.Count,
      TotalQuestions = state.Questions.Count,
      NewlyAwardedXp = awardedXp,
      RemainingBonusXp = Math.Max(0, ledger.RemainingBonusXp - awardedXp),
      Gamification = await _xpService.GetProgressAsync(student.Id, nowUtc, cancellationToken)
    };
  }

  private async Task<BonusQuizContext> LoadOrCreateAttemptAsync(User student, DateTimeOffset nowUtc, CancellationToken cancellationToken)
  {
    for (var attempt = 0; attempt < MaxStartAttempts; attempt++)
    {
      var ledger = await _xpService.GetCurrentBonusQuizLedgerAsync(student.Id, nowUtc, cancellationToken);
      if (ledger is null || ledger.CompletionPoints <= 0 || ledger.RemainingBonusXp <= 0) return null;
      if (!string.IsNullOrWhiteSpace(ledger.StateJson))
        return new BonusQuizContext(ledger, ParseState(ledger.StateJson));

      var questionCount = Math.Min(MaxQuizQuestions, ledger.RemainingBonusXp);
      var candidates = await LoadCandidatesAsync(student, ledger.DueDate, cancellationToken);
      if (candidates.Count < questionCount) return null;

      var state = CreateState(candidates, questionCount, nowUtc);
      if (await _xpService.TryUpdateBonusQuizAsync(
        student.Id,
        ledger.DueDate,
        string.Empty,
        SerializeState(state),
        0,
        nowUtc,
        cancellationToken))
      {
        return new BonusQuizContext(ledger, state);
      }
    }

    throw new InvalidOperationException("The bonus quiz could not be started after concurrent changes.");
  }

  private async Task<List<AssignmentQuestionEntity>> LoadCandidatesAsync(User student, DateOnly latestDueDate, CancellationToken cancellationToken)
  {
    var currentAssignmentKeys = await GetCurrentAssignmentKeysAsync(student, cancellationToken);
    if (currentAssignmentKeys.Count == 0) return [];

    var completedWeeks = await _xpService.GetCompletedQuizWeeksAsync(student.Id, latestDueDate, cancellationToken);
    var completedKeysByWeek = completedWeeks
      .Select(week => (PartitionKey: FormatDate(week.DueDate), Keys: week.CompletedKeys.Where(currentAssignmentKeys.Contains).ToHashSet(StringComparer.Ordinal)))
      .Where(week => week.Keys.Count > 0)
      .ToDictionary(week => week.PartitionKey, week => week.Keys, StringComparer.Ordinal);
    if (completedKeysByWeek.Count == 0) return [];

    var firstPartitionKey = completedKeysByWeek.Keys.Min(StringComparer.Ordinal);
    var lastPartitionKey = completedKeysByWeek.Keys.Max(StringComparer.Ordinal);
    var completedAssignmentKeys = completedKeysByWeek.Values.SelectMany(keys => keys).ToHashSet(StringComparer.Ordinal);
    var firstRowKey = $"{completedAssignmentKeys.Min(StringComparer.Ordinal)}_";
    var lastRowKey = $"{completedAssignmentKeys.Max(StringComparer.Ordinal)}_~";
    var candidates = await _questionsClient.QueryAsync<AssignmentQuestionEntity>(
      filter: $"PartitionKey ge '{EscapeODataValue(firstPartitionKey)}' and PartitionKey le '{EscapeODataValue(lastPartitionKey)}' and RowKey ge '{EscapeODataValue(firstRowKey)}' and RowKey lt '{EscapeODataValue(lastRowKey)}'",
      select: ["PartitionKey", "RowKey"],
      cancellationToken: cancellationToken).ToListAsync(cancellationToken);

    return candidates
      .Where(question => completedKeysByWeek.TryGetValue(question.PartitionKey, out var completedKeys)
        && completedKeys.Contains($"{question.YearGroup:D2}_{question.CourseId}"))
      .DistinctBy(question => (question.PartitionKey, question.RowKey))
      .ToList();
  }

  private async Task<HashSet<string>> GetCurrentAssignmentKeysAsync(User student, CancellationToken cancellationToken)
  {
    var classes = (student.Classes ?? [])
      .Select(className => ClassNameParser.TryParseSubjectClass(className, out var parsed) ? parsed : null)
      .Where(cls => cls is not null)
      .ToList();
    if (classes.Count == 0) return [];

    var courses = (await _courseService.ListCoursesAsync(cancellationToken))
      .Where(course => !string.IsNullOrWhiteSpace(course.SubjectCode))
      .GroupBy(course => BuildCourseLookupKey(course.KeyStage, course.SubjectCode), StringComparer.OrdinalIgnoreCase)
      .ToDictionary(group => group.Key, group => group.Select(course => course.RowKey).Distinct(StringComparer.Ordinal).ToList(), StringComparer.OrdinalIgnoreCase);

    return classes
      .SelectMany(cls => courses.TryGetValue(BuildCourseLookupKey(GetKeyStage(cls.YearGroup), cls.SubjectCode), out var courseIds)
        ? courseIds.Select(courseId => $"{cls.YearGroup:D2}_{courseId}")
        : [])
      .ToHashSet(StringComparer.Ordinal);
  }

  private static BonusQuizAttemptState CreateState(List<AssignmentQuestionEntity> candidates, int questionCount, DateTimeOffset lockedUntilUtc)
  {
    var groups = candidates
      .GroupBy(question => $"{question.YearGroup:D2}_{question.CourseId}", StringComparer.Ordinal)
      .Select(group => group.OrderBy(_ => Random.Shared.Next()).ToList())
      .OrderBy(_ => Random.Shared.Next())
      .ToList();
    var selected = new List<AssignmentQuestionEntity>(questionCount);
    var positions = new int[groups.Count];

    while (selected.Count < questionCount)
    {
      var added = false;
      for (var groupIndex = 0; groupIndex < groups.Count && selected.Count < questionCount; groupIndex++)
      {
        if (positions[groupIndex] >= groups[groupIndex].Count) continue;
        selected.Add(groups[groupIndex][positions[groupIndex]++]);
        added = true;
      }
      if (!added) break;
    }

    return new BonusQuizAttemptState
    {
      AttemptId = Guid.NewGuid().ToString("N"),
      Questions = selected.Select(question => new BonusQuizAttemptQuestion
      {
        PartitionKey = question.PartitionKey,
        RowKey = question.RowKey,
        AnswerOrder = CreateAnswerOrder()
      }).ToList(),
      LockedUntilUtc = lockedUntilUtc
    };
  }

  private async Task<AssignmentQuestionDto> LoadQuestionAsync(BonusQuizAttemptState state, CancellationToken cancellationToken)
  {
    if (state.Position >= state.Questions.Count) return null;

    var selected = state.Questions[state.Position];
    var question = await LoadQuestionEntityAsync(selected, cancellationToken);
    return new AssignmentQuestionDto
    {
      QuestionNumber = state.Position + 1,
      CourseId = question.CourseId,
      UnitId = question.UnitId ?? string.Empty,
      UnitTitle = question.UnitTitle ?? string.Empty,
      QuestionText = question.Question,
      Answers = BuildAnswers(question, selected.AnswerOrder)
    };
  }

  private async Task<AssignmentQuestionEntity> LoadQuestionEntityAsync(BonusQuizAttemptQuestion selected, CancellationToken cancellationToken)
  {
    var question = await _questionsClient.GetEntityIfExistsAsync<AssignmentQuestionEntity>(
      selected.PartitionKey,
      selected.RowKey,
      cancellationToken: cancellationToken);
    return question.HasValue ? question.Value : throw new InvalidOperationException("A bonus quiz question is no longer available.");
  }

  private static BonusQuizAttemptState ParseState(string json)
  {
    try
    {
      var state = JsonSerializer.Deserialize<BonusQuizAttemptState>(json, JsonDefaults.CamelCase);
      if (state is null
        || !Guid.TryParseExact(state.AttemptId, "N", out _)
        || state.Questions.Count is < 1 or > MaxQuizQuestions
        || state.Position < 0
        || state.Position >= state.Questions.Count
        || state.Questions.Any(question => string.IsNullOrWhiteSpace(question.PartitionKey)
          || string.IsNullOrWhiteSpace(question.RowKey)
          || !IsValidAnswerOrder(question.AnswerOrder))
        || state.Questions.Select(question => (question.PartitionKey, question.RowKey)).Distinct().Count() != state.Questions.Count)
      {
        throw new InvalidOperationException("The bonus quiz state is invalid.");
      }

      return state;
    }
    catch (JsonException ex)
    {
      throw new InvalidOperationException("The bonus quiz state is invalid.", ex);
    }
  }

  private static string SerializeState(BonusQuizAttemptState state) => JsonSerializer.Serialize(state, JsonDefaults.CamelCase);

  private static string CreateAnswerOrder()
  {
    var digits = new[] { '0', '1', '2', '3' };
    for (var index = digits.Length - 1; index > 0; index--)
    {
      var swapIndex = Random.Shared.Next(index + 1);
      (digits[index], digits[swapIndex]) = (digits[swapIndex], digits[index]);
    }
    return new string(digits);
  }

  private static bool IsValidAnswerOrder(string answerOrder) =>
    answerOrder?.Length == 4
    && answerOrder.All(ch => ch is >= '0' and <= '3')
    && answerOrder.Distinct().Count() == 4;

  private static int GetCorrectAnswerIndex(string answerOrder)
  {
    var index = answerOrder.IndexOf('0', StringComparison.Ordinal);
    return index >= 0 ? index : throw new InvalidOperationException("The bonus quiz answer order is invalid.");
  }

  private static string[] BuildAnswers(AssignmentQuestionEntity question, string answerOrder)
  {
    return answerOrder.Select(digit => digit switch
    {
      '0' => question.CorrectAnswer,
      '1' => question.IncorrectAnswer1,
      '2' => question.IncorrectAnswer2,
      '3' => question.IncorrectAnswer3,
      _ => throw new InvalidOperationException("The bonus quiz answer order is invalid.")
    }).ToArray();
  }

  private static int GetKeyStage(int yearGroup) => yearGroup >= 12 ? 5 : yearGroup >= 10 ? 4 : 3;

  private static string BuildCourseLookupKey(int keyStage, string subjectCode) => $"{keyStage}:{subjectCode}";

  private static string FormatDate(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

  private static string EscapeODataValue(string value) => value.Replace("'", "''", StringComparison.Ordinal);

  private sealed record BonusQuizContext(BonusQuizLedgerSnapshot Ledger, BonusQuizAttemptState State);

  private sealed class BonusQuizAttemptState
  {
    public string AttemptId { get; set; } = string.Empty;
    public List<BonusQuizAttemptQuestion> Questions { get; set; } = [];
    public int Position { get; set; }
    public DateTimeOffset LockedUntilUtc { get; set; }
  }

  private sealed class BonusQuizAttemptQuestion
  {
    public string PartitionKey { get; set; } = string.Empty;
    public string RowKey { get; set; } = string.Empty;
    public string AnswerOrder { get; set; } = string.Empty;
  }
}
