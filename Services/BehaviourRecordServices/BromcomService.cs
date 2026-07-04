using Azure;
using BromcomEssentials;
using System.Globalization;
using System.Text.Json;

namespace CurriculumPortal;

public sealed class BromcomService : IBehaviourRecordService
{
  private readonly string _applicationId;
  private readonly string _applicationSecret;
  private readonly int _schoolId;
  private readonly ConfigService _config;
  private readonly CourseService _courseService;
  private readonly IHttpClientFactory _httpClientFactory;

  public BromcomService(AppOptions options, ConfigService config, CourseService courseService, IHttpClientFactory httpClientFactory)
  {
    ArgumentNullException.ThrowIfNull(options);
    ArgumentNullException.ThrowIfNull(config);
    ArgumentNullException.ThrowIfNull(courseService);
    ArgumentNullException.ThrowIfNull(httpClientFactory);

    _applicationId = options.BromcomApplicationId;
    _applicationSecret = options.BromcomApplicationSecret;
    if (!int.TryParse(options.BromcomSchoolId, NumberStyles.Integer, CultureInfo.InvariantCulture, out _schoolId))
      throw new InvalidOperationException("Bromcom school ID must be a valid integer before issuing behaviours.");

    _config = config;
    _courseService = courseService;
    _httpClientFactory = httpClientFactory;
  }

  public async Task<(int Positive, int Negative)> IssueBehaviours(Dictionary<string, List<User>> positiveStudentsByBehaviour, Dictionary<string, List<User>> negativeStudentsByBehaviour)
  {
    ArgumentNullException.ThrowIfNull(positiveStudentsByBehaviour);
    ArgumentNullException.ThrowIfNull(negativeStudentsByBehaviour);

    if (positiveStudentsByBehaviour.Values.Sum(o => o.Count) == 0 && negativeStudentsByBehaviour.Values.Sum(o => o.Count) == 0) return (0, 0);
    if (string.IsNullOrWhiteSpace(_applicationId) || string.IsNullOrWhiteSpace(_applicationSecret))
      throw new InvalidOperationException("Bromcom application ID and secret must be configured before issuing behaviours.");

    var behaviours = await GetBromcomBehavioursAsync();
    using var httpClient = _httpClientFactory.CreateClient();
    using var client = new BromcomClient(_applicationId, _applicationSecret, httpClient);

    var subjectNames = await GetSubjectNamesByCodeAsync();
    var positiveCount = 0;
    foreach (var group in positiveStudentsByBehaviour.Where(o => o.Value.Count > 0))
    {
      positiveCount += await IssueBehaviourAsync(client, group.Value, behaviours.StaffId, behaviours.Positive, GetComment(group.Key, subjectNames, false));
    }

    var negativeCount = 0;
    foreach (var group in negativeStudentsByBehaviour.Where(o => o.Value.Count > 0))
    {
      negativeCount += await IssueBehaviourAsync(client, group.Value, behaviours.StaffId, behaviours.Negative, GetComment(group.Key, subjectNames, true));
    }

    return (positiveCount, negativeCount);
  }

  private async Task<BromcomBehaviourSettings> GetBromcomBehavioursAsync()
  {
    try
    {
      return ParseBromcomBehaviours(await _config.ReadBlobAsync("bromcom-behaviours.json"));
    }
    catch (RequestFailedException ex)
    {
      throw new InvalidOperationException("Bromcom behaviour settings are not configured. Upload bromcom-behaviours.json to the config container.", ex);
    }
    catch (InvalidOperationException ex)
    {
      throw new InvalidOperationException("Bromcom behaviour settings are invalid. Check bromcom-behaviours.json in the config container.", ex);
    }
  }

  private async Task<Dictionary<string, string>> GetSubjectNamesByCodeAsync()
  {
    return (await _courseService.ListCoursesAsync())
      .Where(o => !string.IsNullOrWhiteSpace(o.SubjectCode) && !string.IsNullOrWhiteSpace(o.Name))
      .GroupBy(o => o.SubjectCode.Trim(), StringComparer.OrdinalIgnoreCase)
      .ToDictionary(g => g.Key, g => g.First().Name.Trim(), StringComparer.OrdinalIgnoreCase);
  }

  private async Task<int> IssueBehaviourAsync(BromcomClient client, IEnumerable<User> students, int staffId, BromcomBehaviourConfig behaviour, string comment)
  {
    var count = 0;
    foreach (var student in students.DistinctBy(o => o.Id))
    {
      await client.SetBehaviourEventAsync(_schoolId, new BehaviourEvent
      {
        StudentId = student.Id,
        EventTypeId = behaviour.EventTypeId,
        StaffId = staffId,
        ClassId = null,
        LocationId = null,
        Date = DateTime.Now,
        Points = behaviour.Points,
        Comment = comment,
        InternalComment = "Issued automatically by the Curriculum Portal"
      }, CancellationToken.None);
      count++;
    }

    return count;
  }

  private static string GetComment(string behaviourCode, Dictionary<string, string> subjectNames, bool isNegative)
  {
    var completionWord = isNegative ? "incomplete" : "complete";
    if (behaviourCode.Equals("KS3", StringComparison.OrdinalIgnoreCase)) return $"Knowledge quizzes {completionWord}";

    return $"{(subjectNames.TryGetValue(behaviourCode, out var subjectName) ? subjectName : behaviourCode)} knowledge quiz {completionWord}";
  }

  private static BromcomBehaviourSettings ParseBromcomBehaviours(string json)
  {
    try
    {
      var settings = JsonSerializer.Deserialize<BromcomBehaviourSettings>(json, JsonDefaults.CamelCase) ?? throw new InvalidOperationException("bromcom-behaviours.json is invalid.");
      if (settings.StaffId <= 0) throw new InvalidOperationException("bromcom-behaviours.json is missing a valid staffId.");
      return new BromcomBehaviourSettings
      {
        StaffId = settings.StaffId,
        Positive = ValidateBehaviour(settings.Positive, "positive"),
        Negative = ValidateBehaviour(settings.Negative, "negative")
      };
    }
    catch (JsonException ex)
    {
      throw new InvalidOperationException("bromcom-behaviours.json is invalid.", ex);
    }
  }

  private static BromcomBehaviourConfig ValidateBehaviour(BromcomBehaviourConfig behaviour, string name)
  {
    if (behaviour is null || behaviour.EventTypeId <= 0) throw new InvalidOperationException($"bromcom-behaviours.json is missing a valid '{name}' entry.");

    return behaviour;
  }
}

internal sealed class BromcomBehaviourSettings
{
  public int StaffId { get; set; }
  public BromcomBehaviourConfig Positive { get; set; } = new();
  public BromcomBehaviourConfig Negative { get; set; } = new();
}

internal sealed class BromcomBehaviourConfig
{
  public int EventTypeId { get; set; }
  public int Points { get; set; }
}
