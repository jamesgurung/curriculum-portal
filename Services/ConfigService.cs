using Azure.Storage.Blobs;
using CsvHelper;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace CurriculumPortal;

public class ConfigService(AppOptions options, BlobServiceClient blobServiceClient)
{
  private static readonly SemaphoreSlim LoadLock = new(1, 1);
  private readonly BlobContainerClient _configClient = blobServiceClient.GetBlobContainerClient("config");
  private readonly ImmutableHashSet<string> _adminEmails = options.AdminEmails.Split(',').Select(o => o.Trim()).ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);
  private volatile bool _isLoaded;

  public byte[] SchoolLogoBytes { get; private set; }
  public byte[] SchoolNavbarLogoBytes { get; private set; }
  public IReadOnlyDictionary<string, User> UsersByEmail { get; private set; } = new Dictionary<string, User>(StringComparer.OrdinalIgnoreCase);
  public IReadOnlyList<User> Teachers { get; private set; }
  public IReadOnlyList<User> Students { get; private set; }
  public IReadOnlySet<string> SeniorLeaders { get; private set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
  public IReadOnlyList<Holiday> Holidays { get; private set; } = [];
  public HashSet<int> Exemptions { get; private set; } = [];
  public IReadOnlyList<ChecklistItemConfig> ChecklistItems { get; private set; } = [];

  public Task LoadAsync() => LoadAsync(false);

  public Task ReloadAsync() => LoadAsync(true);

  private async Task LoadAsync(bool forceRefresh)
  {
    if (_isLoaded && !forceRefresh) return;
    await LoadLock.WaitAsync();
    try
    {
      if (_isLoaded && !forceRefresh) return;

      Teachers = ParseUsers(await ReadBlobAsync("teachers.csv"), true);
      Students = ParseUsers(await ReadBlobAsync("students.csv"), false);
      SeniorLeaders = ParseEmailList(await ReadBlobAsync("seniorleaders.csv"));
      Holidays = ParseHolidays(await ReadBlobAsync("holidays.csv"));
      Exemptions = ParseExemptions(await ReadBlobAsync("exemptions.csv"));
      ChecklistItems = ParseChecklistItems(await ReadBlobAsync("checklist.json"));
      UsersByEmail = Teachers.Concat(Students).ToDictionary(o => o.Email, o => o, StringComparer.OrdinalIgnoreCase);

      var logoResponse = await _configClient.GetBlobClient("school-logo.png").DownloadContentAsync();
      SchoolLogoBytes = logoResponse.Value.Content.ToArray();
      var navbarLogoResponse = await _configClient.GetBlobClient("school-logo-navbar.png").DownloadContentAsync();
      SchoolNavbarLogoBytes = navbarLogoResponse.Value.Content.ToArray();

      _isLoaded = true;
    }
    finally
    {
      LoadLock.Release();
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

  public async Task<string> ReadBlobAsync(string blobName)
  {
    var response = await _configClient.GetBlobClient(blobName).DownloadContentAsync();
    return Encoding.UTF8.GetString(response.Value.Content.ToArray());
  }

  private List<User> ParseUsers(string csv, bool isTeacher)
  {
    using var reader = new StringReader(csv);
    using var csvReader = CsvReader.Create(reader, CreateCsvReaderOptions());
    var records = new List<User>();
    csvReader.ReadHeader();
    while (csvReader.Read())
    {
      if (!int.TryParse(GetCsvField(csvReader, "Id"), out var id)) continue;
      var email = GetCsvField(csvReader, "Email").Trim().ToLowerInvariant();
      if (email.Length == 0) continue;

      var classes = GetCsvField(csvReader, "Classes").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(o => o.Length > 0)
        .Distinct(StringComparer.OrdinalIgnoreCase).Order().ToList();
      var tutorGroup = isTeacher ? null : (GetCsvField(csvReader, "TutorGroup") ?? string.Empty).Trim();
      var pupilPremium = !isTeacher && bool.TryParse(GetCsvField(csvReader, "PupilPremium"), out var parsedPupilPremium) && parsedPupilPremium;

      records.Add(new User
      {
        Id = id,
        Email = email,
        FirstName = (GetCsvField(csvReader, "FirstName") ?? string.Empty).Trim(),
        LastName = (GetCsvField(csvReader, "LastName") ?? string.Empty).Trim(),
        TutorGroup = isTeacher ? GetTeacherTutorGroup(classes) : tutorGroup,
        Classes = classes,
        IsTeacher = isTeacher,
        PupilPremium = pupilPremium,
        IsAdmin = isTeacher && _adminEmails.Contains(email)
      });
    }

    return records.OrderBy(o => o.LastName).ThenBy(o => o.FirstName).ToList();
  }

  private static string GetTeacherTutorGroup(IEnumerable<string> classes)
  {
    var tutorClass = classes.FirstOrDefault(o => o.Contains("/Tu", StringComparison.OrdinalIgnoreCase));
    if (string.IsNullOrWhiteSpace(tutorClass)) return string.Empty;

    var suffixIndex = tutorClass.IndexOf("/Tu", StringComparison.OrdinalIgnoreCase);
    return tutorClass[..suffixIndex].Trim();
  }

  private static List<Holiday> ParseHolidays(string csv)
  {
    using var reader = new StringReader(csv);
    using var csvReader = CsvReader.Create(reader, CreateCsvReaderOptions());
    var records = new List<Holiday>();
    csvReader.ReadHeader();
    while (csvReader.Read())
    {
      if (!DateOnly.TryParseExact(GetCsvField(csvReader, "Start"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var start)
        || !DateOnly.TryParseExact(GetCsvField(csvReader, "End"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var end)
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
    using var csvReader = CsvReader.Create(reader, CreateCsvReaderOptions());
    var records = new HashSet<int>();

    csvReader.ReadHeader();
    while (csvReader.Read())
    {
      if (!int.TryParse(GetCsvField(csvReader, "UserId"), out var userId)) continue;
      records.Add(userId);
    }

    return records;
  }

  private static HashSet<string> ParseEmailList(string csv)
  {
    if (string.IsNullOrWhiteSpace(csv)) return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    using var reader = new StringReader(csv.Trim());
    using var csvReader = CsvReader.Create(reader, CreateCsvReaderOptions());
    var records = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    csvReader.ReadHeader();
    while (csvReader.Read())
    {
      var email = (GetCsvField(csvReader, "Email") ?? string.Empty).Trim().ToLowerInvariant();
      if (email.Length == 0) continue;
      records.Add(email);
    }

    return records;
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

  private static string GetCsvField(CsvReader reader, string name) =>
    reader.TryGetFieldIndex(name, out var index) && index < reader.FieldCount ? reader.GetString(index) : null;

  private static CsvReaderOptions CreateCsvReaderOptions()
  {
    return new CsvReaderOptions
    {
      Separator = ',',
      HasHeader = true,
      TrimWhitespace = true,
      WhitespaceCharacters = " ",
      MalformedData = CsvMalformedDataHandling.Throw,
      SkipEmptyLines = true,
      FormatProvider = CultureInfo.InvariantCulture
    };
  }

}
