using Azure;
using Azure.Storage.Blobs;
using BromcomEssentials;
using System.Globalization;
using System.Text.Json;

namespace CurriculumPortal;

public sealed class BromcomAssessmentService : BackgroundService
{
  private const int CacheVersion = 2;
  private static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);
  private static readonly TimeZoneInfo UkTime = TimeZoneInfo.FindSystemTimeZoneById("GMT Standard Time");
  private readonly BlobClient _blobClient;
  private readonly SchoolBromcomClient _client;
  private readonly ConfigService _config;
  private readonly ILogger<BromcomAssessmentService> _logger;
  private readonly SemaphoreSlim _loadLock = new(1, 1);
  private volatile BromcomAssessmentCache _cache;
  private int _disposed;

  public BromcomAssessmentService(BlobServiceClient blobServiceClient, SchoolBromcomClient client, ConfigService config, ILogger<BromcomAssessmentService> logger)
  {
    ArgumentNullException.ThrowIfNull(blobServiceClient);
    ArgumentNullException.ThrowIfNull(config);
    ArgumentNullException.ThrowIfNull(logger);
    _blobClient = blobServiceClient.GetBlobContainerClient("cache").GetBlobClient("bromcom-assessments.json");
    _client = client;
    _config = config;
    _logger = logger;
  }

  public bool IsConfigured => _client is not null;
  public List<AssessmentColumn> AssessmentColumns => _cache?.AssessmentColumns;
  public IReadOnlyList<string> Subjects => AssessmentColumns?
    .Select(column => column.Subject?.Trim())
    .Where(subject => !string.IsNullOrWhiteSpace(subject))
    .Distinct(StringComparer.OrdinalIgnoreCase)
    .OrderBy(subject => subject, StringComparer.OrdinalIgnoreCase)
    .ToList() ?? [];

  public override void Dispose()
  {
    if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
    _loadLock.Dispose();
    base.Dispose();
  }

  protected override async Task ExecuteAsync(CancellationToken stoppingToken)
  {
    if (!IsConfigured) return;

    while (!stoppingToken.IsCancellationRequested)
    {
      var utcNow = DateTime.UtcNow;
      var now = TimeZoneInfo.ConvertTimeFromUtc(utcNow, UkTime);
      var nextRun = now.Date.AddHours(4).AddMinutes(30);
      if (now >= nextRun)
        nextRun = nextRun.AddDays(1);

      var wait = TimeZoneInfo.ConvertTimeToUtc(nextRun, UkTime) - utcNow;
      try
      {
        await Task.Delay(wait, stoppingToken);
        await RefreshAsync(stoppingToken);
      }
      catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
      {
        break;
      }
      catch (Exception ex)
      {
        _logger.LogError(ex, "Nightly Bromcom assessment refresh failed.");
      }
    }
  }

  public async Task LoadAsync(CancellationToken cancellationToken = default)
  {
    if (!IsConfigured) return;

    await _loadLock.WaitAsync(cancellationToken);
    try
    {
      BromcomAssessmentCache cachedData = null;
      try
      {
        var response = await _blobClient.DownloadContentAsync(cancellationToken);
        cachedData = JsonSerializer.Deserialize<BromcomAssessmentCache>(response.Value.Content.ToString(), JsonDefaults.CamelCase);
        if (cachedData is not null)
        {
          cachedData.AssessmentColumns = FilterColumns(cachedData.AssessmentColumns);
          cachedData.AssessmentCompletions ??= [];
        }
      }
      catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
      {
        throw;
      }
      catch (RequestFailedException ex) when (ex.Status == 404)
      {
      }
      catch (JsonException ex)
      {
        _logger.LogWarning(ex, "The Bromcom assessment cache blob is invalid.");
      }
      catch (Exception ex)
      {
        _logger.LogWarning(ex, "Failed to load the Bromcom assessment cache blob.");
      }

      var academicYearStart = GetAcademicYearStart(GetUkToday());
      var cacheIsCompatible = cachedData is not null
        && cachedData.Version == CacheVersion
        && cachedData.AcademicYearStart == academicYearStart;
      _cache = cacheIsCompatible ? cachedData : null;
      if (cacheIsCompatible && DateTimeOffset.UtcNow - cachedData.UpdatedAtUtc <= MaxAge) return;

      try
      {
        await RefreshCoreAsync(academicYearStart, cancellationToken);
      }
      catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
      {
        throw;
      }
      catch (Exception ex)
      {
        if (cachedData is null)
          _logger.LogError(ex, "Failed to hydrate the Bromcom assessment cache.");
        else
          _logger.LogWarning(ex, "Failed to refresh the stale Bromcom assessment cache.");
      }
    }
    finally
    {
      _loadLock.Release();
    }
  }

  public async Task RefreshAsync(CancellationToken cancellationToken = default)
  {
    if (!IsConfigured) throw new InvalidOperationException("Bromcom is not configured.");

    await _loadLock.WaitAsync(cancellationToken);
    try
    {
      await RefreshCoreAsync(GetAcademicYearStart(GetUkToday()), cancellationToken);
    }
    finally
    {
      _loadLock.Release();
    }
  }

  internal BromcomAssessmentProgress GetCompletionProgress(CourseEntity course, UnitEntity unit, IReadOnlySet<string> visibleClassNames = null)
  {
    ArgumentNullException.ThrowIfNull(course);
    ArgumentNullException.ThrowIfNull(unit);

    var cache = _cache;
    var parts = unit.BromcomColumn?.Split('|');
    if (cache is null
      || string.IsNullOrWhiteSpace(course.BromcomSubject)
      || parts?.Length != 4
      || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var configuredYearGroup)
      || !int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var configuredColumnId)) return null;

    var column = cache.AssessmentColumns.FirstOrDefault(candidate => candidate.Id == configuredColumnId
      && candidate.YearGroup == unit.YearGroup
      && candidate.YearGroup == configuredYearGroup
      && EqualsNormalized(candidate.Subject, course.BromcomSubject)
      && EqualsNormalized(candidate.Subject, parts[0])
      && EqualsNormalized(candidate.Term, parts[2]));
    if (column is null) return null;

    var columnValue = BuildColumnValue(column);
    var classCompletions = cache.AssessmentCompletions.Where(completion => completion.Total > 0
      && completion.Column == columnValue
      && ClassNameParser.TryParseSubjectClass(completion.ClassName, out var parsed)
      && parsed.YearGroup == unit.YearGroup
      && EqualsNormalized(parsed.SubjectCode, course.SubjectCode)
      && (visibleClassNames is null || visibleClassNames.Contains(completion.ClassName)))
      .OrderBy(completion => completion.ClassName, StringComparer.OrdinalIgnoreCase)
      .ToList();
    if (classCompletions.Count == 0) return null;

    var completed = classCompletions.Sum(completion => completion.Completed);
    var total = classCompletions.Sum(completion => completion.Total);
    return new BromcomAssessmentProgress
    {
      Percentage = Math.Clamp((int)Math.Round(completed * 100d / total), 0, 100),
      UpdatedAt = cache.UpdatedAtUtc,
      EnteredClasses = classCompletions.Where(completion => completion.Completed * 2 >= completion.Total).Select(BromcomAssessmentClassProgress.FromCompletion).ToList(),
      NotEnteredClasses = classCompletions.Where(completion => completion.Completed * 2 < completion.Total).Select(BromcomAssessmentClassProgress.FromCompletion).ToList()
    };
  }

  private async Task RefreshCoreAsync(int academicYearStart, CancellationToken cancellationToken)
  {
    var columns = FilterColumns(await _client.GetColumnsAsync(cancellationToken: cancellationToken));
    var results = (await _client.GetResultsAsync(academicYearStart, cancellationToken: cancellationToken))
      .Where(result => result is not null)
      .ToList();
    var cache = new BromcomAssessmentCache
    {
      Version = CacheVersion,
      AcademicYearStart = academicYearStart,
      UpdatedAtUtc = DateTimeOffset.UtcNow,
      AssessmentColumns = columns,
      AssessmentCompletions = BuildAssessmentCompletions(columns, results)
    };
    var data = BinaryData.FromString(JsonSerializer.Serialize(cache, JsonDefaults.CamelCase));
    await _blobClient.UploadAsync(data, overwrite: true, cancellationToken);
    _cache = cache;
  }

  private static DateOnly GetUkToday() => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, UkTime));

  private static int GetAcademicYearStart(DateOnly today) => today.Month >= 9 ? today.Year : today.Year - 1;

  private static bool EqualsNormalized(string left, string right) => string.Equals(left?.Trim(), right?.Trim(), StringComparison.OrdinalIgnoreCase);

  private static string Normalize(string value) => value?.Trim().ToUpperInvariant() ?? string.Empty;

  private static string BuildColumnValue(AssessmentColumn column) => $"{column.Subject?.Trim()}|{column.YearGroup.Value.ToString(CultureInfo.InvariantCulture)}|{column.Term?.Trim()}|{column.Id.Value.ToString(CultureInfo.InvariantCulture)}";

  private List<BromcomAssessmentCompletion> BuildAssessmentCompletions(
    IEnumerable<AssessmentColumn> columns,
    IEnumerable<AssessmentResult> results)
  {
    var classes = _config.Students
      .SelectMany(student => (student.Classes ?? []).Select(className => ClassNameParser.TryParseSubjectClass(className, out var parsed)
        ? new { StudentId = student.Id, Class = parsed }
        : null))
      .Where(item => item is not null)
      .GroupBy(item => item.Class.Name, StringComparer.OrdinalIgnoreCase)
      .Select(group => new { Class = group.First().Class, StudentIds = group.Select(item => item.StudentId).ToHashSet() })
      .OrderBy(item => item.Class.Name, StringComparer.OrdinalIgnoreCase)
      .ToList();
    var resultStudents = results
      .Where(result => result.YearGroup.HasValue)
      .GroupBy(result => (Subject: Normalize(result.Subject), YearGroup: result.YearGroup.Value, Term: Normalize(result.Term), Type: Normalize(result.Type)))
      .ToDictionary(group => group.Key, group => group.Select(result => result.StudentId).ToHashSet());
    var columnsByYear = columns
      .Where(column => column.Id.HasValue && column.YearGroup.HasValue)
      .DistinctBy(BuildColumnValue)
      .ToLookup(column => column.YearGroup.Value);
    var completions = new List<BromcomAssessmentCompletion>();

    foreach (var cls in classes)
    {
      foreach (var column in columnsByYear[cls.Class.YearGroup])
      {
        resultStudents.TryGetValue((Normalize(column.Subject), cls.Class.YearGroup, Normalize(column.Term), Normalize(column.Type)), out var completedStudents);
        completions.Add(new BromcomAssessmentCompletion
        {
          ClassName = cls.Class.Name,
          Column = BuildColumnValue(column),
          Completed = completedStudents?.Count(cls.StudentIds.Contains) ?? 0,
          Total = cls.StudentIds.Count
        });
      }
    }

    return completions
      .OrderBy(completion => completion.ClassName, StringComparer.OrdinalIgnoreCase)
      .ThenBy(completion => completion.Column, StringComparer.Ordinal)
      .ToList();
  }

  private static List<AssessmentColumn> FilterColumns(IEnumerable<AssessmentColumn> columns) => columns?
    .Where(column => column is not null
      && (column.Type?.StartsWith("Topic ", StringComparison.OrdinalIgnoreCase) == true
        || column.Type?.StartsWith("Mock ", StringComparison.OrdinalIgnoreCase) == true)
      && column.Subject?.Contains('|', StringComparison.Ordinal) != true
      && column.Term?.Contains('|', StringComparison.Ordinal) != true
      && column.Type?.Contains('|', StringComparison.Ordinal) != true)
    .ToList() ?? [];
}

internal sealed class BromcomAssessmentCache
{
  public int Version { get; set; }
  public int AcademicYearStart { get; set; }
  public DateTimeOffset UpdatedAtUtc { get; set; }
  public List<AssessmentColumn> AssessmentColumns { get; set; } = [];
  public List<BromcomAssessmentCompletion> AssessmentCompletions { get; set; } = [];
}

internal sealed class BromcomAssessmentCompletion
{
  public string ClassName { get; set; }
  public string Column { get; set; }
  public int Completed { get; set; }
  public int Total { get; set; }
}

internal sealed class BromcomAssessmentProgress
{
  public int Percentage { get; set; }
  public DateTimeOffset UpdatedAt { get; set; }
  public List<BromcomAssessmentClassProgress> EnteredClasses { get; set; } = [];
  public List<BromcomAssessmentClassProgress> NotEnteredClasses { get; set; } = [];
}

internal sealed class BromcomAssessmentClassProgress
{
  public string Name { get; set; }
  public int Completed { get; set; }
  public int Total { get; set; }

  public static BromcomAssessmentClassProgress FromCompletion(BromcomAssessmentCompletion completion) => new()
  {
    Name = completion.ClassName,
    Completed = completion.Completed,
    Total = completion.Total
  };
}
