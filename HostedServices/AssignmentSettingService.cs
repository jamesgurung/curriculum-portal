namespace CurriculumPortal;

public class AssignmentSettingService(AssignmentService assignmentService, TeamsService teamsService, ConfigService configService, AppOptions options, ILogger<AssignmentSettingService> logger) : BackgroundService
{
  protected override async Task ExecuteAsync(CancellationToken stoppingToken)
  {
    while (!stoppingToken.IsCancellationRequested)
    {
      // Run every Monday at midday
      var now = DateTimeOffset.Now;
      var daysUntilMonday = ((int)DayOfWeek.Monday - (int)now.DayOfWeek + 7) % 7;
      if (daysUntilMonday == 0 && now.TimeOfDay >= TimeSpan.FromHours(12)) daysUntilMonday = 7;
      var wait = now.Date.AddDays(daysUntilMonday).AddHours(12) - now;

      try
      {
        await Task.Delay(wait, stoppingToken);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        if (assignmentService.ResolveDueDate(today) != today) continue;

        if (!string.IsNullOrWhiteSpace(options.ClassChartsEmail) && !string.IsNullOrWhiteSpace(options.ClassChartsPassword))
        {
          try
          {
            var classChartsService = new ClassChartsService(options, configService);
            var students = await assignmentService.GetStudentsWithCompletionAsync(today);
            var positiveStudents = students.Where(o => o.CompletionRate >= options.AssignmentCompletionHighThreshold).Select(o => o.Student).ToList();
            var negativeStudents = students.Where(o => o.CompletionRate < options.AssignmentCompletionLowThreshold).Select(o => o.Student).ToList();
            await classChartsService.IssueBehaviours(positiveStudents, negativeStudents);
            logger.LogInformation("Issued {PositiveCount} positive and {NegativeCount} negative Class Charts behaviour events.", positiveStudents.Count, negativeStudents.Count);
          }
          catch (Exception ex)
          {
            logger.LogError(ex, "Failed to issue Class Charts behaviour events.");
          }
        }

        var dueDate = assignmentService.ResolveDueDate(DateOnly.FromDateTime(DateTime.UtcNow.AddDays(7)));
        HashSet<int> yearGroupsWithAssignments;
        try
        {
          yearGroupsWithAssignments = await assignmentService.GenerateAssignments(dueDate);
          logger.LogInformation("Generated new assignments for due date {DueDate}.", dueDate);
        }
        catch (Exception ex)
        {
          logger.LogError(ex, "Failed to generate new assignments for due date {DueDate}.", dueDate);
          continue;
        }

        try
        {
          var classes = configService.Students
            .SelectMany(student => student.Classes)
            .Where(className => className.Contains("/Tu", StringComparison.OrdinalIgnoreCase)
              && className[0] is >= '7' and <= '9'
              && yearGroupsWithAssignments.Contains(className[0] - '0'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
          await teamsService.SetAssignments(dueDate, classes);
          logger.LogInformation("Set Teams assignments for due date {DueDate}.", dueDate);
        }
        catch (Exception ex)
        {
          logger.LogError(ex, "Failed to set Teams assignments for due date {DueDate}.", dueDate);
        }
      }
      catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
      {
        break;
      }
      catch (Exception ex)
      {
        logger.LogError(ex, "Weekly assignment setting failed.");
      }
    }
  }
}
