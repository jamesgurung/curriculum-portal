using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using System.Globalization;

namespace CurriculumPortal;

public class AssignmentSettingService(
  AssignmentService assignmentService,
  TeamsService teamsService,
  ConfigService configService,
  AppOptions options,
  ServiceAccountAuthService serviceAccountAuthService,
  MailService mailService,
  IServiceScopeFactory serviceScopeFactory,
  ILogger<AssignmentSettingService> logger) : BackgroundService
{
  private static readonly TimeSpan ReauthenticationReminderWindow = TimeSpan.FromDays(14);
  private static readonly TimeZoneInfo _ukTime = TimeZoneInfo.FindSystemTimeZoneById("GMT Standard Time");

  protected override async Task ExecuteAsync(CancellationToken stoppingToken)
  {
    while (!stoppingToken.IsCancellationRequested)
    {
      // Run every Monday at 08:05
      var utcNow = DateTime.UtcNow;
      var now = TimeZoneInfo.ConvertTimeFromUtc(utcNow, _ukTime);
      var daysUntilMonday = ((int)DayOfWeek.Monday - (int)now.DayOfWeek + 7) % 7;
      if (daysUntilMonday == 0 && now.TimeOfDay >= TimeSpan.FromMinutes(485)) daysUntilMonday = 7;
      var nextRun = now.Date.AddDays(daysUntilMonday).AddHours(8).AddMinutes(5);
      var wait = TimeZoneInfo.ConvertTimeToUtc(nextRun, _ukTime) - utcNow;

      try
      {
        await Task.Delay(wait, stoppingToken);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        try
        {
          await SendServiceAccountReauthenticationReminderAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
          throw;
        }
        catch (Exception ex)
        {
          logger.LogError(ex, "Failed to send service account reauthentication reminder.");
        }

        if (assignmentService.ResolveDueDate(today) != today) continue;

        try
        {
          await SendCompletionEmailsAsync(today, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
          throw;
        }
        catch (Exception ex)
        {
          logger.LogError(ex, "Failed to send completion emails.");
        }

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

  private async Task SendServiceAccountReauthenticationReminderAsync(CancellationToken cancellationToken)
  {
    var expiry = await serviceAccountAuthService.GetRefreshTokenExpiryAsync();
    if (expiry is null || expiry.Value - DateTime.UtcNow > ReauthenticationReminderWindow) return;

    var adminEmail = options.AdminEmails.First();
    var expiryText = expiry.Value.ToString("dddd d MMMM yyyy 'at' HH:mm 'UTC'", CultureInfo.InvariantCulture);
    await mailService.SendAsync([new Email
    {
      To = [adminEmail],
      Subject = "Service account reauthentication required",
      Body = "<html><body style=\"font-family: Arial; font-size: 11pt\">The service account refresh token expires on <b>" +
        expiryText +
        "</b>.<br/><br/><a href=\"" + options.Website.TrimEnd('/') + "/serviceaccount\">Reauthenticate the service account</a>." +
        "<br/><br/></body></html>"
    }], cancellationToken);
  }

  public async Task<(int TutorEmails, int TeacherEmails)> SendTestCompletionEmailsAsync(CancellationToken cancellationToken)
  {
    var now = DateOnly.FromDateTime(DateTime.UtcNow);
    var dueDate = now.AddDays(((int)DayOfWeek.Monday - (int)now.DayOfWeek + 7) % 7);
    dueDate = assignmentService.ResolveDueDate(dueDate);
    return await SendCompletionEmailsAsync(dueDate, options.AdminEmails.First(), true, cancellationToken);
  }

  private async Task<(int TutorEmails, int TeacherEmails)> SendCompletionEmailsAsync(DateOnly dueDate, CancellationToken cancellationToken)
    => await SendCompletionEmailsAsync(dueDate, null, false, cancellationToken);

  private async Task<(int TutorEmails, int TeacherEmails)> SendCompletionEmailsAsync(DateOnly dueDate, string recipientOverride, bool firstOnly, CancellationToken cancellationToken)
  {
    var reports = await assignmentService.GetWeeklyCompletionReportsAsync(dueDate);
    if (reports.Tutors.Count == 0 && reports.Teachers.Count == 0) return (0, 0);

    using var scope = serviceScopeFactory.CreateScope();
    var emailTemplateService = scope.ServiceProvider.GetRequiredService<EmailTemplateService>();
    var httpContext = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
    var actionContext = new ActionContext(httpContext, new RouteData(), new ActionDescriptor());
    var emails = new List<Email>();
    var tutorReports = reports.Tutors.Take(firstOnly ? 1 : int.MaxValue).ToList();
    var teacherReports = reports.Teachers.Take(firstOnly ? 1 : int.MaxValue).ToList();

    foreach (var report in tutorReports)
    {
      emails.Add(new Email
      {
        To = [recipientOverride ?? report.Tutor.Email],
        Subject = EmailTemplateService.GetTutorCompletionTitle(report),
        Body = await emailTemplateService.BuildTutorCompletionEmailAsync(actionContext, report)
      });
    }

    foreach (var report in teacherReports)
    {
      emails.Add(new Email
      {
        To = [recipientOverride ?? report.Teacher.Email],
        Subject = EmailTemplateService.GetTeacherCompletionTitle(report),
        Body = await emailTemplateService.BuildTeacherCompletionEmailAsync(actionContext, report)
      });
    }

    if (emails.Count == 0) return (0, 0);

    await mailService.SendAsync(emails, cancellationToken);
    logger.LogInformation("Sent {TutorEmailCount} tutor and {TeacherEmailCount} teacher completion emails.", tutorReports.Count, teacherReports.Count);
    return (tutorReports.Count, teacherReports.Count);
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
