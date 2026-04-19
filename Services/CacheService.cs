using System.Collections.Concurrent;
using System.Text.Json;

namespace CurriculumPortal;

public class CacheService
{
  private readonly ConcurrentDictionary<string, (string Data, DateTimeOffset LastUpdated)> _cache = new(StringComparer.Ordinal);

  public async Task<(string Data, DateTimeOffset LastUpdated)> GetCachedDataAsync<T>(string key, Func<Task<T>> retrieve) where T : new()
  {
    ArgumentNullException.ThrowIfNull(retrieve);
    if (!_cache.TryGetValue(key, out var entry) || entry.Data is null)
    {
      entry = (JsonSerializer.Serialize(await retrieve(), JsonOptions.CamelCase), RoundedNow());
      _cache[key] = entry;
    }

    return (entry.Data, entry.LastUpdated);
  }

  public void Update(string key, string value)
  {
    _cache[key] = (value, RoundedNow());
  }

  public void Invalidate(params string[] keys)
  {
    ArgumentNullException.ThrowIfNull(keys);
    foreach (var key in keys)
    {
      Update(key, null);
    }
  }

  private static DateTimeOffset RoundedNow()
  {
    var now = DateTimeOffset.UtcNow;
    return now.AddTicks(-now.Ticks % TimeSpan.TicksPerSecond);
  }

  private static class JsonOptions
  {
    public static readonly JsonSerializerOptions CamelCase = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
  }
}

