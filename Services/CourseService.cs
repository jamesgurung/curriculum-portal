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

  public CourseService(BlobServiceClient blobServiceClient, TableServiceClient tableServiceClient, ConfigService config)
  {
    ArgumentNullException.ThrowIfNull(blobServiceClient);
    ArgumentNullException.ThrowIfNull(tableServiceClient);
    _blobClient = blobServiceClient.GetBlobContainerClient("curriculum");
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
    return response.HasValue ? response.Value : null;
  }

  public Task UpdateCourseAsync(CourseEntity course, CancellationToken cancellationToken = default) =>
    _coursesClient.UpsertEntityAsync(course, TableUpdateMode.Replace, cancellationToken);

  public Task UpdateUnitAsync(UnitEntity unit, CancellationToken cancellationToken = default) =>
    _unitsClient.UpsertEntityAsync(unit, TableUpdateMode.Replace, cancellationToken);

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

