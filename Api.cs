using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace CurriculumPortal;

public static class Api
{
  public static void MapApiPaths(this WebApplication app)
  {
    app.MapGet("/error", [AllowAnonymous] () => Results.Content("An error occurred.", "text/plain"));

    app.MapGet("/refresh", [Authorize(Roles = Roles.Admin)] async (ConfigService config) =>
    {
      await config.ReloadAsync();
      return Results.NoContent();
    });

    app.MapGet("/images/school-logo.png", [AllowAnonymous] (HttpContext context, ConfigService config) =>
    {
      var logo = config.SchoolLogoBytes;
      context.Response.Headers.CacheControl = "public, max-age=31536000";
      return Results.File(config.SchoolLogoBytes, "image/png");
    });

    app.MapGet("/keyknowledge", [AllowAnonymous] async (HttpContext context, string unit, CourseService storage, CacheService cache) =>
    {
      if (unit?.Length != 36)
      {
        return Results.NotFound();
      }

      var data = await cache.GetCachedDataAsync(unit, async () => await storage.GetBlobAsync<KeyKnowledge>(unit));

      context.Response.Headers.CacheControl = "public, max-age=3600";
      if (context.Request.Headers.TryGetValue("If-Modified-Since", out var ims)
          && DateTimeOffset.TryParse(ims, out var clientCacheDate)
          && clientCacheDate >= data.LastUpdated)
      {
        return Results.StatusCode(304);
      }

      context.Response.Headers.LastModified = data.LastUpdated.ToString("R");
      return Results.Content(data.Data, "application/json");
    });

    app.MapGet("/assessments", [Authorize(Roles = Roles.Teacher)] () => Results.Redirect("/"));

    app.MapGet("/courses/{courseId}", [AllowAnonymous] (string courseId) => Results.Redirect($"/courses#/{Uri.EscapeDataString(courseId)}"));
    app.MapGet("/courses/{courseId}/{unitId}", [AllowAnonymous] (string courseId, string unitId) => Results.Redirect($"/courses#/{Uri.EscapeDataString(courseId)}/{Uri.EscapeDataString(unitId)}"));
    app.MapGet("/courses/{courseId}/{unitId}/quiz", [AllowAnonymous] (string courseId, string unitId) => Results.Redirect($"/courses#/{Uri.EscapeDataString(courseId)}/{Uri.EscapeDataString(unitId)}/quiz"));

    app.MapDelete("/courses/{courseId}/{unitId}/build", [Authorize(Roles = Roles.Teacher)] async (HttpContext context, IAntiforgery antiforgery, string courseId, string unitId, CourseService storage, CacheService cache) =>
    {
      var csrfError = await ValidateAntiForgeryAsync(context, antiforgery);
      if (csrfError is not null)
      {
        return csrfError;
      }

      if (string.IsNullOrWhiteSpace(courseId) || string.IsNullOrWhiteSpace(unitId))
      {
        return Results.BadRequest("Course ID and unit ID are required.");
      }

      var course = await storage.TryGetCourseAsync(courseId);
      var unit = await storage.TryGetUnitAsync(courseId, unitId);
      if (course is null || unit is null)
      {
        return Results.NotFound("Assessment not found.");
      }

      if (!context.User.CanEditCourse(course))
      {
        return Results.Forbid();
      }

      if (unit.KeyKnowledgeStatus > 0 || unit.AssessmentStatus > 0)
      {
        return Results.BadRequest("You cannot delete a unit which has a key knowledge sheet or assessment.");
      }

      await storage.DeleteUnitAsync(courseId, unitId);
      cache.Invalidate("units", unitId);
      return Results.NoContent();
    });

    app.MapPost("/courses/{courseId}/{unitId}/build/key-knowledge", [Authorize(Roles = Roles.Teacher)] async (HttpContext context, IAntiforgery antiforgery, string courseId, string unitId, KeyKnowledge keyKnowledge, CourseService storage, CacheService cache) =>
    {
      var csrfError = await ValidateAntiForgeryAsync(context, antiforgery);
      if (csrfError is not null)
      {
        return csrfError;
      }

      if (string.IsNullOrWhiteSpace(courseId) || string.IsNullOrWhiteSpace(unitId) || keyKnowledge is null)
      {
        return Results.BadRequest("Data missing.");
      }

      var course = await storage.TryGetCourseAsync(courseId);
      var unit = await storage.TryGetUnitAsync(courseId, unitId);
      if (course is null || unit is null)
      {
        return Results.NotFound("Assessment not found.");
      }

      if (!context.User.CanEditCourse(course))
      {
        return Results.Forbid();
      }

      var questionBank = await storage.GetBlobAsync<QuestionBank>(unitId);
      questionBank.Questions ??= [];
      keyKnowledge.RevisionQuiz = [];
      await storage.UploadBlobAsync(unitId, keyKnowledge);
      var previousStatus = unit.KeyKnowledgeStatus;
      var previousQuizStatus = unit.RevisionQuizStatus;
      var hasKeyKnowledge = keyKnowledge.DeclarativeKnowledge.Count > 0 || keyKnowledge.ProceduralKnowledge.Count > 0;
      unit.KeyKnowledgeStatus = hasKeyKnowledge ? 1 : 0;
      if (!hasKeyKnowledge)
      {
        if (questionBank.Questions.Count > 0)
        {
          questionBank.Questions = [];
          await storage.UploadBlobAsync(unitId, questionBank);
        }

        unit.RevisionQuizStatus = 0;
      }
      else
      {
        unit.RevisionQuizStatus = questionBank.Questions.Count == 0 ? 0 : 1;
      }
      if (unit.KeyKnowledgeStatus != previousStatus || unit.RevisionQuizStatus != previousQuizStatus)
      {
        await storage.UpdateUnitAsync(unit);
        cache.Invalidate("units");
      }

      cache.Update(unitId, JsonSerializer.Serialize(keyKnowledge, JsonDefaults.CamelCase));
      return Results.NoContent();
    });

    app.MapPost("/courses/{courseId}/{unitId}/build/quiz", [Authorize(Roles = Roles.Teacher)] async (HttpContext context, IAntiforgery antiforgery, string courseId, string unitId, QuestionBank questionBank, CourseService storage, CacheService cache) =>
    {
      var csrfError = await ValidateAntiForgeryAsync(context, antiforgery);
      if (csrfError is not null)
      {
        return csrfError;
      }

      if (string.IsNullOrWhiteSpace(courseId) || string.IsNullOrWhiteSpace(unitId) || questionBank is null)
      {
        return Results.BadRequest("Data missing.");
      }

      var course = await storage.TryGetCourseAsync(courseId);
      var unit = await storage.TryGetUnitAsync(courseId, unitId);
      if (course is null || unit is null)
      {
        return Results.NotFound("Assessment not found.");
      }

      if (!context.User.CanEditCourse(course))
      {
        return Results.Forbid();
      }

      questionBank.Questions ??= [];
      await storage.UploadBlobAsync(unitId, questionBank);

      var previousStatus = unit.RevisionQuizStatus;
      unit.RevisionQuizStatus = questionBank.Questions.Count == 0 ? 0 : 1;
      if (unit.RevisionQuizStatus != previousStatus)
      {
        await storage.UpdateUnitAsync(unit);
        cache.Invalidate("units");
      }

      return Results.NoContent();
    });

    app.MapPost("/courses/{courseId}/{unitId}/build/assessment", [Authorize(Roles = Roles.Teacher)] async (HttpContext context, IAntiforgery antiforgery, string courseId, string unitId, Assessment assessment, CourseService storage, CacheService cache) =>
    {
      var csrfError = await ValidateAntiForgeryAsync(context, antiforgery);
      if (csrfError is not null)
      {
        return csrfError;
      }

      if (string.IsNullOrWhiteSpace(courseId) || string.IsNullOrWhiteSpace(unitId) || assessment is null)
      {
        return Results.BadRequest("Data missing.");
      }

      var course = await storage.TryGetCourseAsync(courseId);
      var unit = await storage.TryGetUnitAsync(courseId, unitId);
      if (course is null || unit is null)
      {
        return Results.NotFound("Assessment not found.");
      }

      if (!context.User.CanEditCourse(course))
      {
        return Results.Forbid();
      }

      await storage.UploadBlobAsync(unitId, assessment);
      var previousStatus = unit.AssessmentStatus;
      unit.AssessmentStatus = assessment.Sections.SelectMany(o => o.Questions).Any() ? 1 : 0;
      if (unit.AssessmentStatus != previousStatus)
      {
        await storage.UpdateUnitAsync(unit);
        cache.Invalidate("units");
      }

      return Results.NoContent();
    });

    app.MapPost("/courses/{courseId}/{unitId}/build/{item}-complete", [Authorize(Roles = Roles.Teacher)] async (HttpContext context, IAntiforgery antiforgery, string courseId, string unitId, string item, CourseService storage, CacheService cache) =>
    {
      var csrfError = await ValidateAntiForgeryAsync(context, antiforgery);
      if (csrfError is not null)
      {
        return csrfError;
      }

      if (string.IsNullOrWhiteSpace(courseId) || string.IsNullOrWhiteSpace(unitId) || string.IsNullOrWhiteSpace(item))
      {
        return Results.BadRequest("Data missing.");
      }

      var course = await storage.TryGetCourseAsync(courseId);
      var unit = await storage.TryGetUnitAsync(courseId, unitId);
      if (course is null || unit is null)
      {
        return Results.NotFound("Assessment not found.");
      }

      if (!context.User.CanEditCourse(course))
      {
        return Results.Forbid();
      }

      switch (item.ToLowerInvariant())
      {
        case "key-knowledge":
          var storedKeyKnowledge = await storage.GetBlobAsync<KeyKnowledge>(unitId);
          var keyKnowledgeError = GetKeyKnowledgeCompletionError(storedKeyKnowledge);
          if (keyKnowledgeError is not null)
          {
            return Results.BadRequest(keyKnowledgeError);
          }
          unit.KeyKnowledgeStatus = 2;
          break;
        case "quiz":
          var questionBank = await storage.GetBlobAsync<QuestionBank>(unitId);
          questionBank.Questions ??= [];
          if (questionBank.Questions.Count == 0)
          {
            return Results.BadRequest("At least one quiz question is required.");
          }
          if (questionBank.Questions.Any(o => string.IsNullOrWhiteSpace(o.Question)))
          {
            return Results.BadRequest("Quiz questions cannot be blank.");
          }
          if (questionBank.Questions.Any(o => string.IsNullOrWhiteSpace(o.CorrectAnswer)))
          {
            return Results.BadRequest("Every quiz question must have a correct answer.");
          }
          if (questionBank.Questions.Any(o => string.IsNullOrWhiteSpace(o.IncorrectAnswer1) || string.IsNullOrWhiteSpace(o.IncorrectAnswer2) || string.IsNullOrWhiteSpace(o.IncorrectAnswer3)))
          {
            return Results.BadRequest("Every quiz question must have three incorrect answers.");
          }
          var keyKnowledge = await storage.GetBlobAsync<KeyKnowledge>(unitId);
          keyKnowledge.RevisionQuiz = BuildRevisionQuiz(questionBank);
          await storage.UploadBlobAsync(unitId, keyKnowledge);
          cache.Update(unitId, JsonSerializer.Serialize(keyKnowledge, JsonDefaults.CamelCase));
          unit.RevisionQuizStatus = 2;
          break;
        case "assessment":
          var assessment = await storage.GetBlobAsync<Assessment>(unitId);
          var assessmentError = GetAssessmentCompletionError(assessment);
          if (assessmentError is not null)
          {
            return Results.BadRequest(assessmentError);
          }
          unit.AssessmentStatus = 2;
          break;
        default:
          return Results.BadRequest("Invalid item specified.");
      }

      await storage.UpdateUnitAsync(unit);
      cache.Invalidate("units");
      return Results.NoContent();
    });

    app.MapPut("/courses/{courseId}/build/sort-units", [Authorize(Roles = Roles.Teacher)] async (HttpContext context, IAntiforgery antiforgery, string courseId, UnitSortOrder model, CourseService storage, CacheService cache) =>
    {
      var csrfError = await ValidateAntiForgeryAsync(context, antiforgery);
      if (csrfError is not null)
      {
        return csrfError;
      }

      if (string.IsNullOrWhiteSpace(courseId) || model.Order is null || model.Order.Count == 0)
      {
        return Results.BadRequest("Data missing.");
      }

      var course = await storage.TryGetCourseAsync(courseId);
      if (course is null)
      {
        return Results.NotFound("Course not found.");
      }

      if (!context.User.CanEditCourse(course))
      {
        return Results.Forbid();
      }

      for (var i = 0; i < model.Order.Count; i++)
      {
        var unitId = model.Order[i];
        var unit = await storage.TryGetUnitAsync(courseId, unitId);
        if (unit is null)
        {
          return Results.NotFound($"Unit {unitId} not found.");
        }

        if (unit.Order == i)
        {
          continue;
        }

        unit.Order = i;
        await storage.UpdateUnitAsync(unit);
      }

      cache.Invalidate("units");
      return Results.NoContent();
    });

    app.MapPut("/courses/{courseId}/build/{property}", [Authorize(Roles = Roles.Teacher)] async (HttpContext context, IAntiforgery antiforgery, string courseId, string property, SingleValueModel model, CourseService storage, CacheService cache) =>
    {
      var csrfError = await ValidateAntiForgeryAsync(context, antiforgery);
      if (csrfError is not null)
      {
        return csrfError;
      }

      if (string.IsNullOrWhiteSpace(courseId) || model is null || string.IsNullOrWhiteSpace(property))
      {
        return Results.BadRequest("Data missing.");
      }

      var course = await storage.TryGetCourseAsync(courseId);
      if (course is null)
      {
        return Results.NotFound("Course not found.");
      }

      if (!context.User.CanEditCourse(course))
      {
        return Results.Forbid();
      }

      var value = model.Value?.Trim() ?? string.Empty;
      switch (property.ToLowerInvariant())
      {
        case "intent":
          course.Intent = value;
          break;
        case "specification":
          course.Specification = value;
          break;
        case "assignment-length":
          if (!context.User.IsInRole(Roles.Admin))
          {
            return Results.Forbid();
          }
          if (!int.TryParse(value, out var assignmentLength) || assignmentLength < 0 || assignmentLength > 99)
          {
            return Results.BadRequest("Assignment length must be a whole number from 0 to 99.");
          }
          course.AssignmentLength = assignmentLength;
          break;
        default:
          return Results.BadRequest("Invalid property specified.");
      }

      await storage.UpdateCourseAsync(course);
      cache.Invalidate("courses");
      return Results.NoContent();
    });

    app.MapPut("/courses/{courseId}/{unitId}/build/{property}", [Authorize(Roles = Roles.Teacher)] async (HttpContext context, IAntiforgery antiforgery, string courseId, string unitId, string property, SingleValueModel model, CourseService storage, CacheService cache) =>
    {
      var csrfError = await ValidateAntiForgeryAsync(context, antiforgery);
      if (csrfError is not null)
      {
        return csrfError;
      }

      if (string.IsNullOrWhiteSpace(courseId) || string.IsNullOrWhiteSpace(unitId) || string.IsNullOrWhiteSpace(property) || model is null)
      {
        return Results.BadRequest("Data missing.");
      }

      var course = await storage.TryGetCourseAsync(courseId);
      if (course is null)
      {
        return Results.NotFound("Course not found.");
      }

      if (!context.User.CanEditCourse(course))
      {
        return Results.Forbid();
      }

      var unit = await storage.TryGetUnitAsync(courseId, unitId);
      if (unit is null)
      {
        return Results.NotFound("Unit not found.");
      }

      var value = model.Value?.Trim() ?? string.Empty;
      switch (property.ToLowerInvariant())
      {
        case "rename":
          unit.Title = value;
          break;
        case "why-this":
          unit.WhyThis = value;
          break;
        case "why-now":
          unit.WhyNow = value;
          break;
        case "scheme-url":
          unit.SchemeUrl = value;
          break;
        case "assessment-url":
          unit.AssessmentUrl = value;
          break;
        case "mark-scheme-url":
          unit.MarkSchemeUrl = value;
          break;
        case "checklist":
          unit.Checklist = value;
          break;
        case "term":
          if (value is not "Autumn" and not "Spring" and not "Summer")
          {
            return Results.BadRequest("Invalid term specified.");
          }

          unit.Term = value;
          break;
        default:
          return Results.BadRequest("Invalid property specified.");
      }

      await storage.UpdateUnitAsync(unit);
      cache.Invalidate("units");
      return Results.NoContent();
    });

    app.MapPost("/courses/{courseId}/build", [Authorize(Roles = Roles.Teacher)] async (HttpContext context, IAntiforgery antiforgery, string courseId, NewUnitModel model, CourseService storage, CacheService cache) =>
    {
      var csrfError = await ValidateAntiForgeryAsync(context, antiforgery);
      if (csrfError is not null)
      {
        return csrfError;
      }

      if (string.IsNullOrWhiteSpace(courseId) || string.IsNullOrWhiteSpace(model.Title) || model.YearGroup < 7 || model.YearGroup > 13)
      {
        return Results.BadRequest("Invalid data.");
      }

      var course = await storage.TryGetCourseAsync(courseId);
      if (course is null)
      {
        return Results.NotFound("Course not found.");
      }

      if (!context.User.CanEditCourse(course))
      {
        return Results.Forbid();
      }

      var unit = new UnitEntity
      {
        PartitionKey = courseId,
        RowKey = Guid.NewGuid().ToString("D"),
        Title = model.Title.Trim(),
        YearGroup = model.YearGroup,
        AssessmentUrl = string.Empty,
        Checklist = string.Empty,
        KeyKnowledgeStatus = 0,
        AssessmentStatus = 0,
        MarkSchemeUrl = string.Empty
      };

      await storage.UpdateUnitAsync(unit);
      cache.Invalidate("units");
      return Results.Json(unit);
    });

    app.MapPost("/courses/build/ai/import", [Authorize(Roles = Roles.Teacher)] async (HttpContext context, IAntiforgery antiforgery, SingleValueModel model, CourseService storage, AIService ai) =>
    {
      var csrfError = await ValidateAntiForgeryAsync(context, antiforgery);
      if (csrfError is not null)
      {
        return csrfError;
      }

      if (string.IsNullOrWhiteSpace(model?.Value))
      {
        return Results.BadRequest("Text assessment data is required.");
      }

      var courses = await storage.ListCoursesAsync();
      if (!courses.Any(course => context.User.CanEditCourse(course)))
      {
        return Results.Forbid();
      }

      var assessment = await ai.ImportTextAssessmentAsync(model.Value);
      return Results.Content(JsonSerializer.Serialize(assessment, JsonDefaults.CamelCase), "application/json");
    });

    app.MapGet("/courses/build/ai/createquizzes", [Authorize(Roles = Roles.Admin)] async (AIService ai) =>
    {
      var processed = await ai.CreateQuizQuestionsAsync();
      return Results.Text($"{processed} quizzes created", "text/plain");
    });

    app.MapPost("/courses/{courseId}/build/ai/generatemarkscheme", [Authorize(Roles = Roles.Teacher)] async (HttpContext context, IAntiforgery antiforgery, string courseId, AssessmentQuestion question, CourseService storage, AIService ai) =>
    {
      var csrfError = await ValidateAntiForgeryAsync(context, antiforgery);
      if (csrfError is not null)
      {
        return csrfError;
      }

      if (string.IsNullOrWhiteSpace(question?.Question))
      {
        return Results.BadRequest("Question is required.");
      }

      if (!string.IsNullOrEmpty(question.MarkScheme))
      {
        return Results.BadRequest("Mark scheme already exists");
      }

      var courses = await storage.ListCoursesAsync();
      if (!courses.Any(course => context.User.CanEditCourse(course)))
      {
        return Results.Forbid();
      }

      var markScheme = await ai.GenerateMarkSchemeAsync(courseId, question);
      return Results.Content(JsonSerializer.Serialize(markScheme, JsonDefaults.CamelCase), "application/json");
    });

    app.MapPost("/courses/build/ai/generatekeyknowledge", [Authorize(Roles = Roles.Teacher)] async (HttpContext context, IAntiforgery antiforgery, SingleValueModel model, CourseService storage, AIService ai) =>
    {
      var csrfError = await ValidateAntiForgeryAsync(context, antiforgery);
      if (csrfError is not null)
      {
        return csrfError;
      }

      if (string.IsNullOrWhiteSpace(model?.Value))
      {
        return Results.BadRequest("Learning outcomes are required.");
      }

      var courses = await storage.ListCoursesAsync();
      if (!courses.Any(course => context.User.CanEditCourse(course)))
      {
        return Results.Forbid();
      }

      var keyKnowledge = await ai.GenerateKeyKnowledgeAsync(model.Value);
      return Results.Json(keyKnowledge);
    });

    app.MapPost("/courses/build/ai/generatequestions", [Authorize(Roles = Roles.Teacher)] async (HttpContext context, IAntiforgery antiforgery, GenerateQuestionsRequest model, CourseService storage, AIService ai) =>
    {
      var csrfError = await ValidateAntiForgeryAsync(context, antiforgery);
      if (csrfError is not null)
      {
        return csrfError;
      }

      if (model is null || model.MultipleChoiceCount is < 0 or > 20 || model.ShortAnswerCount is < 0 or > 20 || model.DeclarativeKnowledge is null || model.DeclarativeKnowledge.Count == 0)
      {
        return Results.BadRequest("Invalid request data.");
      }

      var courses = await storage.ListCoursesAsync();
      if (!courses.Any(course => context.User.CanEditCourse(course)))
      {
        return Results.Forbid();
      }

      var questions = await ai.GenerateQuestionsAsync(model);
      return Results.Json(questions);
    });

    app.MapPost("/courses/{courseId}/{unitId}/build/ai/generatequiz", [Authorize(Roles = Roles.Teacher)] async (HttpContext context, IAntiforgery antiforgery, string courseId, string unitId, CourseService storage, AIService ai) =>
    {
      var csrfError = await ValidateAntiForgeryAsync(context, antiforgery);
      if (csrfError is not null)
      {
        return csrfError;
      }

      if (string.IsNullOrWhiteSpace(courseId) || string.IsNullOrWhiteSpace(unitId))
      {
        return Results.BadRequest("Data missing.");
      }

      var course = await storage.TryGetCourseAsync(courseId);
      var unit = await storage.TryGetUnitAsync(courseId, unitId);
      if (course is null || unit is null)
      {
        return Results.NotFound("Assessment not found.");
      }

      if (!context.User.CanEditCourse(course))
      {
        return Results.Forbid();
      }

      var keyKnowledge = await storage.GetBlobAsync<KeyKnowledge>(unitId);
      if (keyKnowledge.DeclarativeKnowledge.Count == 0)
      {
        return Results.BadRequest("Key knowledge is required before generating quiz questions.");
      }

      var questions = await ai.GenerateQuizQuestionsAsync(unit, keyKnowledge);
      var questionBank = new QuestionBank { Questions = questions };
      return Results.Json(questionBank);
    });

    app.MapGet("/courses/{courseId}/build/summary", [Authorize(Roles = Roles.Teacher)] async (HttpContext context, string courseId, CourseService storage, AIService ai) =>
    {
      var course = await storage.TryGetCourseAsync(courseId);
      if (course is null)
      {
        return Results.NotFound("Course not found.");
      }

      if (!context.User.CanEditCourse(course))
      {
        return Results.Forbid();
      }

      var units = await storage.ListUnitsAsync(courseId);
      if (units.Count == 0)
      {
        return Results.Text("No units found for this course.", "text/plain", Encoding.UTF8);
      }

      var summary = await ai.SummariseCourseAsync(course, units);
      return Results.Text(summary, "text/plain", Encoding.UTF8);
    });

    app.MapGet("/assignments/set", [Authorize(Roles = Roles.Admin)] async (AssignmentService assignmentService) =>
    {
      var now = DateOnly.FromDateTime(DateTime.UtcNow);
      var dueDate = now.AddDays((((int)DayOfWeek.Monday - (int)now.DayOfWeek + 6) % 7) + 1);
      dueDate = assignmentService.ResolveDueDate(dueDate);
      await assignmentService.GenerateAssignments(dueDate);
      return Results.Text($"Created assignments due {dueDate:yyyy-MM-dd}.");
    });

    app.MapGet("/test-emails", [Authorize(Roles = Roles.Admin)] async (AssignmentSettingService assignmentSettingService, CancellationToken cancellationToken) =>
    {
      var (tutorEmails, teacherEmails) = await assignmentSettingService.SendTestCompletionEmailsAsync(cancellationToken);
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
      var className = currentUser.Classes.FindMatchingClassName(partitionKey);
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

    app.MapPut("/api/users", [AllowAnonymous] async (HttpContext context, [FromHeader(Name = "X-Api-Key")] string auth, ConfigService config, AppOptions options) =>
    {
      if (string.IsNullOrEmpty(options.SyncApiKey)) return Results.Conflict("An sync API key is not configured.");
      if (auth != options.SyncApiKey) return Results.Unauthorized();

      var formFiles = context.Request.Form.Files;
      if (formFiles.Count != 2) return Results.BadRequest();
      if (formFiles.Any(o => o.Length == 0)) return Results.BadRequest();
      var teachersFile = formFiles.SingleOrDefault(o => o.Name == "teachers");
      var studentsFile = formFiles.SingleOrDefault(o => o.Name == "students");
      if (teachersFile is null || studentsFile is null) return Results.BadRequest();

      using (var teachersStream = teachersFile.OpenReadStream())
      {
        using var teachersReader = new StreamReader(teachersStream);
        var teachersCsv = await teachersReader.ReadToEndAsync();
        await config.UpdateDataFileAsync("teachers.csv", teachersCsv);
      }

      using (var studentsStream = studentsFile.OpenReadStream())
      {
        using var studentsReader = new StreamReader(studentsStream);
        var studentsCsv = await studentsReader.ReadToEndAsync();
        await config.UpdateDataFileAsync("students.csv", studentsCsv);
      }

      await config.ReloadAsync();
      return Results.NoContent();
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

