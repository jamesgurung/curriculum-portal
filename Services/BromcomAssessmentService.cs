using Azure;
using Azure.Storage.Blobs;
using BromcomEssentials;
using System.Text.Json;

namespace CurriculumPortal;

public sealed class BromcomAssessmentService : IDisposable
{
  private static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);
  private readonly BlobClient _blobClient;
  private readonly SchoolBromcomClient _client;
  private readonly ILogger<BromcomAssessmentService> _logger;
  private readonly SemaphoreSlim _loadLock = new(1, 1);

  public BromcomAssessmentService(BlobServiceClient blobServiceClient, SchoolBromcomClient client, ILogger<BromcomAssessmentService> logger)
  {
    ArgumentNullException.ThrowIfNull(blobServiceClient);
    ArgumentNullException.ThrowIfNull(logger);
    _blobClient = blobServiceClient.GetBlobContainerClient("cache").GetBlobClient("bromcom-assessment-columns.json");
    _client = client;
    _logger = logger;
  }

  public bool IsConfigured => _client is not null;
  public List<AssessmentColumn> AssessmentColumns { get; private set; }
  public IReadOnlyList<string> Subjects => AssessmentColumns?
    .Select(column => column.Subject?.Trim())
    .Where(subject => !string.IsNullOrWhiteSpace(subject))
    .Distinct(StringComparer.OrdinalIgnoreCase)
    .OrderBy(subject => subject, StringComparer.OrdinalIgnoreCase)
    .ToList() ?? [];

  public void Dispose() => _loadLock.Dispose();

  public async Task LoadAsync(CancellationToken cancellationToken = default)
  {
    if (!IsConfigured) return;

    await _loadLock.WaitAsync(cancellationToken);
    try
    {
      List<AssessmentColumn> cachedColumns = null;
      DateTimeOffset? lastModified = null;
      try
      {
        var response = await _blobClient.DownloadContentAsync(cancellationToken);
        cachedColumns = FilterColumns(JsonSerializer.Deserialize<List<AssessmentColumn>>(response.Value.Content.ToString(), JsonDefaults.CamelCase));
        lastModified = response.Value.Details.LastModified;
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
        _logger.LogWarning(ex, "The Bromcom assessment column cache blob is invalid.");
      }
      catch (Exception ex)
      {
        _logger.LogWarning(ex, "Failed to load the Bromcom assessment column cache blob.");
      }

      AssessmentColumns = cachedColumns;
      if (cachedColumns is not null && DateTimeOffset.UtcNow - lastModified <= MaxAge) return;

      try
      {
        await RefreshCoreAsync(cancellationToken);
      }
      catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
      {
        throw;
      }
      catch (Exception ex)
      {
        if (cachedColumns is null)
          _logger.LogError(ex, "Failed to hydrate the Bromcom assessment column cache.");
        else
          _logger.LogWarning(ex, "Failed to refresh the stale Bromcom assessment column cache.");
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
      await RefreshCoreAsync(cancellationToken);
    }
    finally
    {
      _loadLock.Release();
    }
  }

  private async Task RefreshCoreAsync(CancellationToken cancellationToken)
  {
    var columns = FilterColumns(await _client.GetColumnsAsync(cancellationToken: cancellationToken));
    var data = BinaryData.FromString(JsonSerializer.Serialize(columns, JsonDefaults.CamelCase));
    await _blobClient.UploadAsync(data, overwrite: true, cancellationToken);
    AssessmentColumns = columns;
  }

  private static List<AssessmentColumn> FilterColumns(IEnumerable<AssessmentColumn> columns) => columns?
    .Where(column => column is not null
      && (column.Type?.StartsWith("Topic ", StringComparison.OrdinalIgnoreCase) == true
        || column.Type?.StartsWith("Mock ", StringComparison.OrdinalIgnoreCase) == true)
      && column.Subject?.Contains('|', StringComparison.Ordinal) != true
      && column.Term?.Contains('|', StringComparison.Ordinal) != true
      && column.Type?.Contains('|', StringComparison.Ordinal) != true)
    .ToList();
}
