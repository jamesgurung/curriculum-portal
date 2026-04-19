using Azure;
using Azure.Data.Tables;
using System.Security.Claims;

namespace CurriculumPortal;

public static class ExtensionMethods
{
  public static bool CanEditCourse(this ClaimsPrincipal user, CourseEntity course)
  {
    ArgumentNullException.ThrowIfNull(user);
    ArgumentNullException.ThrowIfNull(course);

    return user.IsInRole(Roles.Admin) || course.LeadersList.Contains(user.GetEmail());
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

  public static string FindMatchingClassName(this IEnumerable<string> classes, string expectedPartitionKey)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(expectedPartitionKey);
    if (classes is null)
    {
      return null;
    }

    foreach (var className in classes)
    {
      if (TryBuildStudentPartitionKey(className, out var partitionKey)
        && partitionKey.Equals(expectedPartitionKey, StringComparison.OrdinalIgnoreCase))
      {
        return className.Trim();
      }
    }

    return null;
  }

  public static async Task<List<T>> ToListAsync<T>(this AsyncPageable<T> query) where T : notnull
  {
    ArgumentNullException.ThrowIfNull(query);
    var list = new List<T>();
    await foreach (var item in query)
    {
      list.Add(item);
    }

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

  private static bool TryBuildStudentPartitionKey(string className, out string partitionKey)
  {
    partitionKey = null;
    if (string.IsNullOrWhiteSpace(className))
    {
      return false;
    }

    var trimmed = className.Trim();
    var slashIndex = trimmed.IndexOf('/', StringComparison.Ordinal);
    if (slashIndex <= 0 || slashIndex + 2 >= trimmed.Length)
    {
      return false;
    }

    var yearDigits = new string(trimmed.TakeWhile(char.IsDigit).ToArray());
    if (!int.TryParse(yearDigits, out var yearGroup))
    {
      return false;
    }

    partitionKey = $"{yearGroup:D2}{trimmed.Substring(slashIndex + 1, 2)}";
    return true;
  }
}
