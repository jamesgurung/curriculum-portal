using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using System.Buffers;
using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;

namespace CurriculumPortal;

public static partial class Api
{
  public static void MapApiPaths(this WebApplication app)
  {
    MapInfrastructurePaths(app);
    MapCurriculumPaths(app);
    MapCourseAiPaths(app);
    MapEvaluationPaths(app);
    MapAssignmentPaths(app);
    MapSyncPaths(app);
  }

  private static void MapInfrastructurePaths(WebApplication app)
  {
    app.MapGet("/error", [AllowAnonymous] () => Results.Content("An error occurred.", "text/plain"));

    app.MapGet("/refresh", [Authorize(Roles = Roles.Admin)] async (ConfigService config) =>
    {
      await config.ReloadAsync();
      return Results.Ok();
    });

    app.MapGet("/backup", [Authorize(Roles = Roles.Admin)] async (HttpContext context, BackupService backup) =>
    {
      var file = await backup.CreateBackupAsync(context.RequestAborted);
      return Results.File(file.Stream, "application/zip", file.FileName);
    });

    app.MapGet("/images/school-logo.png", [AllowAnonymous] (HttpContext context, ConfigService config) =>
    {
      context.Response.Headers.CacheControl = "public, max-age=31536000";
      return Results.File(config.SchoolLogoBytes, "image/png");
    });

    app.MapGet("/images/school-logo-navbar.png", [AllowAnonymous] (HttpContext context, ConfigService config) =>
    {
      context.Response.Headers.CacheControl = "public, max-age=31536000";
      return Results.File(config.SchoolNavbarLogoBytes, "image/png");
    });
  }

  private static async Task<IResult> ValidateAntiForgeryAsync(HttpContext context, IAntiforgery antiforgery)
  {
    try
    {
      await antiforgery.ValidateRequestAsync(context);
      return null;
    }
    catch
    {
      return Results.BadRequest("Invalid anti-forgery token.");
    }
  }

  private static IResult CreateInsufficientTokensResult(InsufficientTokensException ex) =>
    Results.Text(ex.Message, "text/plain", statusCode: StatusCodes.Status429TooManyRequests);

  private static IResult StreamAiOperation<TResult>(Func<CancellationToken, Task<TResult>> operation, CancellationToken cancellationToken) =>
    Results.Stream(stream => SseFormatter.WriteAsync(StreamAiOperationAsync(operation, cancellationToken), stream, WriteSseItem, cancellationToken), "text/event-stream");

  private static IResult CreateProgressStream(Func<Action<int, int>, CancellationToken, Task<object>> operation, CancellationToken cancellationToken) =>
    Results.Stream(stream => SseFormatter.WriteAsync(StreamProgressOperationAsync(operation, cancellationToken), stream, WriteSseItem, cancellationToken), "text/event-stream");

  private static void WriteSseItem(SseItem<object> item, IBufferWriter<byte> writer) =>
    writer.Write(JsonSerializer.SerializeToUtf8Bytes(item.Data, item.Data?.GetType() ?? typeof(object), JsonDefaults.CamelCase));

  private static async IAsyncEnumerable<SseItem<object>> StreamAiOperationAsync<TResult>(Func<CancellationToken, Task<TResult>> operation, [EnumeratorCancellation] CancellationToken cancellationToken)
  {
    Task<TResult> operationTask;
    Exception error = null;
    try
    {
      operationTask = operation(cancellationToken);
    }
    catch (Exception ex)
    {
      error = ex;
      operationTask = null;
    }

    if (error is not null)
    {
      yield return new SseItem<object>(new { message = error.Message }, "error");
      yield break;
    }

    while (!operationTask.IsCompleted)
    {
      var delayTask = Task.Delay(TimeSpan.FromSeconds(15), cancellationToken);
      if (await Task.WhenAny(operationTask, delayTask) == operationTask) break;
      cancellationToken.ThrowIfCancellationRequested();
      yield return new SseItem<object>(new { ok = true }, "heartbeat");
    }

    object result = null;
    try
    {
      result = await operationTask;
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
      throw;
    }
    catch (Exception ex)
    {
      error = ex;
    }

    yield return error is null
      ? new SseItem<object>(result, "result")
      : new SseItem<object>(new { message = error.Message }, "error");
  }

  private static async IAsyncEnumerable<SseItem<object>> StreamProgressOperationAsync(Func<Action<int, int>, CancellationToken, Task<object>> operation, [EnumeratorCancellation] CancellationToken cancellationToken)
  {
    var progress = Channel.CreateUnbounded<(int Completed, int Total)>();
    Task<object> operationTask;
    Exception error = null;
    try
    {
      operationTask = operation((completed, total) => progress.Writer.TryWrite((completed, total)), cancellationToken);
      _ = operationTask.ContinueWith(_ => progress.Writer.TryComplete(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
    catch (Exception ex)
    {
      error = ex;
      operationTask = null;
      progress.Writer.TryComplete();
    }

    if (error is not null)
    {
      yield return new SseItem<object>(new { message = error.Message }, "error");
      yield break;
    }

    var progressAvailableTask = progress.Reader.WaitToReadAsync(cancellationToken).AsTask();
    while (true)
    {
      var delayTask = Task.Delay(TimeSpan.FromSeconds(15), cancellationToken);
      var completedTask = await Task.WhenAny(operationTask, progressAvailableTask, delayTask);

      if (completedTask == progressAvailableTask)
      {
        if (!await progressAvailableTask)
        {
          break;
        }

        while (progress.Reader.TryRead(out var progressItem))
        {
          yield return CreateProgressItem(progressItem);
        }

        progressAvailableTask = progress.Reader.WaitToReadAsync(cancellationToken).AsTask();
        continue;
      }

      if (completedTask == operationTask)
      {
        progress.Writer.TryComplete();
        break;
      }

      if (completedTask == delayTask)
      {
        cancellationToken.ThrowIfCancellationRequested();
        yield return new SseItem<object>(new { ok = true }, "heartbeat");
      }
    }

    while (progress.Reader.TryRead(out var progressItem))
    {
      yield return CreateProgressItem(progressItem);
    }

    object result = null;
    try
    {
      result = await operationTask;
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
      throw;
    }
    catch (Exception ex)
    {
      error = ex;
    }

    yield return error is null
      ? new SseItem<object>(result, "result")
      : new SseItem<object>(new { message = error.Message }, "error");
  }

  private static int GetProgressPercentage(int completed, int total) =>
    total <= 0 ? 100 : Math.Clamp((int)Math.Round(completed * 100d / total), 0, 100);

  private static SseItem<object> CreateProgressItem((int Completed, int Total) progress) =>
    new(new { completed = progress.Completed, total = progress.Total, percentage = GetProgressPercentage(progress.Completed, progress.Total) }, "progress");

  private static List<KeyKnowledgeRevisionQuestion> BuildRevisionQuiz(QuestionBank questionBank)
  {
    questionBank.Questions ??= [];
    return questionBank.Questions.Select(o => new KeyKnowledgeRevisionQuestion
    {
      Question = o.Question,
      CorrectAnswer = o.CorrectAnswer,
      IncorrectAnswer = o.IncorrectAnswer1
    }).ToList();
  }

  private static string GetKeyKnowledgeCompletionError(KeyKnowledge keyKnowledge)
  {
    var declarativeCount = keyKnowledge?.DeclarativeKnowledge?.Count(o => !string.IsNullOrWhiteSpace(o)) ?? 0;
    var proceduralCount = keyKnowledge?.ProceduralKnowledge?.Count(o => !string.IsNullOrWhiteSpace(o)) ?? 0;

    if (declarativeCount == 0 || proceduralCount == 0)
    {
      return "Both key knowledge sections are required.";
    }

    if (declarativeCount < 5)
    {
      return "There must be at least 5 declarative knowledge items.";
    }

    return null;
  }

  private static string GetAssessmentCompletionError(Assessment assessment)
  {
    assessment ??= new Assessment();
    assessment.Sections ??= [];

    if (assessment.Sections.Count == 0 || !assessment.Sections.SelectMany(o => o.Questions ?? []).Any())
    {
      return "Assessment must contain at least one question.";
    }

    if (assessment.Sections.Any(o => (o.Questions ?? []).Count == 0))
    {
      return "All sections must have at least one question.";
    }

    if (assessment.Sections.Any(o => (o.Questions ?? []).Any(q => string.IsNullOrWhiteSpace(q.Question))))
    {
      return "Questions cannot be blank.";
    }

    if (assessment.Sections.Any(o => (o.Questions ?? []).Any(q => q.Answers is not null && (q.Answers.Count != 4 || q.Answers.Any(string.IsNullOrWhiteSpace)))))
    {
      return "All multiple-choice questions must have four choices.";
    }

    if (assessment.Sections.Any(o => (o.Questions ?? []).Any(q => string.IsNullOrWhiteSpace(q.MarkScheme))))
    {
      return "All questions must have a mark scheme.";
    }

    return null;
  }
}

