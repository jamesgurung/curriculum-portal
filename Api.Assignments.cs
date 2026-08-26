using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using System.Globalization;
using System.Text.Json;

namespace CurriculumPortal;

public static partial class Api
{
  private static void MapAssignmentPaths(WebApplication app)
  {
    app.MapGet("/assignments/set", [Authorize(Roles = Roles.Admin)] async (AssignmentService assignmentService, CancellationToken cancellationToken) =>
    {
      var now = DateOnly.FromDateTime(DateTime.UtcNow);
      var dueDate = now.AddDays((((int)DayOfWeek.Monday - (int)now.DayOfWeek + 6) % 7) + 1);
      dueDate = assignmentService.ResolveDueDate(dueDate);
      await assignmentService.GenerateAssignmentsAsync(dueDate, cancellationToken);
      return Results.Text($"Created assignments due {dueDate:yyyy-MM-dd}.");
    });

    app.MapGet("/test-emails", [Authorize(Roles = Roles.Admin)] async (AssignmentAutomationService assignmentAutomationService, CancellationToken cancellationToken) =>
    {
      var (tutorEmails, teacherEmails) = await assignmentAutomationService.SendTestCompletionEmailsAsync(cancellationToken);
      return Results.Text($"Sent {tutorEmails} tutor and {teacherEmails} teacher completion emails.");
    });

    app.MapPost("/assignments/{courseId}/year-{year}/{dueDate}/submit", [Authorize(Roles = Roles.Student)] async (HttpContext context, IAntiforgery antiforgery, string courseId, int year, string dueDate, AssignmentAnswerRequest model, ConfigService config, CourseService courseService, AssignmentService assignmentService) =>
    {
      var csrfError = await ValidateAntiForgeryAsync(context, antiforgery);
      if (csrfError is not null)
      {
        return csrfError;
      }

      if (context.User.Identity?.IsAuthenticated != true)
      {
        return Results.Unauthorized();
      }

      if (!context.User.IsInRole(Roles.Student) || !config.UsersByEmail.TryGetValue(context.User.GetEmail(), out var currentUser))
      {
        return Results.Forbid();
      }

      if (string.IsNullOrWhiteSpace(courseId)
        || year < 1
        || model is null
        || !DateOnly.TryParseExact(dueDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedDueDate))
      {
        return Results.BadRequest("Data missing.");
      }

      var course = await courseService.TryGetCourseAsync(courseId);
      if (course is null || string.IsNullOrWhiteSpace(course.SubjectCode))
      {
        return Results.NotFound();
      }

      var partitionKey = $"{year:D2}{course.SubjectCode}";
      var className = currentUser.Classes.FindMatchingClassName(partitionKey, parsedDueDate, DateOnly.FromDateTime(DateTime.UtcNow));
      if (className is null)
      {
        return Results.Forbid();
      }

      try
      {
        var response = await assignmentService.SubmitStudentAssignmentAnswerAsync(currentUser, course, year, parsedDueDate, className, model);
        if (response is null)
        {
          return Results.NotFound();
        }

        return Results.Content(JsonSerializer.Serialize(response, JsonDefaults.CamelCase), "application/json");
      }
      catch (ArgumentException ex)
      {
        return Results.BadRequest(ex.Message);
      }
      catch (TooManyRequestsException ex)
      {
        return Results.Text(ex.Message, "text/plain", statusCode: StatusCodes.Status429TooManyRequests);
      }
      catch (InvalidOperationException ex)
      {
        return Results.Conflict(ex.Message);
      }
    });

    app.MapPost("/bonus-quiz/submit", [Authorize(Roles = Roles.Student)] async (
      HttpContext context,
      IAntiforgery antiforgery,
      BonusQuizAnswerRequest model,
      ConfigService config,
      BonusQuizService bonusQuizService) =>
    {
      var csrfError = await ValidateAntiForgeryAsync(context, antiforgery);
      if (csrfError is not null) return csrfError;

      if (context.User.Identity?.IsAuthenticated != true) return Results.Unauthorized();
      if (!config.UsersByEmail.TryGetValue(context.User.GetEmail(), out var currentUser)) return Results.Forbid();
      if (model is null) return Results.BadRequest("Data missing.");

      try
      {
        var response = await bonusQuizService.SubmitAnswerAsync(
          currentUser,
          model,
          DateTimeOffset.UtcNow,
          context.RequestAborted);
        return Results.Content(JsonSerializer.Serialize(response, JsonDefaults.CamelCase), "application/json");
      }
      catch (ArgumentException ex)
      {
        return Results.BadRequest(ex.Message);
      }
      catch (TooManyRequestsException ex)
      {
        return Results.Text(ex.Message, "text/plain", statusCode: StatusCodes.Status429TooManyRequests);
      }
      catch (InvalidOperationException ex)
      {
        return Results.Conflict(ex.Message);
      }
    });
  }
}
