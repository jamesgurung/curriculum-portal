namespace CurriculumPortal;

public class CourseEvaluationAutomationService(
  CourseEvaluationService evaluationService,
  ILogger<CourseEvaluationAutomationService> logger) : BackgroundService
{
  private static readonly TimeZoneInfo UkTime = TimeZoneInfo.FindSystemTimeZoneById("GMT Standard Time");

  protected override async Task ExecuteAsync(CancellationToken stoppingToken)
  {
    while (!stoppingToken.IsCancellationRequested)
    {
      var utcNow = DateTime.UtcNow;
      var now = TimeZoneInfo.ConvertTimeFromUtc(utcNow, UkTime);
      var nextRun = now.Date.AddHours(23);
      if (now >= nextRun)
        nextRun = nextRun.AddDays(1);

      var wait = TimeZoneInfo.ConvertTimeToUtc(nextRun, UkTime) - utcNow;
      try
      {
        await Task.Delay(wait, stoppingToken);
        await evaluationService.RefreshOutdatedEvaluationsAsync(stoppingToken);
      }
      catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
      {
        break;
      }
      catch (Exception ex)
      {
        logger.LogError(ex, "Daily evaluation feedback refresh failed.");
      }
    }
  }
}
