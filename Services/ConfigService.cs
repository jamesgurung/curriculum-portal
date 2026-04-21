using Azure;
using Azure.Storage.Blobs;
using CsvHelper;
using CsvHelper.Configuration;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace CurriculumPortal;

public partial class ConfigService(AppOptions options)
{
  private static readonly SemaphoreSlim _semaphore = new(1, 1);
  private readonly BlobContainerClient _configClient = new BlobServiceClient(options.StorageAccountConnectionString).GetBlobContainerClient("config");
  private readonly ImmutableHashSet<string> _adminEmails = options.AdminEmails.ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);
  private volatile bool _isLoaded;
  private volatile bool _classChartsBehavioursLoaded;
  private ClassChartsBehaviourSet _classChartsBehaviours = new();

  public byte[] SchoolLogoBytes { get; private set; }
  public IReadOnlyDictionary<string, User> UsersByEmail { get; private set; } = new Dictionary<string, User>(StringComparer.OrdinalIgnoreCase);
  public IReadOnlyList<User> Teachers { get; private set; }
  public IReadOnlyList<User> Students { get; private set; }
  public IReadOnlyList<Holiday> Holidays { get; private set; } = [];
  public HashSet<int> Exemptions { get; private set; } = [];
  public IReadOnlyList<ChecklistItemConfig> ChecklistItems { get; private set; } = [];

  public Task LoadAsync()
  {
    return LoadAsync(false);
  }

  public Task ReloadAsync()
  {
    return LoadAsync(true);
  }

  private async Task LoadAsync(bool forceRefresh)
  {
    if (_isLoaded && !forceRefresh) return;
    await _semaphore.WaitAsync();
    try
    {
      Teachers = ParseUsers(await ReadBlobAsync("teachers.csv"), true);
      Students = ParseUsers(await ReadBlobAsync("students.csv"), false);
      Holidays = ParseHolidays(await ReadBlobAsync("holidays.csv"));
      Exemptions = ParseExemptions(await ReadBlobAsync("exemptions.csv"));
      ChecklistItems = ParseChecklistItems(await ReadBlobAsync("checklist.json"));
      UsersByEmail = Teachers.Concat(Students).ToDictionary(o => o.Email, o => o, StringComparer.OrdinalIgnoreCase);

      var logoResponse = await _configClient.GetBlobClient("school-logo.png").DownloadContentAsync();
      SchoolLogoBytes = logoResponse.Value.Content.ToArray();

      if (forceRefresh)
      {
        _classChartsBehavioursLoaded = false;
        _classChartsBehaviours = new();
      }

      _isLoaded = true;
    }
    finally
    {
      _semaphore.Release();
    }
  }

  public async Task UpdateDataFileAsync(string fileName, string content)
  {
    ArgumentNullException.ThrowIfNull(fileName);
    ArgumentNullException.ThrowIfNull(content);
    var blobClient = _configClient.GetBlobClient(fileName);
    using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));
    await blobClient.UploadAsync(stream, true);
  }

  public async Task<ClassChartsBehaviourSet> GetClassChartsBehavioursAsync()
  {
    if (_classChartsBehavioursLoaded) return _classChartsBehaviours;
    await _semaphore.WaitAsync();
    try
    {
      if (_classChartsBehavioursLoaded) return _classChartsBehaviours;

      try
      {
        _classChartsBehaviours = ParseClassChartsBehaviours(await ReadBlobAsync("classcharts-behaviours.json"));
        _classChartsBehavioursLoaded = true;
        return _classChartsBehaviours;
      }
      catch (RequestFailedException ex)
      {
        throw new InvalidOperationException("Class Charts behaviour settings are not configured. Upload classcharts-behaviours.json to the config container.", ex);
      }
      catch (InvalidOperationException ex)
      {
        throw new InvalidOperationException("Class Charts behaviour settings are invalid. Check classcharts-behaviours.json in the config container.", ex);
      }
    }
    finally
    {
      _semaphore.Release();
    }
  }

  private async Task<string> ReadBlobAsync(string blobName)
  {
    var response = await _configClient.GetBlobClient(blobName).DownloadContentAsync();
    return Encoding.UTF8.GetString(response.Value.Content.ToArray());
  }

  private List<User> ParseUsers(string csv, bool isTeacher)
  {
    var config = CreateCsvConfiguration();
    using var reader = new StringReader(csv);
    using var csvReader = new CsvReader(reader, config);
    var records = new List<User>();
    csvReader.Read();
    csvReader.ReadHeader();
    while (csvReader.Read())
    {
      if (!int.TryParse(csvReader.GetField("Id"), out var id)) continue;
      var email = csvReader.GetField("Email").Trim().ToLowerInvariant();
      if (email.Length == 0) continue;

      records.Add(new User
      {
        Id = id,
        Email = email,
        FirstName = (csvReader.GetField("FirstName") ?? string.Empty).Trim(),
        LastName = (csvReader.GetField("LastName") ?? string.Empty).Trim(),
        TutorGroup = (csvReader.GetField("TutorGroup") ?? string.Empty).Trim(),
        Classes = csvReader.GetField("Classes").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(o => o.Length > 0)
          .Distinct(StringComparer.OrdinalIgnoreCase).Order().ToList(),
        IsTeacher = isTeacher,
        IsAdmin = isTeacher && _adminEmails.Contains(email)
      });
    }

    return records.OrderBy(o => o.LastName).ThenBy(o => o.FirstName).ToList();
  }

  private static List<Holiday> ParseHolidays(string csv)
  {
    using var reader = new StringReader(csv);
    using var csvReader = new CsvReader(reader, CreateCsvConfiguration());
    var records = new List<Holiday>();
    csvReader.Read();
    csvReader.ReadHeader();
    while (csvReader.Read())
    {
      if (!DateOnly.TryParseExact(csvReader.GetField("Start"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var start)
        || !DateOnly.TryParseExact(csvReader.GetField("End"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var end)
        || end < start) continue;

      records.Add(new Holiday
      {
        Start = start,
        End = end
      });
    }

    return records.OrderBy(o => o.Start).ToList();
  }

  private static HashSet<int> ParseExemptions(string csv)
  {
    if (string.IsNullOrWhiteSpace(csv)) return [];

    using var reader = new StringReader(csv.Trim());
    using var csvReader = new CsvReader(reader, CreateCsvConfiguration());
    var records = new HashSet<int>();
    if (!csvReader.Read()) return records;

    csvReader.ReadHeader();
    while (csvReader.Read())
    {
      if (!int.TryParse(csvReader.GetField("UserId"), out var userId)) continue;
      records.Add(userId);
    }

    return records;
  }

  private static ClassChartsBehaviourSet ParseClassChartsBehaviours(string json)
  {
    try
    {
      var blob = JsonSerializer.Deserialize<Dictionary<string, ClassChartsBehaviourConfig>>(json, JsonDefaults.CamelCase) ?? throw new InvalidOperationException("classcharts-behaviours.json is invalid.");
      return new ClassChartsBehaviourSet
      {
        Positive = ValidateBehaviour(blob.TryGetValue("positive", out var positive) ? positive : null, "positive"),
        Negative = ValidateBehaviour(blob.TryGetValue("negative", out var negative) ? negative : null, "negative")
      };
    }
    catch (JsonException ex)
    {
      throw new InvalidOperationException("classcharts-behaviours.json is invalid.", ex);
    }
  }

  private static ClassChartsBehaviourConfig ValidateBehaviour(ClassChartsBehaviourConfig behaviour, string name)
  {
    if (behaviour is null || behaviour.Id <= 0 || string.IsNullOrWhiteSpace(behaviour.Reason) || string.IsNullOrWhiteSpace(behaviour.Icon)) throw new InvalidOperationException($"classcharts-behaviours.json is missing a valid '{name}' entry.");

    return behaviour;
  }

  private static List<ChecklistItemConfig> ParseChecklistItems(string json)
  {
    try
    {
      var items = JsonSerializer.Deserialize<List<ChecklistItemConfig>>(json, JsonDefaults.CamelCase) ?? throw new InvalidOperationException("checklist.json is invalid.");
      var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
      foreach (var item in items)
      {
        if (string.IsNullOrWhiteSpace(item?.Id) || string.IsNullOrWhiteSpace(item.Title))
        {
          throw new InvalidOperationException("checklist.json contains an item without a valid id/title.");
        }

        item.Id = item.Id.Trim();
        item.Title = item.Title.Trim();
        if (!item.Id.All(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_'))
        {
          throw new InvalidOperationException($"checklist.json contains an invalid id '{item.Id}'. Checklist ids may only contain letters, numbers, hyphens, and underscores.");
        }

        if (!ids.Add(item.Id))
        {
          throw new InvalidOperationException($"checklist.json contains a duplicate id '{item.Id}'.");
        }
      }

      return items;
    }
    catch (JsonException ex)
    {
      throw new InvalidOperationException("checklist.json is invalid.", ex);
    }
  }

  private static CsvConfiguration CreateCsvConfiguration()
  {
    return new CsvConfiguration(CultureInfo.InvariantCulture)
    {
      HasHeaderRecord = true,
      MissingFieldFound = null,
      TrimOptions = TrimOptions.Trim,
      HeaderValidated = null,
      IgnoreBlankLines = true
    };
  }

}
