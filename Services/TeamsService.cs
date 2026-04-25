using Azure.Identity;
using Microsoft.Graph;
using Microsoft.Graph.Beta;
using Microsoft.Graph.Beta.Models;
using Microsoft.Kiota.Abstractions.Serialization;
using System.Net;

namespace CurriculumPortal;

public class TeamsService : IDisposable
{
  private readonly string _prefix;
  private readonly string _website;
  private readonly CourseService _courseService;
  private readonly GraphServiceClient _client;
  private static readonly TimeOnly _8am = new(8, 0);
  private static readonly TimeZoneInfo _ukTime = TimeZoneInfo.FindSystemTimeZoneById("GMT Standard Time");
  private bool disposedValue;

  public TeamsService(AppOptions options, CourseService courseService)
  {
    ArgumentNullException.ThrowIfNull(options);
    ArgumentNullException.ThrowIfNull(courseService);
    _prefix = options.MicrosoftTeamsPrefix;
    _website = options.Website;
    _courseService = courseService;
    _client = new(new ClientSecretCredential(options.MicrosoftTenantId, options.MicrosoftClientId, options.MicrosoftClientSecret));
  }

  public async Task SetAssignments(DateOnly dueDate, HashSet<string> classNames)
  {
    var classes = await GetClassesAsync(classNames);
    if (classes.Count == 0) return;

    var coursesBySubjectCode = (await _courseService.ListCoursesAsync())
      .Where(o => !string.IsNullOrWhiteSpace(o.SubjectCode) && !string.IsNullOrWhiteSpace(o.Name))
      .ToLookup(o => o.SubjectCode, StringComparer.OrdinalIgnoreCase);
    var due = new DateTimeOffset(new DateTime(dueDate, _8am), _ukTime.GetUtcOffset(new DateTime(dueDate, _8am)));

    foreach (var cls in classes)
    {
      var subjectName = GetSubjectName(cls.Name, coursesBySubjectCode);
      var title = BuildTitle(subjectName);
      var description = BuildDescription(subjectName);
      var existing = await _client.Education.Classes[cls.Id].Assignments.GetAsync(o =>
        o.QueryParameters.Filter = $"dueDateTime ge {dueDate:yyyy-MM-dd}T00:00:00Z and dueDateTime le {dueDate:yyyy-MM-dd}T23:59:59Z");
      if (existing?.Value?.Any(o => o.DisplayName?.Equals(title, StringComparison.Ordinal) == true) == true) continue;

      var assignment = await _client.Education.Classes[cls.Id].Assignments.PostAsync(new EducationAssignment
      {
        DueDateTime = due,
        DisplayName = title,
        Instructions = new EducationItemBody { ContentType = BodyType.Html, Content = description },
        AddedStudentAction = EducationAddedStudentAction.AssignIfOpen
      });
      if (!string.IsNullOrWhiteSpace(assignment?.Id)) await _client.Education.Classes[cls.Id].Assignments[assignment.Id].Publish.PostAsync();
    }
  }

  private static string BuildTitle(string subjectName)
  {
    return string.IsNullOrWhiteSpace(subjectName)
      ? "Homework: Complete knowledge quizzes"
      : $"Homework: {subjectName} knowledge quiz";
  }

  private string BuildDescription(string subjectName)
  {
    var instruction = string.IsNullOrWhiteSpace(subjectName)
      ? "Answer all your subject quizzes and keep persevering until you achieve 100%."
      : $"Answer your {WebUtility.HtmlEncode(subjectName)} quiz and keep persevering until you achieve 100%.";
    return $"<html><body><div><b>{instruction}</b></div><div><br></div><div>To get started, go to the <a target=\"_blank\" href=\"{_website}/assignments\">Curriculum Portal</a> and sign in with your school Microsoft account.<br></div></body></html>";
  }

  private static string GetSubjectName(string className, ILookup<string, CourseEntity> coursesBySubjectCode)
  {
    if (!TryParseSubjectClass(className, out var yearGroup, out var subjectCode) || yearGroup is < 10 or > 13) return null;

    var keyStage = yearGroup is >= 12 ? 5 : 4;
    return coursesBySubjectCode[subjectCode]
      .OrderBy(o => o.KeyStage == keyStage ? 0 : 1)
      .ThenBy(o => o.KeyStage)
      .ThenBy(o => o.Name, StringComparer.OrdinalIgnoreCase)
      .FirstOrDefault()?.Name ?? GetClassSubjectName(className, subjectCode);
  }

  private static bool TryParseSubjectClass(string className, out int yearGroup, out string subjectCode)
  {
    yearGroup = 0;
    subjectCode = null;
    if (string.IsNullOrWhiteSpace(className)) return false;

    var trimmed = className.Trim();
    var slashIndex = trimmed.IndexOf('/', StringComparison.Ordinal);
    if (slashIndex <= 0 || slashIndex + 2 >= trimmed.Length) return false;

    var yearDigits = new string(trimmed.TakeWhile(char.IsDigit).ToArray());
    if (!int.TryParse(yearDigits, out yearGroup)) return false;

    subjectCode = trimmed.Substring(slashIndex + 1, 2);
    return true;
  }

  private static string GetClassSubjectName(string className, string subjectCode)
  {
    var slashIndex = className.IndexOf('/', StringComparison.Ordinal);
    if (slashIndex < 0 || slashIndex + 1 >= className.Length) return subjectCode;

    var subjectName = new string(className[(slashIndex + 1)..].TakeWhile(c => !char.IsDigit(c)).ToArray()).Trim();
    return string.IsNullOrWhiteSpace(subjectName) ? subjectCode : subjectName;
  }

  private async Task<List<ClassInfo>> GetClassesAsync(HashSet<string> classNames)
  {
    var response = await _client.Education.Classes.GetAsync(o =>
    {
      o.QueryParameters.Filter = $"startswith(externalName,'{_prefix}')";
      o.QueryParameters.Select = ["id", "externalName"];
      o.QueryParameters.Top = 999;
    });
    return (await ReadAllAsync<EducationClass, EducationClassCollectionResponse>(response))
      .Where(o => !string.IsNullOrWhiteSpace(o.Id) && !string.IsNullOrWhiteSpace(o.ExternalName) && o.ExternalName.Length > _prefix.Length)
      .Select(o => new ClassInfo(o.Id, o.ExternalName[_prefix.Length..].Replace('-', '/')))
      .Where(o => classNames.Contains(o.Name, StringComparer.OrdinalIgnoreCase))
      .ToList();
  }

  private async Task<List<TEntity>> ReadAllAsync<TEntity, TCollection>(TCollection response) where TCollection : IParsable, IAdditionalDataHolder, new()
  {
    if (response is null) return [];
    var items = new List<TEntity>();
    var iterator = PageIterator<TEntity, TCollection>.CreatePageIterator(_client, response, o => { items.Add(o); return true; });
    await iterator.IterateAsync();
    return items;
  }

  private sealed record ClassInfo(string Id, string Name);

  protected virtual void Dispose(bool disposing)
  {
    if (!disposedValue)
    {
      if (disposing)
      {
        _client.Dispose();
      }
      disposedValue = true;
    }
  }

  public void Dispose()
  {
    Dispose(disposing: true);
    GC.SuppressFinalize(this);
  }
}
