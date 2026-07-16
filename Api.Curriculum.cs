using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using System.Text.Json;

namespace CurriculumPortal;

public static partial class Api
{
  private static void MapCurriculumPaths(WebApplication app)
  {
    app.MapGet("/keyknowledge", [AllowAnonymous] async (HttpContext context, string unit, CourseService courseService, CacheService cache) =>
    {
      if (unit?.Length != 36)
      {
        return Results.NotFound();
      }

      var data = await cache.GetCachedDataAsync(unit, () => courseService.GetBlobAsync<KeyKnowledge>(unit));

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

    app.MapDelete("/courses/{courseId}/{unitId}/build", [Authorize(Roles = Roles.Teacher)] async (HttpContext context, IAntiforgery antiforgery, string courseId, string unitId, CourseService courseService, ConfigService config, CacheService cache) =>
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

      var course = await courseService.TryGetCourseAsync(courseId);
      var unit = await courseService.TryGetUnitAsync(courseId, unitId);
      if (course is null || unit is null)
      {
        return Results.NotFound("Assessment not found.");
      }

      if (!context.User.CanEditCourse(course, config))
      {
        return Results.Forbid();
      }

      if (unit.KeyKnowledgeStatus > 0 || unit.AssessmentStatus > 0)
      {
        return Results.BadRequest("You cannot delete a unit which has a key knowledge sheet or assessment.");
      }

      await courseService.DeleteUnitAsync(courseId, unitId);
      cache.Invalidate("units", unitId);
      return Results.NoContent();
    });

    app.MapPost("/courses/{courseId}/{unitId}/build/key-knowledge", [Authorize(Roles = Roles.Teacher)] async (HttpContext context, IAntiforgery antiforgery, string courseId, string unitId, KeyKnowledge keyKnowledge, CourseService courseService, ConfigService config, CacheService cache) =>
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

      var course = await courseService.TryGetCourseAsync(courseId);
      var unit = await courseService.TryGetUnitAsync(courseId, unitId);
      if (course is null || unit is null)
      {
        return Results.NotFound("Assessment not found.");
      }

      if (!context.User.CanEditCourse(course, config))
      {
        return Results.Forbid();
      }

      var questionBank = await courseService.GetBlobAsync<QuestionBank>(unitId);
      questionBank.Questions ??= [];
      keyKnowledge.RevisionQuiz = [];
      await courseService.UploadBlobAsync(unitId, keyKnowledge);
      var hasKeyKnowledge = keyKnowledge.DeclarativeKnowledge.Count > 0 || keyKnowledge.ProceduralKnowledge.Count > 0;
      unit.KeyKnowledgeStatus = hasKeyKnowledge ? 1 : 0;
      if (!hasKeyKnowledge)
      {
        if (questionBank.Questions.Count > 0)
        {
          questionBank.Questions = [];
          await courseService.UploadBlobAsync(unitId, questionBank);
        }

        unit.RevisionQuizStatus = 0;
      }
      else
      {
        unit.RevisionQuizStatus = questionBank.Questions.Count == 0 ? 0 : 1;
      }
      await courseService.UpdateUnitAsync(unit);
      cache.Invalidate("units");

      cache.Update(unitId, JsonSerializer.Serialize(keyKnowledge, JsonDefaults.CamelCase));
      return Results.NoContent();
    });

    app.MapPost("/courses/{courseId}/{unitId}/build/quiz", [Authorize(Roles = Roles.Teacher)] async (HttpContext context, IAntiforgery antiforgery, string courseId, string unitId, QuestionBank questionBank, CourseService courseService, ConfigService config, CacheService cache) =>
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

      var course = await courseService.TryGetCourseAsync(courseId);
      var unit = await courseService.TryGetUnitAsync(courseId, unitId);
      if (course is null || unit is null)
      {
        return Results.NotFound("Assessment not found.");
      }

      if (!context.User.CanEditCourse(course, config))
      {
        return Results.Forbid();
      }

      questionBank.Questions ??= [];
      await courseService.UploadBlobAsync(unitId, questionBank);

      unit.RevisionQuizStatus = questionBank.Questions.Count == 0 ? 0 : 1;
      await courseService.UpdateUnitAsync(unit);
      cache.Invalidate("units");

      return Results.NoContent();
    });

    app.MapPost("/courses/{courseId}/{unitId}/build/assessment", [Authorize(Roles = Roles.Teacher)] async (HttpContext context, IAntiforgery antiforgery, string courseId, string unitId, Assessment assessment, CourseService courseService, ConfigService config, CacheService cache) =>
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

      var course = await courseService.TryGetCourseAsync(courseId);
      var unit = await courseService.TryGetUnitAsync(courseId, unitId);
      if (course is null || unit is null)
      {
        return Results.NotFound("Assessment not found.");
      }

      if (!context.User.CanEditCourse(course, config))
      {
        return Results.Forbid();
      }

      await courseService.UploadBlobAsync(unitId, assessment);
      unit.AssessmentStatus = assessment.Sections.SelectMany(o => o.Questions).Any() ? 1 : 0;
      await courseService.UpdateUnitAsync(unit);
      cache.Invalidate("units");

      return Results.NoContent();
    });

    app.MapPost("/courses/{courseId}/{unitId}/build/{item}-complete", [Authorize(Roles = Roles.Teacher)] async (HttpContext context, IAntiforgery antiforgery, string courseId, string unitId, string item, CourseService courseService, ConfigService config, CacheService cache) =>
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

      var course = await courseService.TryGetCourseAsync(courseId);
      var unit = await courseService.TryGetUnitAsync(courseId, unitId);
      if (course is null || unit is null)
      {
        return Results.NotFound("Assessment not found.");
      }

      if (!context.User.CanEditCourse(course, config))
      {
        return Results.Forbid();
      }

      switch (item.ToLowerInvariant())
      {
        case "key-knowledge":
          var storedKeyKnowledge = await courseService.GetBlobAsync<KeyKnowledge>(unitId);
          var keyKnowledgeError = GetKeyKnowledgeCompletionError(storedKeyKnowledge);
          if (keyKnowledgeError is not null)
          {
            return Results.BadRequest(keyKnowledgeError);
          }
          unit.KeyKnowledgeStatus = 2;
          break;
        case "quiz":
          var questionBank = await courseService.GetBlobAsync<QuestionBank>(unitId);
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
          var keyKnowledge = await courseService.GetBlobAsync<KeyKnowledge>(unitId);
          keyKnowledge.RevisionQuiz = BuildRevisionQuiz(questionBank);
          await courseService.UploadBlobAsync(unitId, keyKnowledge);
          cache.Update(unitId, JsonSerializer.Serialize(keyKnowledge, JsonDefaults.CamelCase));
          unit.RevisionQuizStatus = 2;
          break;
        case "assessment":
          var assessment = await courseService.GetBlobAsync<Assessment>(unitId);
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

      await courseService.UpdateUnitAsync(unit);
      cache.Invalidate("units");
      return Results.NoContent();
    });

    app.MapPut("/courses/{courseId}/build/sort-units", [Authorize(Roles = Roles.Teacher)] async (HttpContext context, IAntiforgery antiforgery, string courseId, UnitSortOrder model, CourseService courseService, ConfigService config, CacheService cache) =>
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

      var course = await courseService.TryGetCourseAsync(courseId);
      if (course is null)
      {
        return Results.NotFound("Course not found.");
      }

      if (!context.User.CanEditCourse(course, config))
      {
        return Results.Forbid();
      }

      for (var i = 0; i < model.Order.Count; i++)
      {
        var unitId = model.Order[i];
        var unit = await courseService.TryGetUnitAsync(courseId, unitId);
        if (unit is null)
        {
          return Results.NotFound($"Unit {unitId} not found.");
        }

        if (unit.Order == i)
        {
          continue;
        }

        unit.Order = i;
        await courseService.UpdateUnitAsync(unit);
      }

      cache.Invalidate("units");
      return Results.NoContent();
    });

    app.MapPut("/courses/{courseId}/build/{property}", [Authorize(Roles = Roles.Teacher)] async (HttpContext context, IAntiforgery antiforgery, string courseId, string property, SingleValueModel model, CourseService courseService, ConfigService config, CacheService cache) =>
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

      var course = await courseService.TryGetCourseAsync(courseId);
      if (course is null)
      {
        return Results.NotFound("Course not found.");
      }

      if (!context.User.CanEditCourse(course, config))
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
        case "icon":
          if (!context.User.IsInRole(Roles.Admin))
          {
            return Results.Forbid();
          }
          course.Icon = value;
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

      await courseService.UpdateCourseAsync(course);
      cache.Invalidate("courses");
      return Results.NoContent();
    });

    app.MapPut("/courses/{courseId}/{unitId}/build/{property}", [Authorize(Roles = Roles.Teacher)] async (HttpContext context, IAntiforgery antiforgery, string courseId, string unitId, string property, SingleValueModel model, CourseService courseService, ConfigService config, CacheService cache) =>
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

      var course = await courseService.TryGetCourseAsync(courseId);
      if (course is null)
      {
        return Results.NotFound("Course not found.");
      }

      if (!context.User.CanEditCourse(course, config))
      {
        return Results.Forbid();
      }

      var unit = await courseService.TryGetUnitAsync(courseId, unitId);
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

      await courseService.UpdateUnitAsync(unit);
      cache.Invalidate("units");
      return Results.NoContent();
    });

    app.MapPost("/courses/{courseId}/build", [Authorize(Roles = Roles.Teacher)] async (HttpContext context, IAntiforgery antiforgery, string courseId, NewUnitModel model, CourseService courseService, ConfigService config, CacheService cache) =>
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

      var course = await courseService.TryGetCourseAsync(courseId);
      if (course is null)
      {
        return Results.NotFound("Course not found.");
      }

      if (!context.User.CanEditCourse(course, config))
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

      await courseService.UpdateUnitAsync(unit);
      cache.Invalidate("units");
      return Results.Json(unit);
    });
  }
}
