using Azure;
using Azure.Data.Tables;
using Azure.Storage.Blobs;
using System.Text.Json;

namespace CurriculumPortal;

public class CourseService
{
  private readonly ConfigService _config;
  private readonly BlobContainerClient _blobClient;
  private readonly TableClient _coursesClient;
  private readonly TableClient _unitsClient;
  private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

  public CourseService(AppOptions options, ConfigService config)
  {
    ArgumentNullException.ThrowIfNull(options);
    _blobClient = new BlobServiceClient(options.StorageAccountConnectionString).GetBlobContainerClient("curriculum");
    var tableServiceClient = new TableServiceClient(options.StorageAccountConnectionString);
    _coursesClient = tableServiceClient.GetTableClient("courses");
    _unitsClient = tableServiceClient.GetTableClient("units");
    _config = config;
  }

  public async Task<List<CourseEntity>> ListCoursesAsync(CancellationToken cancellationToken = default)
  {
    var courses = await _coursesClient.QueryAsync<CourseEntity>(cancellationToken: cancellationToken).ToListAsync(cancellationToken);
    foreach (var course in courses)
    {
      course.LeaderNames = string.Join(", ", course.LeadersList
        .Select(o => _config.UsersByEmail.TryGetValue(o, out var t) ? t.DisplayName : null)
        .Where(o => o is not null));
    }
    return courses.OrderBy(o => o.KeyStage).ThenBy(o => o.Name).ToList();
  }

  public async Task<List<UnitEntity>> ListUnitsAsync(string courseId = null, CancellationToken cancellationToken = default)
  {
    var units = courseId is null
      ? await _unitsClient.QueryAsync<UnitEntity>(cancellationToken: cancellationToken).ToListAsync(cancellationToken)
      : await _unitsClient.QueryAsync<UnitEntity>(u => u.PartitionKey == courseId, cancellationToken: cancellationToken).ToListAsync(cancellationToken);

    return units.OrderBy(o => o.PartitionKey).ThenBy(o => o.YearGroup).ThenBy(o => o.Term).ThenBy(o => o.Order).ThenBy(o => o.Title).ToList();
  }

  public async Task<CourseEntity> TryGetCourseAsync(string courseId, CancellationToken cancellationToken = default)
  {
    try
    {
      var response = await _coursesClient.GetEntityAsync<CourseEntity>("course", courseId, cancellationToken: cancellationToken);
      response.Value.LeaderNames = string.Join(", ", response.Value.LeadersList
        .Select(o => _config.UsersByEmail.TryGetValue(o, out var t) ? t.DisplayName : null)
        .Where(o => o is not null));
      return response.Value;
    }
    catch (RequestFailedException ex) when (ex.Status == 404)
    {
      return null;
    }
  }

  public async Task<UnitEntity> TryGetUnitAsync(string courseId, string unitId, CancellationToken cancellationToken = default)
  {
    try
    {
      var response = await _unitsClient.GetEntityAsync<UnitEntity>(courseId, unitId, cancellationToken: cancellationToken);
      return response.Value;
    }
    catch (RequestFailedException ex) when (ex.Status == 404)
    {
      return null;
    }
  }

  public Task UpdateCourseAsync(CourseEntity course)
  {
    return _coursesClient.UpsertEntityAsync(course, TableUpdateMode.Replace);
  }

  public Task UpdateUnitAsync(UnitEntity unit, CancellationToken cancellationToken = default)
  {
    return _unitsClient.UpsertEntityAsync(unit, TableUpdateMode.Replace, cancellationToken);
  }

  public async Task DeleteUnitAsync(string courseId, string unitId)
  {
    await _unitsClient.DeleteEntityAsync(courseId, unitId);
    await _blobClient.GetBlobClient(unitId + ".knowledge.json").DeleteIfExistsAsync();
    await _blobClient.GetBlobClient(unitId + ".assessment.json").DeleteIfExistsAsync();
    await _blobClient.GetBlobClient(unitId + ".questions.json").DeleteIfExistsAsync();
  }

  public async Task<T> GetBlobAsync<T>(string unitId, CancellationToken cancellationToken = default) where T : ICurriculumBlob, new()
  {
    var suffix = GetSuffix(typeof(T));
    var blobClient = _blobClient.GetBlobClient($"{unitId}.{suffix}.json");
    try
    {
      var response = await blobClient.DownloadContentAsync(cancellationToken);
      return JsonSerializer.Deserialize<T>(response.Value.Content.ToString(), JsonOptions) ?? new T();
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
    var binaryData = new BinaryData(JsonSerializer.Serialize(curriculumBlob, JsonOptions));
    return blobClient.UploadAsync(binaryData, overwrite: true, cancellationToken);
  }

  public async Task<CourseEvaluation> TryGetCourseEvaluationAsync(string courseId, CancellationToken cancellationToken = default)
  {
    var blobClient = _blobClient.GetBlobClient($"evaluations/{courseId}.json");
    try
    {
      var response = await blobClient.DownloadContentAsync(cancellationToken);
      return JsonSerializer.Deserialize<CourseEvaluation>(response.Value.Content.ToString(), JsonOptions);
    }
    catch (RequestFailedException ex) when (ex.Status == 404)
    {
      return null;
    }
  }

  public Task UploadCourseEvaluationAsync(string courseId, CourseEvaluation evaluation, CancellationToken cancellationToken = default)
  {
    var blobClient = _blobClient.GetBlobClient($"evaluations/{courseId}.json");
    var binaryData = new BinaryData(JsonSerializer.Serialize(evaluation, JsonOptions));
    return blobClient.UploadAsync(binaryData, overwrite: true, cancellationToken);
  }

  private static string GetSuffix(Type type)
  {
    if (type == typeof(KeyKnowledge)) return "knowledge";
    if (type == typeof(Assessment)) return "assessment";
    if (type == typeof(QuestionBank)) return "questions";
    throw new NotImplementedException();
  }
}

