using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace CurriculumPortal;

public sealed partial class ClassChartsService
{
  private const string BaseUrl = "https://www.classcharts.com";
  private readonly string _email;
  private readonly string _password;
  private readonly ConfigService _config;

  public ClassChartsService(AppOptions options, ConfigService config)
  {
    ArgumentNullException.ThrowIfNull(options);
    ArgumentNullException.ThrowIfNull(config);
    _email = options.ClassChartsEmail;
    _password = options.ClassChartsPassword;
    _config = config;
  }

  public async Task IssueBehaviours(List<User> positiveStudents, List<User> negativeStudents)
  {
    ArgumentNullException.ThrowIfNull(positiveStudents);
    ArgumentNullException.ThrowIfNull(negativeStudents);

    if (positiveStudents.Count == 0 && negativeStudents.Count == 0) return;
    if (string.IsNullOrWhiteSpace(_email) || string.IsNullOrWhiteSpace(_password))
      throw new InvalidOperationException("Class Charts email and password must be configured before issuing behaviours.");

    var requestedStudents = positiveStudents.Concat(negativeStudents)
      .Select(student => new StudentRequest(student, GetYearGroup(student.TutorGroup)))
      .Where(o => o.YearGroup > 0)
      .DistinctBy(o => o.Student.Id)
      .ToList();
    if (requestedStudents.Count == 0) return;
    var behaviours = await _config.GetClassChartsBehavioursAsync();

    using var handler = new HttpClientHandler
    {
      AllowAutoRedirect = false,
      CookieContainer = new CookieContainer(),
      UseCookies = true,
      CheckCertificateRevocationList = true
    };
    using var client = new HttpClient(handler, disposeHandler: false);

    var session = await LoginAsync(client);
    var lessonPupilIds = await GetLessonPupilIdsAsync(client, requestedStudents.Select(o => o.YearGroup));

    await IssueBehaviourAsync(client, session, positiveStudents, lessonPupilIds, behaviours.Positive);
    await IssueBehaviourAsync(client, session, negativeStudents, lessonPupilIds, behaviours.Negative);
  }

  private async Task<ClassChartsSession> LoginAsync(HttpClient client)
  {
    using (await SendAsync(client, HttpMethod.Get, "/account/login")) { }

    using var loginResponse = await SendAsync(client, HttpMethod.Post, "/account/login",
    [
      new("email", _email),
      new("password", _password),
      new("remember_me", "1"),
      new("recaptcha-token", "no-token-available")
    ], allowNonSuccess: true);
    if ((int)loginResponse.StatusCode >= 400) throw new InvalidOperationException("Class Charts login failed.");

    using var response = await SendAsync(client, HttpMethod.Get, "/lesson");
    var html = await response.Content.ReadAsStringAsync();
    var csrf = ReadRequiredValue(html, "csrf_session", 16, 32, "Class Charts CSRF token");
    var schoolIdIndex = html.IndexOf("csrf_school_id", StringComparison.Ordinal);
    if (schoolIdIndex < 0) throw new InvalidOperationException("Class Charts login failed: missing school ID.");

    var schoolIdStart = schoolIdIndex + 17;
    var schoolIdEnd = html.IndexOf(',', schoolIdStart, StringComparison.Ordinal);
    if (schoolIdEnd < 0 || !int.TryParse(html[schoolIdStart..schoolIdEnd], NumberStyles.Integer, CultureInfo.InvariantCulture, out var schoolId))
    {
      throw new InvalidOperationException("Class Charts login failed: invalid school ID.");
    }

    return new ClassChartsSession(csrf, schoolId);
  }

  private static async Task<Dictionary<string, int>> GetLessonPupilIdsAsync(HttpClient client, IEnumerable<int> yearGroups)
  {
    var ids = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    var duplicateKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var yearGroup in yearGroups.Distinct().Order())
    {
      var offset = 0;
      while (true)
      {
        using var response = await PostAsync(client, "/nolesson/loadpupils",
        [
          new("offset", offset.ToString(CultureInfo.InvariantCulture)),
          new("filter", string.Empty),
          new("year", yearGroup.ToString(CultureInfo.InvariantCulture))
        ]);

        var html = await response.Content.ReadAsStringAsync();
        var pupils = ParsePupils(html, yearGroup);
        if (pupils.Count == 0) break;

        foreach (var pupil in pupils)
        {
          var key = BuildStudentKey(pupil.FirstName, pupil.LastName, pupil.YearGroup);
          if (!ids.TryAdd(key, pupil.Id))
          {
            duplicateKeys.Add(key);
          }
        }

        offset += 100;
      }
    }

    foreach (var duplicateKey in duplicateKeys)
    {
      ids.Remove(duplicateKey);
    }

    return ids;
  }

  private static async Task IssueBehaviourAsync(HttpClient client, ClassChartsSession session, IEnumerable<User> students, Dictionary<string, int> lessonPupilIds, ClassChartsBehaviourConfig behaviour)
  {
    var matchedIds = students
      .Select(student => new StudentRequest(student, GetYearGroup(student.TutorGroup)))
      .Where(o => o.YearGroup > 0)
      .DistinctBy(o => o.Student.Id)
      .Select(o => lessonPupilIds.TryGetValue(BuildStudentKey(o.Student.FirstName, o.Student.LastName, o.YearGroup), out var lessonPupilId) ? lessonPupilId : 0)
      .Where(o => o > 0)
      .Distinct()
      .ToList();
    if (matchedIds.Count == 0) return;

    var body = new List<FormField>
    {
      new("reason", behaviour.Reason),
      new("score", behaviour.Score.ToString(CultureInfo.InvariantCulture)),
      new("behaviour_id", behaviour.Id.ToString(CultureInfo.InvariantCulture)),
      new("icon", behaviour.Icon),
      new("location_id", "0"),
      new("activity_id", "0")
    };
    body.AddRange(matchedIds.Select(id => new FormField("lesson_pupil_id[]", id.ToString(CultureInfo.InvariantCulture))));
    body.AddRange(
    [
      new("detention_default_date", string.Empty),
      new("detention_type_id", "0"),
      new("outcome_id", "0"),
      new("safeguarding", "false"),
      new("require_note", "false"),
      new("require_outcome", "false"),
      new("default_outcome_id", "0"),
      new("csrf", session.Csrf),
      new("csrf_school_id", session.SchoolId.ToString(CultureInfo.InvariantCulture))
    ]);

    using var _ = await PostAsync(client, "/apilesson/addbehaviour", body);
  }

  private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string uri, IReadOnlyList<FormField> fields)
  {
    ArgumentNullException.ThrowIfNull(fields);
    return await SendAsync(client, HttpMethod.Post, uri, fields);
  }

  private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string uri, IReadOnlyList<FormField> fields = null, bool allowNonSuccess = false)
  {
    for (var attempt = 1; attempt <= 4; attempt++)
    {
      try
      {
        using var request = new HttpRequestMessage(method, new Uri(BaseUrl + uri));
        if (fields is not null)
        {
          request.Content = new StringContent(BuildBody(fields), Encoding.UTF8, "application/x-www-form-urlencoded");
        }

        var response = await client.SendAsync(request);
        if (response.IsSuccessStatusCode || allowNonSuccess) return response;

        response.Dispose();
      }
      catch (HttpRequestException) when (attempt < 4)
      {
        await Task.Delay(5000);
      }
    }

    throw new HttpRequestException($"Class Charts request failed for '{uri}'.");
  }

  private static List<PupilLookup> ParsePupils(string html, int yearGroup)
  {
    var pupils = new List<PupilLookup>();
    var searchStart = 0;
    while (true)
    {
      if (!TryReadIntAfter(html, "\"lesson_pupil_id\":", searchStart, out var id, out var cursor)
        || !TryReadStringAfter(html, "\"pupil_firstname\":\"", cursor, out var firstName, out cursor)
        || !TryReadStringAfter(html, "\"pupil_lastname\":\"", cursor, out var lastName, out cursor))
      {
        break;
      }

      pupils.Add(new PupilLookup(id, DecodeValue(firstName), DecodeValue(lastName), yearGroup));
      searchStart = cursor;
    }

    return pupils;
  }

  private static bool TryReadIntAfter(string source, string marker, int searchStart, out int value, out int nextIndex)
  {
    value = 0;
    nextIndex = searchStart;
    var start = source.IndexOf(marker, searchStart, StringComparison.Ordinal);
    if (start < 0) return false;

    start += marker.Length;
    var end = source.IndexOf(',', start, StringComparison.Ordinal);
    if (end < 0 || !int.TryParse(source[start..end], NumberStyles.Integer, CultureInfo.InvariantCulture, out value)) return false;

    nextIndex = end;
    return true;
  }

  private static bool TryReadStringAfter(string source, string marker, int searchStart, out string value, out int nextIndex)
  {
    value = string.Empty;
    nextIndex = searchStart;
    var start = source.IndexOf(marker, searchStart, StringComparison.Ordinal);
    if (start < 0) return false;

    start += marker.Length;
    var end = UnescapedQuoteRegex.Match(source[start..]) is { Success: true } match ? start + match.Index + match.Length - 1 : -1;
    if (end >= source.Length) return false;

    value = source[start..end];
    nextIndex = end;
    return true;
  }

  private static string BuildStudentKey(string firstName, string lastName, int yearGroup)
  {
    return $"{firstName}|{lastName}|{yearGroup}";
  }

  private static string DecodeValue(string value)
  {
    return WebUtility.HtmlDecode(Regex.Unescape(value ?? string.Empty));
  }

  private static int GetYearGroup(string tutorGroup)
  {
    if (string.IsNullOrWhiteSpace(tutorGroup)) return 0;

    var digits = new string(tutorGroup.Trim().TakeWhile(char.IsDigit).ToArray());
    return int.TryParse(digits, out var yearGroup) ? yearGroup : 0;
  }

  private static string BuildBody(IEnumerable<FormField> fields)
  {
    return string.Join("&", fields.Select(field => $"{Encode(field.Name)}={Encode(field.Value)}"));
  }

  private static string Encode(string value)
  {
    return Uri.EscapeDataString(value ?? string.Empty).Replace("%20", "+", StringComparison.Ordinal);
  }

  private static string ReadRequiredValue(string html, string marker, int offset, int length, string description)
  {
    var start = html.IndexOf(marker, StringComparison.Ordinal);
    if (start < 0 || start + offset + length > html.Length) throw new InvalidOperationException($"Class Charts login failed: missing {description}.");

    return html.Substring(start + offset, length);
  }

  private readonly record struct FormField(string Name, string Value);
  private readonly record struct PupilLookup(int Id, string FirstName, string LastName, int YearGroup);
  private readonly record struct StudentRequest(User Student, int YearGroup);
  private readonly record struct ClassChartsSession(string Csrf, int SchoolId);

  [GeneratedRegex(@"(?:^|[^\\])(?:\\\\)*""")]
  private static partial Regex UnescapedQuoteRegex { get; }
}

public sealed class ClassChartsBehaviourConfig
{
  public int Id { get; set; }
  public string Reason { get; set; } = string.Empty;
  public int Score { get; set; }
  public string Icon { get; set; } = string.Empty;
}

public sealed class ClassChartsBehaviourSet
{
  public ClassChartsBehaviourConfig Positive { get; set; } = new();
  public ClassChartsBehaviourConfig Negative { get; set; } = new();
}
