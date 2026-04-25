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
            var negativeStudents = students
              .Where(o => o.CompletionRate < options.AssignmentCompletionLowThreshold && !configService.Exemptions.Contains(o.Student.Id) && !IsExamYearExempt(o.Student, today))
              .Select(o => o.Student)
              .ToList();
            await classChartsService.IssueBehaviours(positiveStudents, negativeStudents);
            logger.LogInformation("Issued {PositiveCount} positive and {NegativeCount} negative Class Charts behaviour events.", positiveStudents.Count, negativeStudents.Count);
          }
          catch (Exception ex)
          {
            logger.LogError(ex, "Failed to issue Class Charts behaviour events.");
          }
        }

        var dueDate = assignmentService.ResolveDueDate(DateOnly.FromDateTime(DateTime.UtcNow.AddDays(7)));
        HashSet<string> assignmentPartitionKeys;
        try
        {
          assignmentPartitionKeys = await assignmentService.GenerateAssignments(dueDate);
          logger.LogInformation("Generated new assignments for due date {DueDate}.", dueDate);
        }
        catch (Exception ex)
        {
          logger.LogError(ex, "Failed to generate new assignments for due date {DueDate}.", dueDate);
          continue;
        }

        try
        {
          var ks3YearGroupsWithAssignments = assignmentPartitionKeys
            .Select(GetLeadingNumber)
            .Where(yearGroup => yearGroup is >= 7 and <= 9)
            .ToHashSet();
          var classes = configService.Students
            .SelectMany(student => student.Classes)
            .Where(className => IsKs3TutorClassWithAssignments(className, ks3YearGroupsWithAssignments)
              || IsKs45SubjectClassWithAssignment(className, assignmentPartitionKeys))
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

  private static bool IsExamYearExempt(User student, DateOnly dueDate)
  {
    return dueDate.Month is >= 4 and <= 8 && GetYearGroup(student) is 11 or 13;
  }

  private static int GetYearGroup(User student)
  {
    if (student.Classes is not null)
    {
      foreach (var className in student.Classes)
      {
        var yearGroup = GetLeadingNumber(className);
        if (yearGroup is 11 or 13) return yearGroup;
      }
    }

    return GetLeadingNumber(student.TutorGroup);
  }

  private static bool IsKs3TutorClassWithAssignments(string className, HashSet<int> ks3YearGroupsWithAssignments)
  {
    var yearGroup = GetLeadingNumber(className);
    return yearGroup is >= 7 and <= 9
      && className.Contains("/Tu", StringComparison.OrdinalIgnoreCase)
      && ks3YearGroupsWithAssignments.Contains(yearGroup);
  }

  private static bool IsKs45SubjectClassWithAssignment(string className, HashSet<string> assignmentPartitionKeys)
  {
    var partitionKey = GetAssignmentPartitionKey(className);
    return partitionKey is not null
      && GetLeadingNumber(partitionKey) is >= 10 and <= 13
      && assignmentPartitionKeys.Contains(partitionKey);
  }

  private static string GetAssignmentPartitionKey(string className)
  {
    if (string.IsNullOrWhiteSpace(className)) return null;

    var trimmed = className.Trim();
    var yearGroup = GetLeadingNumber(trimmed);
    var slashIndex = trimmed.IndexOf('/', StringComparison.Ordinal);
    if (yearGroup == 0 || slashIndex < 0 || slashIndex + 2 >= trimmed.Length) return null;

    return $"{yearGroup:D2}{trimmed.Substring(slashIndex + 1, 2)}";
  }

  private static int GetLeadingNumber(string value)
  {
    if (string.IsNullOrWhiteSpace(value)) return 0;

    var digits = new string(value.Trim().TakeWhile(char.IsDigit).ToArray());
    return int.TryParse(digits, out var yearGroup) ? yearGroup : 0;
  }
}
