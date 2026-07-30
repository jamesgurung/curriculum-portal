using Azure;
using Azure.Data.Tables;
using System.Globalization;
using System.Text.Json;

namespace CurriculumPortal;

public class XpService
{
  private const int MaxUpdateAttempts = 5;
  private const int WeeklyRowConcurrency = 8;
  private static readonly TimeZoneInfo UkTime = TimeZoneInfo.FindSystemTimeZoneById("GMT Standard Time");
  private static readonly (string Name, int Threshold)[] Ranks =
  [
    ("Novice", 0),
    ("Apprentice", 100),
    ("Practitioner", 300),
    ("Specialist", 700),
    ("Expert", 1200),
    ("Champion", 1800),
    ("Elite", 2500),
    ("Grandmaster", 3300),
    ("Legend", 4200),
    ("Icon", 5200),
    ("Titan", 6300),
    ("Paragon", 7500),
    ("Mythic", 8900),
    ("Sovereign", 10600),
    ("Transcendent", 12600),
    ("Immortal", 15000)
  ];

  private readonly ConfigService _config;
  private readonly CourseService _courseService;
  private readonly TableClient _assignmentsClient;
  private readonly TableClient _submissionsClient;
  private readonly TableClient _ledgerClient;
  private readonly ILogger<XpService> _logger;

  public XpService(TableServiceClient tableServiceClient, ConfigService config, CourseService courseService, ILogger<XpService> logger)
  {
    ArgumentNullException.ThrowIfNull(tableServiceClient);
    _assignmentsClient = tableServiceClient.GetTableClient("assignments");
    _submissionsClient = tableServiceClient.GetTableClient("submissions");
    _ledgerClient = tableServiceClient.GetTableClient("xpledger");
    _config = config;
    _courseService = courseService;
    _logger = logger;
  }

  public async Task CreateWeeklyRowsAsync(DateOnly dueDate, CancellationToken cancellationToken)
  {
    if (_config.Holidays.Any(holiday => dueDate >= holiday.Start && dueDate <= holiday.End)) return;

    var context = await LoadRequirementContextAsync(dueDate, cancellationToken);
    var eligibleStudents = _config.Students
      .Select(student => (Student: student, RequiredKeys: ResolveRequiredAssignmentKeys(student, dueDate, context)))
      .Where(o => o.RequiredKeys.Count > 0)
      .ToList();

    await Parallel.ForEachAsync(
      eligibleStudents,
      new ParallelOptions { MaxDegreeOfParallelism = WeeklyRowConcurrency, CancellationToken = cancellationToken },
      async (eligible, token) =>
      {
        try
        {
          await _ledgerClient.AddEntityAsync(CreateRow(eligible.Student.Id, dueDate), token);
        }
        catch (RequestFailedException ex) when (ex.Status == 409)
        {
        }
      });
  }

  public async Task<XpAwardResult> RecordAssignmentCompletionAsync(
    User student,
    DateOnly dueDate,
    string assignmentRowKey,
    DateTimeOffset completedAt,
    int questionCount,
    CancellationToken cancellationToken)
  {
    ArgumentNullException.ThrowIfNull(student);
    ArgumentException.ThrowIfNullOrWhiteSpace(assignmentRowKey);
    ArgumentOutOfRangeException.ThrowIfLessThan(questionCount, 1);

    try
    {
      var partitionKey = FormatStudentId(student.Id);
      var rowKey = FormatDate(dueDate);
      for (var attempt = 0; attempt < MaxUpdateAttempts; attempt++)
      {
        var row = await LoadOrCreateRowAsync(student, dueDate, assignmentRowKey, cancellationToken);
        if (row is null)
          return new XpAwardResult(0, await GetProgressAsync(student.Id, DateTimeOffset.UtcNow, cancellationToken));

        var completedKeys = ParseCompletedKeys(row.CompletedKeysJson);
        var newlyAwardedXp = 0;
        if (completedKeys.Add(assignmentRowKey))
        {
          row.CompletedKeysJson = JsonSerializer.Serialize(completedKeys.Order(StringComparer.Ordinal));
          row.AnswerPoints = checked(row.AnswerPoints + questionCount);
          newlyAwardedXp += questionCount;
        }

        if (row.CompletionPoints == 0 && completedAt <= GetDeadlineUtc(dueDate))
        {
          var context = await LoadRequirementContextAsync(dueDate, cancellationToken);
          var requiredKeys = ResolveRequiredAssignmentKeys(student, dueDate, context);
          if (requiredKeys.Count > 0 && await AllRequiredAssignmentsCompletedAsync(student.Id, dueDate, requiredKeys, cancellationToken))
          {
            row.CompletionPoints = await MostRecentEarlierRowCompletedAsync(partitionKey, rowKey, cancellationToken) ? 20 : 10;
            newlyAwardedXp += row.CompletionPoints;
          }
        }

        try
        {
          await _ledgerClient.UpdateEntityAsync(row, row.ETag, TableUpdateMode.Replace, cancellationToken);
          return new XpAwardResult(newlyAwardedXp, await GetProgressAsync(student.Id, DateTimeOffset.UtcNow, cancellationToken));
        }
        catch (RequestFailedException ex) when (ex.Status == 412 && attempt + 1 < MaxUpdateAttempts)
        {
        }
      }

      throw new InvalidOperationException("The XP ledger could not be updated after concurrent changes.");
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
      throw;
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Failed to record XP for student {StudentId} and assignment {AssignmentRowKey} due {DueDate}.", student.Id, assignmentRowKey, dueDate);
      return null;
    }
  }

  public async Task<GamificationProgress> GetProgressAsync(int studentId, DateTimeOffset nowUtc, CancellationToken cancellationToken)
  {
    var partitionKey = FormatStudentId(studentId);
    var rows = await _ledgerClient.QueryAsync<XpLedgerEntity>(
      filter: $"PartitionKey eq '{EscapeODataValue(partitionKey)}'",
      select: ["PartitionKey", "RowKey", "AnswerPoints", "CompletionPoints", "BonusQuizPoints"],
      cancellationToken: cancellationToken).ToListAsync(cancellationToken);
    rows = rows.OrderBy(row => row.RowKey, StringComparer.Ordinal).ToList();

    var totalXp = rows.Aggregate(0, (total, row) => checked(total + row.AnswerPoints + row.CompletionPoints + row.BonusQuizPoints));
    var currentStreak = 0;
    var bestStreak = 0;
    foreach (var row in rows)
    {
      if (!DateOnly.TryParseExact(row.RowKey, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dueDate))
        throw new InvalidOperationException($"XP ledger row '{row.RowKey}' has an invalid due date.");

      if (row.CompletionPoints > 0)
      {
        currentStreak++;
        bestStreak = Math.Max(bestStreak, currentStreak);
      }
      else if (nowUtc > GetDeadlineUtc(dueDate))
      {
        currentStreak = 0;
      }
    }

    var rankIndex = Array.FindLastIndex(Ranks, rank => rank.Threshold <= totalXp);
    var currentRank = Ranks[rankIndex];
    if (rankIndex == Ranks.Length - 1)
    {
      return new GamificationProgress
      {
        TotalXp = totalXp,
        CurrentRank = currentRank.Name,
        CurrentStreak = currentStreak,
        BestStreak = bestStreak
      };
    }

    var nextRank = Ranks[rankIndex + 1];
    return new GamificationProgress
    {
      TotalXp = totalXp,
      CurrentRank = currentRank.Name,
      NextRank = nextRank.Name,
      RankProgressXp = totalXp - currentRank.Threshold,
      RankSpanXp = nextRank.Threshold - currentRank.Threshold,
      CurrentStreak = currentStreak,
      BestStreak = bestStreak
    };
  }

  public DateOnly GetCurrentBonusQuizDueDate(DateTimeOffset nowUtc)
  {
    var localNow = TimeZoneInfo.ConvertTime(nowUtc, UkTime);
    var localDate = DateOnly.FromDateTime(localNow.Date);
    var daysUntilMonday = ((int)DayOfWeek.Monday - (int)localDate.DayOfWeek + 7) % 7;
    var dueDate = localDate.AddDays(daysUntilMonday);
    if (daysUntilMonday == 0 && nowUtc > GetDeadlineUtc(dueDate)) dueDate = dueDate.AddDays(7);

    while (_config.Holidays.Any(holiday => dueDate >= holiday.Start && dueDate <= holiday.End))
      dueDate = dueDate.AddDays(7);

    return dueDate;
  }

  public async Task<BonusQuizLedgerSnapshot> GetCurrentBonusQuizLedgerAsync(int studentId, DateTimeOffset nowUtc, CancellationToken cancellationToken)
  {
    var dueDate = GetCurrentBonusQuizDueDate(nowUtc);
    if (nowUtc > GetDeadlineUtc(dueDate)) return null;

    var existing = await _ledgerClient.GetEntityIfExistsAsync<XpLedgerEntity>(
      FormatStudentId(studentId),
      FormatDate(dueDate),
      cancellationToken: cancellationToken);
    return existing.HasValue ? CreateBonusQuizSnapshot(existing.Value, dueDate) : null;
  }

  public async Task<List<BonusQuizCompletedWeek>> GetCompletedQuizWeeksAsync(int studentId, DateOnly latestDueDate, CancellationToken cancellationToken)
  {
    var rows = await _ledgerClient.QueryAsync<XpLedgerEntity>(
      filter: $"PartitionKey eq '{EscapeODataValue(FormatStudentId(studentId))}' and RowKey le '{EscapeODataValue(FormatDate(latestDueDate))}'",
      select: ["PartitionKey", "RowKey", "CompletedKeysJson"],
      cancellationToken: cancellationToken).ToListAsync(cancellationToken);

    return rows
      .Select(row => DateOnly.TryParseExact(row.RowKey, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dueDate)
        ? new BonusQuizCompletedWeek(dueDate, ParseCompletedKeys(row.CompletedKeysJson))
        : throw new InvalidOperationException($"XP ledger row '{row.RowKey}' has an invalid due date."))
      .Where(week => week.CompletedKeys.Count > 0)
      .OrderBy(week => week.DueDate)
      .ToList();
  }

  public async Task<bool> TryUpdateBonusQuizAsync(
    int studentId,
    DateOnly dueDate,
    string expectedStateJson,
    string nextStateJson,
    int awardedXp,
    DateTimeOffset nowUtc,
    CancellationToken cancellationToken)
  {
    ArgumentOutOfRangeException.ThrowIfNegative(awardedXp);
    expectedStateJson ??= string.Empty;
    nextStateJson ??= string.Empty;

    if (dueDate != GetCurrentBonusQuizDueDate(nowUtc) || nowUtc > GetDeadlineUtc(dueDate)) return false;

    var partitionKey = FormatStudentId(studentId);
    var rowKey = FormatDate(dueDate);
    for (var attempt = 0; attempt < MaxUpdateAttempts; attempt++)
    {
      var existing = await _ledgerClient.GetEntityIfExistsAsync<XpLedgerEntity>(
        partitionKey,
        rowKey,
        cancellationToken: cancellationToken);
      if (!existing.HasValue) return false;

      var row = existing.Value;
      if (row.CompletionPoints <= 0
        || !string.Equals(row.BonusQuizStateJson ?? string.Empty, expectedStateJson, StringComparison.Ordinal)
        || awardedXp > GetRemainingBonusQuizXp(row))
      {
        return false;
      }

      row.BonusQuizPoints = checked(row.BonusQuizPoints + awardedXp);
      row.BonusQuizStateJson = nextStateJson;

      try
      {
        await _ledgerClient.UpdateEntityAsync(row, row.ETag, TableUpdateMode.Replace, cancellationToken);
        return true;
      }
      catch (RequestFailedException ex) when (ex.Status == 412 && attempt + 1 < MaxUpdateAttempts)
      {
      }
    }

    throw new InvalidOperationException("The bonus quiz ledger could not be updated after concurrent changes.");
  }

  private async Task<XpLedgerEntity> LoadOrCreateRowAsync(User student, DateOnly dueDate, string assignmentRowKey, CancellationToken cancellationToken)
  {
    if (_config.Holidays.Any(holiday => dueDate >= holiday.Start && dueDate <= holiday.End)) return null;

    var partitionKey = FormatStudentId(student.Id);
    var rowKey = FormatDate(dueDate);
    var existing = await _ledgerClient.GetEntityIfExistsAsync<XpLedgerEntity>(partitionKey, rowKey, cancellationToken: cancellationToken);
    if (existing.HasValue) return existing.Value;

    var context = await LoadRequirementContextAsync(dueDate, cancellationToken);
    var requiredKeys = ResolveRequiredAssignmentKeys(student, dueDate, context);
    if (!requiredKeys.Contains(assignmentRowKey, StringComparer.Ordinal)) return null;

    try
    {
      await _ledgerClient.AddEntityAsync(CreateRow(student.Id, dueDate), cancellationToken);
    }
    catch (RequestFailedException ex) when (ex.Status == 409)
    {
    }

    existing = await _ledgerClient.GetEntityIfExistsAsync<XpLedgerEntity>(partitionKey, rowKey, cancellationToken: cancellationToken);
    return existing.HasValue ? existing.Value : throw new InvalidOperationException("The XP ledger row could not be loaded.");
  }

  private async Task<RequirementContext> LoadRequirementContextAsync(DateOnly dueDate, CancellationToken cancellationToken)
  {
    var dueDateText = FormatDate(dueDate);
    var coursesTask = _courseService.ListCoursesAsync(cancellationToken);
    var assignmentsTask = _assignmentsClient.QueryAsync<AssignmentEntity>(
      filter: $"PartitionKey eq '{EscapeODataValue(dueDateText)}'",
      select: ["PartitionKey", "RowKey"],
      cancellationToken: cancellationToken).ToListAsync(cancellationToken).AsTask();
    await Task.WhenAll(coursesTask, assignmentsTask);

    var courseIdsByKeyStageAndSubject = (await coursesTask)
      .Where(course => !string.IsNullOrWhiteSpace(course.SubjectCode))
      .GroupBy(course => BuildCourseLookupKey(course.KeyStage, course.SubjectCode), StringComparer.OrdinalIgnoreCase)
      .ToDictionary(group => group.Key, group => group.Select(course => course.RowKey).Distinct(StringComparer.Ordinal).ToList(), StringComparer.OrdinalIgnoreCase);
    var assignmentKeys = (await assignmentsTask).Select(assignment => assignment.RowKey).ToHashSet(StringComparer.Ordinal);
    return new RequirementContext(courseIdsByKeyStageAndSubject, assignmentKeys);
  }

  private static HashSet<string> ResolveRequiredAssignmentKeys(User student, DateOnly dueDate, RequirementContext context)
  {
    return (student.Classes ?? [])
      .Select(className => ClassNameParser.TryParseSubjectClass(className, out var parsed) ? parsed : null)
      .Where(cls => cls is not null && !IsExamYearExempt(cls.YearGroup, dueDate))
      .SelectMany(cls => context.CourseIdsByKeyStageAndSubject.TryGetValue(BuildCourseLookupKey(GetKeyStage(cls.YearGroup), cls.SubjectCode), out var courseIds)
        ? courseIds.Select(courseId => $"{cls.YearGroup:D2}_{courseId}")
        : [])
      .Where(context.AssignmentKeys.Contains)
      .ToHashSet(StringComparer.Ordinal);
  }

  private async Task<bool> AllRequiredAssignmentsCompletedAsync(int studentId, DateOnly dueDate, HashSet<string> requiredKeys, CancellationToken cancellationToken)
  {
    var dueDateText = FormatDate(dueDate);
    var submissionPrefix = $"{FormatStudentId(studentId)}_";
    var submissions = await _submissionsClient.QueryAsync<AssignmentSubmissionEntity>(
      filter: $"PartitionKey eq '{EscapeODataValue(dueDateText)}' and RowKey ge '{EscapeODataValue(submissionPrefix)}' and RowKey lt '{EscapeODataValue(submissionPrefix + "~")}'",
      select: ["PartitionKey", "RowKey", "CompletedAt"],
      cancellationToken: cancellationToken).ToListAsync(cancellationToken);
    var completedByAssignmentKey = submissions.ToDictionary(
      submission => $"{submission.YearGroup:D2}_{submission.CourseId}",
      submission => submission.CompletedAt,
      StringComparer.Ordinal);
    var deadline = GetDeadlineUtc(dueDate);
    return requiredKeys.All(key => completedByAssignmentKey.TryGetValue(key, out var completedAt) && completedAt.HasValue && completedAt.Value <= deadline);
  }

  private async Task<bool> MostRecentEarlierRowCompletedAsync(string partitionKey, string rowKey, CancellationToken cancellationToken)
  {
    var earlierRows = await _ledgerClient.QueryAsync<XpLedgerEntity>(
      filter: $"PartitionKey eq '{EscapeODataValue(partitionKey)}' and RowKey lt '{EscapeODataValue(rowKey)}'",
      select: ["PartitionKey", "RowKey", "CompletionPoints"],
      cancellationToken: cancellationToken).ToListAsync(cancellationToken);
    return earlierRows.MaxBy(row => row.RowKey, StringComparer.Ordinal)?.CompletionPoints > 0;
  }

  private static HashSet<string> ParseCompletedKeys(string json)
  {
    try
    {
      return (JsonSerializer.Deserialize<List<string>>(json ?? "[]") ?? [])
        .Where(key => !string.IsNullOrWhiteSpace(key))
        .ToHashSet(StringComparer.Ordinal);
    }
    catch (JsonException ex)
    {
      throw new InvalidOperationException("The XP ledger contains invalid completed assignment data.", ex);
    }
  }

  private static XpLedgerEntity CreateRow(int studentId, DateOnly dueDate) => new()
  {
    PartitionKey = FormatStudentId(studentId),
    RowKey = FormatDate(dueDate)
  };

  private static BonusQuizLedgerSnapshot CreateBonusQuizSnapshot(XpLedgerEntity row, DateOnly dueDate) => new(
    dueDate,
    row.AnswerPoints,
    row.CompletionPoints,
    row.BonusQuizPoints,
    GetRemainingBonusQuizXp(row),
    row.BonusQuizStateJson ?? string.Empty);

  private static int GetRemainingBonusQuizXp(XpLedgerEntity row) =>
    Math.Max(0, 150 - row.AnswerPoints - 20 - row.BonusQuizPoints);

  private static DateTimeOffset GetDeadlineUtc(DateOnly dueDate)
  {
    var localDeadline = DateTime.SpecifyKind(dueDate.ToDateTime(new TimeOnly(8, 0)), DateTimeKind.Unspecified);
    return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(localDeadline, UkTime), TimeSpan.Zero);
  }

  private static bool IsExamYearExempt(int yearGroup, DateOnly dueDate)
    => dueDate.Month is >= 4 and <= 8 && yearGroup is 11 or 13;

  private static int GetKeyStage(int yearGroup) => yearGroup >= 12 ? 5 : yearGroup >= 10 ? 4 : 3;

  private static string BuildCourseLookupKey(int keyStage, string subjectCode) => $"{keyStage}:{subjectCode}";

  private static string FormatStudentId(int studentId) => $"{studentId:D6}";

  private static string FormatDate(DateOnly dueDate) => dueDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

  private static string EscapeODataValue(string value) => value.Replace("'", "''", StringComparison.Ordinal);

  private sealed record RequirementContext(
    Dictionary<string, List<string>> CourseIdsByKeyStageAndSubject,
    HashSet<string> AssignmentKeys);
}

public sealed record XpAwardResult(int NewlyAwardedXp, GamificationProgress Progress);

public sealed record BonusQuizLedgerSnapshot(
  DateOnly DueDate,
  int AnswerPoints,
  int CompletionPoints,
  int BonusQuizPoints,
  int RemainingBonusXp,
  string StateJson);

public sealed record BonusQuizCompletedWeek(DateOnly DueDate, HashSet<string> CompletedKeys);
