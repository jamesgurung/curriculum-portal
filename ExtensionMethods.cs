using Azure;
using Azure.Data.Tables;
using System.Security.Claims;

namespace CurriculumPortal;

public static class ExtensionMethods
{
  public static bool CanEditCourse(this ClaimsPrincipal user, CourseEntity course, ConfigService config)
  {
    ArgumentNullException.ThrowIfNull(user);
    ArgumentNullException.ThrowIfNull(course);
    ArgumentNullException.ThrowIfNull(config);

    var email = user.GetEmail();
    return user.IsInRole(Roles.Admin)
      || config.SeniorLeaders.Contains(email)
      || course.LeadersList.Contains(email);
  }

  public static string GetEmail(this ClaimsPrincipal user)
  {
    ArgumentNullException.ThrowIfNull(user);
    return user.Identity?.Name;
  }

  public static string GetDisplayName(this ClaimsPrincipal user)
  {
    ArgumentNullException.ThrowIfNull(user);
    return user.FindFirst(ClaimTypes.GivenName)?.Value;
  }

  public static string FindMatchingClassName(this IEnumerable<string> classes, string expectedPartitionKey, DateOnly dueDate, DateOnly currentDate)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(expectedPartitionKey);
    if (classes is null)
      return null;

    foreach (var className in classes)
    {
      var partitionKey = ClassNameParser.GetAssignmentPartitionKey(className, dueDate, currentDate);
      if (string.Equals(partitionKey, expectedPartitionKey, StringComparison.OrdinalIgnoreCase))
        return className.Trim();
    }

    return null;
  }

  public static async Task<List<T>> ToListAsync<T>(this AsyncPageable<T> query) where T : notnull
  {
    ArgumentNullException.ThrowIfNull(query);
    var list = new List<T>();
    await foreach (var item in query)
      list.Add(item);

    return list;
  }

  public static async Task BatchAddAsync<T>(this TableClient client, IEnumerable<T> entities) where T : class, ITableEntity
  {
    ArgumentNullException.ThrowIfNull(client);
    ArgumentNullException.ThrowIfNull(entities);
    foreach (var batch in entities.Chunk(100))
    {
      var actions = batch.Select(entity => new TableTransactionAction(TableTransactionActionType.Add, entity)).ToList();
      await client.SubmitTransactionAsync(actions);
    }
  }
}
