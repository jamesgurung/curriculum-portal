using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using System.Globalization;

namespace CurriculumPortal;

public class AssignmentAutomationService(
  AssignmentService assignmentService,
  TeamsService teamsService,
  ConfigService configService,
  AppOptions options,
  ServiceAccountAuthService serviceAccountAuthService,
  MailService mailService,
  IBehaviourRecordService behaviourRecordService,
  IServiceScopeFactory serviceScopeFactory,
  ILogger<AssignmentAutomationService> logger) : BackgroundService
{
  private static readonly TimeSpan ReauthenticationReminderWindow = TimeSpan.FromDays(14);
  private static readonly TimeZoneInfo UkTime = TimeZoneInfo.FindSystemTimeZoneById("GMT Standard Time");

  protected override async Task ExecuteAsync(CancellationToken stoppingToken)
  {
    while (!stoppingToken.IsCancellationRequested)
    {
      // Run every Monday at 08:05
      var utcNow = DateTime.UtcNow;
      var now = TimeZoneInfo.ConvertTimeFromUtc(utcNow, UkTime);
      var daysUntilMonday = ((int)DayOfWeek.Monday - (int)now.DayOfWeek + 7) % 7;
      if (daysUntilMonday == 0 && now.TimeOfDay >= TimeSpan.FromMinutes(485)) daysUntilMonday = 7;
      var nextRun = now.Date.AddDays(daysUntilMonday).AddHours(8).AddMinutes(5);
      var wait = TimeZoneInfo.ConvertTimeToUtc(nextRun, UkTime) - utcNow;

      try
      {
        await Task.Delay(wait, stoppingToken);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        if (configService.Holidays.Any(holiday => today >= holiday.Start && today <= holiday.End)) continue;

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

        try
        {
          await IssueBehavioursAsync(today);
        }
        catch (Exception ex)
        {
          logger.LogError(ex, "Failed to issue behaviour events.");
        }

        var dueDate = assignmentService.ResolveDueDate(today.AddDays(7));
        HashSet<string> assignmentPartitionKeys;
        try
        {
          assignmentPartitionKeys = await assignmentService.GenerateAssignmentsAsync(dueDate, stoppingToken);
          logger.LogInformation("Generated new assignments for due date {DueDate}.", dueDate);
        }
        catch (Exception ex)
        {
          logger.LogError(ex, "Failed to generate new assignments for due date {DueDate}.", dueDate);
          continue;
        }

        try
        {
          await SetTeamsAssignmentsAsync(dueDate, assignmentPartitionKeys);
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

  private async Task IssueBehavioursAsync(DateOnly dueDate)
  {
    var students = await assignmentService.GetStudentsWithCompletionAsync(dueDate);
    var positiveStudents = students
      .Where(o => o.CompletionRate >= options.AssignmentCompletionHighThreshold)
      .GroupBy(o => o.BehaviourCode, StringComparer.OrdinalIgnoreCase)
      .ToDictionary(g => g.Key, g => g.Select(o => o.Student).ToList(), StringComparer.OrdinalIgnoreCase);
    var negativeStudents = students
      .Where(o => o.CompletionRate < options.AssignmentCompletionLowThreshold && !configService.Exemptions.Contains(o.Student.Id) && !IsExamYearExempt(o.Student, dueDate))
      .GroupBy(o => o.BehaviourCode, StringComparer.OrdinalIgnoreCase)
      .ToDictionary(g => g.Key, g => g.Select(o => o.Student).ToList(), StringComparer.OrdinalIgnoreCase);
    var issued = await behaviourRecordService.IssueBehavioursAsync(positiveStudents, negativeStudents);
    logger.LogInformation("Issued {PositiveCount} positive and {NegativeCount} negative behaviour events.", issued.Positive, issued.Negative);
  }

  private async Task SetTeamsAssignmentsAsync(DateOnly dueDate, HashSet<string> assignmentPartitionKeys)
  {
    var ks3YearGroupsWithAssignments = assignmentPartitionKeys
      .Select(ClassNameParser.GetLeadingNumber)
      .Where(yearGroup => yearGroup is >= 7 and <= 9)
      .ToHashSet();
    var classes = configService.Students
      .SelectMany(student => student.Classes)
      .Where(className => !IsExamYearExempt(ClassNameParser.GetLeadingNumber(className), dueDate)
        && (IsKs3TutorClassWithAssignments(className, ks3YearGroupsWithAssignments)
          || IsKs45SubjectClassWithAssignment(className, assignmentPartitionKeys)))
      .ToHashSet(StringComparer.OrdinalIgnoreCase);
    await teamsService.SetAssignmentsAsync(dueDate, classes);
    logger.LogInformation("Set Teams assignments for due date {DueDate}.", dueDate);
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

  public Task<(int TutorEmails, int TeacherEmails)> SendTestCompletionEmailsAsync(CancellationToken cancellationToken)
  {
    var now = DateOnly.FromDateTime(DateTime.UtcNow);
    var dueDate = now.AddDays(((int)DayOfWeek.Monday - (int)now.DayOfWeek + 7) % 7);
    dueDate = assignmentService.ResolveDueDate(dueDate);
    return SendCompletionEmailsAsync(dueDate, options.AdminEmails.First(), true, cancellationToken);
  }

  private Task<(int TutorEmails, int TeacherEmails)> SendCompletionEmailsAsync(DateOnly dueDate, CancellationToken cancellationToken)
    => SendCompletionEmailsAsync(dueDate, null, false, cancellationToken);

  private async Task<(int TutorEmails, int TeacherEmails)> SendCompletionEmailsAsync(DateOnly dueDate, string recipientOverride, bool firstOnly, CancellationToken cancellationToken)
  {
    var reports = await assignmentService.GetWeeklyCompletionReportsAsync(dueDate);
    var tutorReports = reports.Tutors
      .Where(o => !IsExamYearExempt(ClassNameParser.GetLeadingNumber(o.TutorGroup), dueDate))
      .Take(firstOnly ? 1 : int.MaxValue)
      .ToList();
    var teacherReports = reports.Teachers
      .Select(o => new TeacherCompletionReport
      {
        DueDateLabel = o.DueDateLabel,
        Teacher = o.Teacher,
        Classes = o.Classes
          .Where(cls => !IsExamYearExempt(ClassNameParser.GetLeadingNumber(cls.ClassName), dueDate))
          .ToList()
      })
      .Where(o => o.Classes.Count > 0)
      .Take(firstOnly ? 1 : int.MaxValue)
      .ToList();
    if (tutorReports.Count == 0 && teacherReports.Count == 0) return (0, 0);

    using var scope = serviceScopeFactory.CreateScope();
    var emailTemplateService = scope.ServiceProvider.GetRequiredService<EmailTemplateService>();
    var httpContext = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
    var actionContext = new ActionContext(httpContext, new RouteData(), new ActionDescriptor());
    var emails = new List<Email>();

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
    return IsExamYearExempt(GetYearGroup(student), dueDate);
  }

  private static bool IsExamYearExempt(int yearGroup, DateOnly dueDate)
  {
    return dueDate.Month is >= 4 and <= 8 && yearGroup is 11 or 13;
  }

  private static int GetYearGroup(User student)
  {
    if (student.Classes is not null)
    {
      foreach (var className in student.Classes)
      {
        var yearGroup = ClassNameParser.GetLeadingNumber(className);
        if (yearGroup is 11 or 13) return yearGroup;
      }
    }

    return ClassNameParser.GetLeadingNumber(student.TutorGroup);
  }

  private static bool IsKs3TutorClassWithAssignments(string className, HashSet<int> ks3YearGroupsWithAssignments)
  {
    var yearGroup = ClassNameParser.GetLeadingNumber(className);
    return yearGroup is >= 7 and <= 9
      && className.Contains("/Tu", StringComparison.OrdinalIgnoreCase)
      && ks3YearGroupsWithAssignments.Contains(yearGroup);
  }

  private static bool IsKs45SubjectClassWithAssignment(string className, HashSet<string> assignmentPartitionKeys)
  {
    var partitionKey = ClassNameParser.GetAssignmentPartitionKey(className);
    return partitionKey is not null
      && ClassNameParser.GetLeadingNumber(partitionKey) is >= 10 and <= 13
      && assignmentPartitionKeys.Contains(partitionKey);
  }
}
