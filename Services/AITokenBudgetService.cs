using System.Net.Http.Headers;
using System.Text.Json;

namespace CurriculumPortal;

public sealed class AITokenBudgetService(AppOptions options, IHttpClientFactory httpClientFactory) : IDisposable
{
  private readonly SemaphoreSlim reservationGate = new(1, 1);
  private int reservedTokens;

  internal async Task<IDisposable> ReserveAsync(int tokens, CancellationToken cancellationToken = default)
  {
    if (options.DailyTokenLimit == default) return EmptyReservation.Instance;

    await reservationGate.WaitAsync(cancellationToken);
    try
    {
      var tokensUsed = await GetTokensUsedTodayAsync(cancellationToken);
      if (tokensUsed + Volatile.Read(ref reservedTokens) + tokens > options.DailyTokenLimit) throw new InsufficientTokensException();

      Interlocked.Add(ref reservedTokens, tokens);
      return new TokenReservation(() => Release(tokens));
    }
    finally
    {
      reservationGate.Release();
    }
  }

  internal async Task<int> GetAvailableTokensAsync(CancellationToken cancellationToken = default)
  {
    if (options.DailyTokenLimit == default) return int.MaxValue;

    await reservationGate.WaitAsync(cancellationToken);
    try
    {
      var tokensUsed = await GetTokensUsedTodayAsync(cancellationToken);
      return Math.Max(0, options.DailyTokenLimit - tokensUsed - Volatile.Read(ref reservedTokens));
    }
    finally
    {
      reservationGate.Release();
    }
  }

  private async Task<int> GetTokensUsedTodayAsync(CancellationToken cancellationToken)
  {
    var start = new DateTimeOffset(DateTime.UtcNow.Date, TimeSpan.Zero);
    var end = start.AddDays(1);
    var url = new Uri($"https://api.openai.com/v1/organization/usage/completions?start_time={start.ToUnixTimeSeconds()}&end_time={end.ToUnixTimeSeconds()}&bucket_width=1d");

    using var http = httpClientFactory.CreateClient();
    http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.OpenAIAdminApiKey);

    var json = await http.GetStringAsync(url, cancellationToken);
    using var doc = JsonDocument.Parse(json);
    var inputTokens = 0;
    var outputTokens = 0;
    foreach (var bucket in doc.RootElement.GetProperty("data").EnumerateArray())
    {
      foreach (var result in bucket.GetProperty("results").EnumerateArray())
      {
        inputTokens += result.GetProperty("input_tokens").GetInt32();
        outputTokens += result.GetProperty("output_tokens").GetInt32();
      }
    }

    return inputTokens + outputTokens;
  }

  private void Release(int tokens) => Interlocked.Add(ref reservedTokens, -tokens);

  public void Dispose() => reservationGate.Dispose();

  private sealed class TokenReservation(Action release) : IDisposable
  {
    private Action release = release;

    public void Dispose() => Interlocked.Exchange(ref release, null)?.Invoke();
  }

  private sealed class EmptyReservation : IDisposable
  {
    internal static EmptyReservation Instance { get; } = new();

    public void Dispose() { }
  }
}
