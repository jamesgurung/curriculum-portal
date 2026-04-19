using Azure.Identity;
using Microsoft.Graph;
using Microsoft.Graph.Beta;
using Microsoft.Graph.Beta.Models;
using Microsoft.Kiota.Abstractions.Serialization;

namespace CurriculumPortal;

public class TeamsService : IDisposable
{
  private readonly string _prefix;
  private readonly string _website;
  private readonly GraphServiceClient _client;
  private static readonly TimeOnly _8am = new(8, 0);
  private static readonly TimeZoneInfo _ukTime = TimeZoneInfo.FindSystemTimeZoneById("GMT Standard Time");
  private bool disposedValue;

  public TeamsService(AppOptions options)
  {
    ArgumentNullException.ThrowIfNull(options);
    _prefix = options.MicrosoftTeamsPrefix;
    _website = options.Website;
    _client = new(new ClientSecretCredential(options.MicrosoftTenantId, options.MicrosoftClientId, options.MicrosoftClientSecret));
  }

  public async Task SetAssignments(DateOnly dueDate, HashSet<string> classNames)
  {
    var classes = await GetClassesAsync(classNames);
    var title = "Homework: Complete knowledge quizzes";
    var description = $"<html><body><div><b>Answer all your subject quizzes and keep persevering until you achieve 100%.</b></div><div><br></div><div>To get started, go to the <a target=\"_blank\" href=\"{_website}/assignments\">Curriculum Portal</a> and sign in with your school Microsoft account.<br></div></body></html>";
    var due = new DateTimeOffset(new DateTime(dueDate, _8am), _ukTime.GetUtcOffset(new DateTime(dueDate, _8am)));

    foreach (var cls in classes)
    {
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
