using Azure;
using Azure.Data.Tables;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using SkiaSharp;
using System.Text.Json;

namespace CurriculumPortal;

public class CourseService
{
  private static readonly int[] ImageWidths = [384, 768, 1536];
  private readonly ConfigService _config;
  private readonly BlobContainerClient _blobClient;
  private readonly BlobContainerClient _imagesClient;
  private readonly TableClient _coursesClient;
  private readonly TableClient _unitsClient;

  public CourseService(BlobServiceClient blobServiceClient, TableServiceClient tableServiceClient, ConfigService config)
  {
    ArgumentNullException.ThrowIfNull(blobServiceClient);
    ArgumentNullException.ThrowIfNull(tableServiceClient);
    _blobClient = blobServiceClient.GetBlobContainerClient("curriculum");
    _imagesClient = blobServiceClient.GetBlobContainerClient("images");
    _coursesClient = tableServiceClient.GetTableClient("courses");
    _unitsClient = tableServiceClient.GetTableClient("units");
    _config = config;
  }

  public async Task<List<CourseEntity>> ListCoursesAsync(CancellationToken cancellationToken = default)
  {
    var courses = await _coursesClient.QueryAsync<CourseEntity>(cancellationToken: cancellationToken).ToListAsync(cancellationToken);
    foreach (var course in courses)
      PopulateLeaderNames(course);

    return courses.OrderBy(o => o.KeyStage).ThenBy(o => o.Name).ToList();
  }

  public async Task<List<UnitEntity>> ListUnitsAsync(string courseId = null, CancellationToken cancellationToken = default)
  {
    var units = courseId is null
      ? await _unitsClient.QueryAsync<UnitEntity>(cancellationToken: cancellationToken).ToListAsync(cancellationToken)
      : await _unitsClient.QueryAsync<UnitEntity>(u => u.PartitionKey == courseId, cancellationToken: cancellationToken).ToListAsync(cancellationToken);

    foreach (var unit in units)
      PopulateImageUrl(unit);

    return units.OrderBy(o => o.PartitionKey).ThenBy(o => o.YearGroup).ThenBy(o => o.Term).ThenBy(o => o.Order).ThenBy(o => o.Title).ToList();
  }

  public async Task<CourseEntity> TryGetCourseAsync(string courseId, CancellationToken cancellationToken = default)
  {
    var response = await _coursesClient.GetEntityIfExistsAsync<CourseEntity>("course", courseId, cancellationToken: cancellationToken);
    if (!response.HasValue) return null;

    PopulateLeaderNames(response.Value);
    return response.Value;
  }

  public async Task<UnitEntity> TryGetUnitAsync(string courseId, string unitId, CancellationToken cancellationToken = default)
  {
    var response = await _unitsClient.GetEntityIfExistsAsync<UnitEntity>(courseId, unitId, cancellationToken: cancellationToken);
    if (!response.HasValue) return null;

    PopulateImageUrl(response.Value);
    return response.Value;
  }

  public Task UpdateCourseAsync(CourseEntity course, CancellationToken cancellationToken = default) =>
    _coursesClient.UpsertEntityAsync(course, TableUpdateMode.Replace, cancellationToken);

  public Task UpdateUnitAsync(UnitEntity unit, CancellationToken cancellationToken = default) =>
    _unitsClient.UpsertEntityAsync(unit, TableUpdateMode.Replace, cancellationToken);

  public async Task UpdateUnitRationalesAsync(IReadOnlyList<UnitEntity> units, IReadOnlyDictionary<string, UnitRationaleResponse> rationales,
    CancellationToken cancellationToken = default)
  {
    ArgumentNullException.ThrowIfNull(units);
    ArgumentNullException.ThrowIfNull(rationales);
    if (units.Count is 0 or > 100 || rationales.Count != units.Count)
      throw new InvalidOperationException("The unit rationales could not be updated safely.");

    var partitionKey = units[0].PartitionKey;
    if (units.Any(unit => unit.PartitionKey != partitionKey || !rationales.ContainsKey(unit.RowKey)))
      throw new InvalidOperationException("The unit rationales could not be updated safely.");

    var actions = units.Select(unit =>
    {
      var rationale = rationales[unit.RowKey];
      var entity = new TableEntity(unit.PartitionKey, unit.RowKey)
      {
        [nameof(UnitEntity.WhyThis)] = rationale.WhyThis,
        [nameof(UnitEntity.WhyNow)] = rationale.WhyNow
      };
      if (unit.WhyThis != rationale.WhyThis || unit.WhyNow != rationale.WhyNow)
        entity[nameof(UnitEntity.EvaluationUpdatedAt)] = DateTimeOffset.UtcNow;
      return new TableTransactionAction(TableTransactionActionType.UpdateMerge, entity, unit.ETag);
    }).ToList();
    try
    {
      await _unitsClient.SubmitTransactionAsync(actions, cancellationToken);
    }
    catch (RequestFailedException ex) when (ex.Status is 404 or 409 or 412)
    {
      throw new InvalidOperationException("A unit changed while the rationales were being enhanced. No changes were made.", ex);
    }
  }

  public async Task DeleteUnitAsync(string courseId, string unitId)
  {
    await _unitsClient.DeleteEntityAsync(courseId, unitId);
    await _blobClient.GetBlobClient(unitId + ".knowledge.json").DeleteIfExistsAsync();
    await _blobClient.GetBlobClient(unitId + ".assessment.json").DeleteIfExistsAsync();
    await _blobClient.GetBlobClient(unitId + ".questions.json").DeleteIfExistsAsync();
    foreach (var width in ImageWidths)
      await _imagesClient.GetBlobClient($"{unitId}.{width}.webp").DeleteIfExistsAsync();
  }

  public async Task PrepareImageContainerAsync(CancellationToken cancellationToken = default)
  {
    try
    {
      await _imagesClient.CreateIfNotExistsAsync(PublicAccessType.Blob, cancellationToken: cancellationToken);
      var policy = await _imagesClient.GetAccessPolicyAsync(cancellationToken: cancellationToken);
      if (policy.Value.BlobPublicAccess != PublicAccessType.Blob)
        throw new InvalidOperationException("The images container must allow public blob access without public container listing.");
    }
    catch (RequestFailedException ex)
    {
      throw new InvalidOperationException("Could not prepare the public images container. Check storage permissions and that the account permits public blob access.", ex);
    }
  }

  public async Task<bool> RestoreUnitImageAsync(UnitEntity unit, CancellationToken cancellationToken = default)
  {
    ArgumentNullException.ThrowIfNull(unit);
    if (!(await _imagesClient.GetBlobClient(unit.RowKey + ".1536.webp").ExistsAsync(cancellationToken)).Value)
      return false;

    var image = await _imagesClient.GetBlobClient(unit.RowKey + ".1536.webp").DownloadContentAsync(cancellationToken);
    await UploadUnitThumbnailsAsync(unit.RowKey, image.Value.Content, cancellationToken);
    await SetUnitImageFlagAsync(unit, true, cancellationToken);
    return true;
  }

  public async Task<bool> UploadUnitImageAsync(UnitEntity unit, BinaryData image, CancellationToken cancellationToken = default)
  {
    ArgumentNullException.ThrowIfNull(unit);
    ArgumentNullException.ThrowIfNull(image);
    var blob = _imagesClient.GetBlobClient(unit.RowKey + ".1536.webp");
    var created = true;
    try
    {
      await blob.UploadAsync(image, new BlobUploadOptions
      {
        HttpHeaders = new BlobHttpHeaders { ContentType = "image/webp", CacheControl = "public, max-age=604800" },
        Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All }
      }, cancellationToken);
    }
    catch (RequestFailedException ex) when (ex.ErrorCode is "BlobAlreadyExists" or "ConditionNotMet")
    {
      created = false;
      image = (await blob.DownloadContentAsync(cancellationToken)).Value.Content;
    }

    await UploadUnitThumbnailsAsync(unit.RowKey, image, cancellationToken);
    await SetUnitImageFlagAsync(unit, true, cancellationToken);
    return created;
  }

  private async Task UploadUnitThumbnailsAsync(string unitId, BinaryData image, CancellationToken cancellationToken)
  {
    using var original = SKBitmap.Decode(image.ToArray()) ?? throw new InvalidOperationException("The unit image could not be decoded.");
    foreach (var width in ImageWidths.Take(2))
    {
      using var thumbnail = original.Resize(new SKSizeI(width, width / 3), new SKSamplingOptions(SKCubicResampler.Mitchell))
        ?? throw new InvalidOperationException("The unit image could not be resized.");
      using var encoded = thumbnail.Encode(SKEncodedImageFormat.Webp, 80)
        ?? throw new InvalidOperationException("The unit thumbnail could not be encoded.");
      using var stream = encoded.AsStream();
      await _imagesClient.GetBlobClient($"{unitId}.{width}.webp").UploadAsync(stream, new BlobUploadOptions
      {
        HttpHeaders = new BlobHttpHeaders { ContentType = "image/webp", CacheControl = "public, max-age=604800" }
      }, cancellationToken);
    }
  }

  public async Task DeleteUnitImageAsync(UnitEntity unit, CancellationToken cancellationToken = default)
  {
    ArgumentNullException.ThrowIfNull(unit);
    foreach (var width in ImageWidths)
      await _imagesClient.GetBlobClient($"{unit.RowKey}.{width}.webp").DeleteIfExistsAsync(cancellationToken: cancellationToken);
    await SetUnitImageFlagAsync(unit, false, cancellationToken);
  }

  public async Task UpdateUnitImageSceneSummaryAsync(UnitEntity unit, string summary, CancellationToken cancellationToken = default)
  {
    ArgumentNullException.ThrowIfNull(unit);
    var entity = new TableEntity(unit.PartitionKey, unit.RowKey) { [nameof(UnitEntity.ImageSceneSummary)] = summary };
    await _unitsClient.UpdateEntityAsync(entity, ETag.All, TableUpdateMode.Merge, cancellationToken);
    unit.ImageSceneSummary = summary;
  }

  private async Task SetUnitImageFlagAsync(UnitEntity unit, bool hasImage, CancellationToken cancellationToken)
  {
    var entity = new TableEntity(unit.PartitionKey, unit.RowKey) { [nameof(UnitEntity.HasImage)] = hasImage };
    if (!hasImage)
      entity[nameof(UnitEntity.ImageSceneSummary)] = string.Empty;
    await _unitsClient.UpdateEntityAsync(entity, ETag.All, TableUpdateMode.Merge, cancellationToken);
    unit.HasImage = hasImage;
    if (!hasImage)
      unit.ImageSceneSummary = string.Empty;
    if (hasImage)
      unit.Timestamp = (await _unitsClient.GetEntityAsync<UnitEntity>(unit.PartitionKey, unit.RowKey, cancellationToken: cancellationToken)).Value.Timestamp;
    PopulateImageUrl(unit);
  }

  private void PopulateImageUrl(UnitEntity unit)
  {
    if (!unit.HasImage)
    {
      unit.ImageUrl = null;
      unit.ImageSrcSet = null;
      return;
    }

    var images = ImageWidths.Select(width => new
    {
      Width = width,
      Url = new UriBuilder(_imagesClient.GetBlobClient($"{unit.RowKey}.{width}.webp").Uri) { Query = $"v={unit.Timestamp?.UtcTicks}" }.Uri.AbsoluteUri
    }).ToList();
    unit.ImageUrl = images[^1].Url;
    unit.ImageSrcSet = string.Join(", ", images.Select(image => $"{image.Url} {image.Width}w"));
  }

  public async Task<T> GetBlobAsync<T>(string unitId, CancellationToken cancellationToken = default) where T : ICurriculumBlob, new()
  {
    var suffix = GetSuffix(typeof(T));
    var blobClient = _blobClient.GetBlobClient($"{unitId}.{suffix}.json");
    try
    {
      var response = await blobClient.DownloadContentAsync(cancellationToken);
      return JsonSerializer.Deserialize<T>(response.Value.Content.ToString(), JsonDefaults.CamelCase) ?? new T();
    }
    catch (RequestFailedException ex) when (ex.Status == 404)
    {
      return new T();
    }
  }

  public Task UploadBlobAsync<T>(string unitId, T curriculumBlob, CancellationToken cancellationToken = default) where T : ICurriculumBlob
  {
    var suffix = GetSuffix(typeof(T));
    var blobClient = _blobClient.GetBlobClient($"{unitId}.{suffix}.json");
    var binaryData = new BinaryData(JsonSerializer.Serialize(curriculumBlob, JsonDefaults.CamelCase));
    return blobClient.UploadAsync(binaryData, overwrite: true, cancellationToken);
  }

  public async Task<CourseEvaluation> TryGetCourseEvaluationAsync(string courseId, CancellationToken cancellationToken = default)
  {
    var blobClient = _blobClient.GetBlobClient($"evaluations/{courseId}.json");
    try
    {
      var response = await blobClient.DownloadContentAsync(cancellationToken);
      var evaluation = JsonSerializer.Deserialize<CourseEvaluation>(response.Value.Content.ToString(), JsonDefaults.CamelCase);
      if (evaluation?.GeneratedAt != default)
      {
        if (evaluation.Overall.GeneratedAt == default)
          evaluation.Overall.GeneratedAt = evaluation.GeneratedAt;

        foreach (var unit in evaluation.Units.Where(o => o.GeneratedAt == default))
          unit.GeneratedAt = evaluation.GeneratedAt;
      }

      return evaluation;
    }
    catch (RequestFailedException ex) when (ex.Status == 404)
    {
      return null;
    }
  }

  public Task UploadCourseEvaluationAsync(string courseId, CourseEvaluation evaluation, CancellationToken cancellationToken = default)
  {
    var blobClient = _blobClient.GetBlobClient($"evaluations/{courseId}.json");
    var binaryData = new BinaryData(JsonSerializer.Serialize(evaluation, JsonDefaults.CamelCase));
    return blobClient.UploadAsync(binaryData, overwrite: true, cancellationToken);
  }

  private void PopulateLeaderNames(CourseEntity course)
  {
    course.LeaderNames = string.Join(", ", course.LeadersList
      .Select(email => _config.UsersByEmail.TryGetValue(email, out var user) ? user.DisplayName : null)
      .Where(name => name is not null));
  }

  private static string GetSuffix(Type type)
  {
    if (type == typeof(KeyKnowledge)) return "knowledge";
    if (type == typeof(Assessment)) return "assessment";
    if (type == typeof(QuestionBank)) return "questions";
    throw new NotImplementedException();
  }
}

