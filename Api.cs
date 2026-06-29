using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Buffers;
using System.Globalization;
using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace CurriculumPortal;

public static class Api
{
  public static void MapApiPaths(this WebApplication app)
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

    app.MapDelete("/courses/{courseId}/{unitId}/build", [Authorize(Roles = Roles.Teacher)] async (HttpContext context, IAntiforgery antiforgery, string courseId, string unitId, CourseService storage, ConfigService config, CacheService cache) =>
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

      if (!context.User.CanEditCourse(course, config))
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

    app.MapPost("/courses/{courseId}/{unitId}/build/key-knowledge", [Authorize(Roles = Roles.Teacher)] async (HttpContext context, IAntiforgery antiforgery, string courseId, string unitId, KeyKnowledge keyKnowledge, CourseService storage, ConfigService config, CacheService cache) =>
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

      if (!context.User.CanEditCourse(course, config))
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

    app.MapPost("/courses/{courseId}/{unitId}/build/quiz", [Authorize(Roles = Roles.Teacher)] async (HttpContext context, IAntiforgery antiforgery, string courseId, string unitId, QuestionBank questionBank, CourseService storage, ConfigService config, CacheService cache) =>
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

      if (!context.User.CanEditCourse(course, config))
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

    app.MapPost("/courses/{courseId}/{unitId}/build/assessment", [Authorize(Roles = Roles.Teacher)] async (HttpContext context, IAntiforgery antiforgery, string courseId, string unitId, Assessment assessment, CourseService storage, ConfigService config, CacheService cache) =>
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

      if (!context.User.CanEditCourse(course, config))
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

    app.MapPost("/courses/{courseId}/{unitId}/build/{item}-complete", [Authorize(Roles = Roles.Teacher)] async (HttpContext context, IAntiforgery antiforgery, string courseId, string unitId, string item, CourseService storage, ConfigService config, CacheService cache) =>
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

      if (!context.User.CanEditCourse(course, config))
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

    app.MapPut("/courses/{courseId}/build/sort-units", [Authorize(Roles = Roles.Teacher)] async (HttpContext context, IAntiforgery antiforgery, string courseId, UnitSortOrder model, CourseService storage, ConfigService config, CacheService cache) =>
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

      if (!context.User.CanEditCourse(course, config))
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

    app.MapPut("/courses/{courseId}/build/{property}", [Authorize(Roles = Roles.Teacher)] async (HttpContext context, IAntiforgery antiforgery, string courseId, string property, SingleValueModel model, CourseService storage, ConfigService config, CacheService cache) =>
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

    app.MapPut("/courses/{courseId}/{unitId}/build/{property}", [Authorize(Roles = Roles.Teacher)] async (HttpContext context, IAntiforgery antiforgery, string courseId, string unitId, string property, SingleValueModel model, CourseService storage, ConfigService config, CacheService cache) =>
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

      if (!context.User.CanEditCourse(course, config))
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

    app.MapPost("/courses/{courseId}/build", [Authorize(Roles = Roles.Teacher)] async (HttpContext context, IAntiforgery antiforgery, string courseId, NewUnitModel model, CourseService storage, ConfigService config, CacheService cache) =>
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

      await storage.UpdateUnitAsync(unit);
      cache.Invalidate("units");
      return Results.Json(unit);
    });

    app.MapPost("/courses/build/ai/import", [Authorize(Roles = Roles.Teacher)] async (HttpContext context, IAntiforgery antiforgery, SingleValueModel model, CourseService storage, ConfigService config, AIService ai) =>
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

      var courses = await storage.ListCoursesAsync(context.RequestAborted);
      if (!courses.Any(course => context.User.CanEditCourse(course, config)))
      {
        return Results.Forbid();
      }

      return StreamAiOperation(ct => ai.ImportTextAssessmentAsync(model.Value, ct), context.RequestAborted);
    });

    app.MapGet("/courses/build/ai/createquizzes", [Authorize(Roles = Roles.Admin)] async (AIService ai, CancellationToken cancellationToken) =>
    {
      try
      {
        var processed = await ai.CreateQuizQuestionsAsync(cancellationToken);
        return Results.Text($"{processed} quizzes created", "text/plain");
      }
      catch (InsufficientTokensException ex)
      {
        return CreateInsufficientTokensResult(ex);
      }
    });

    app.MapPost("/courses/{courseId}/build/ai/generatemarkscheme", [Authorize(Roles = Roles.Teacher)] async (HttpContext context, IAntiforgery antiforgery, string courseId, AssessmentQuestion question, CourseService storage, ConfigService config, AIService ai, CancellationToken cancellationToken) =>
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

      var courses = await storage.ListCoursesAsync(cancellationToken);
      if (!courses.Any(course => context.User.CanEditCourse(course, config)))
      {
        return Results.Forbid();
      }

      try
      {
        var markScheme = await ai.GenerateMarkSchemeAsync(courseId, question, cancellationToken);
        return Results.Content(JsonSerializer.Serialize(markScheme, JsonDefaults.CamelCase), "application/json");
      }
      catch (InsufficientTokensException ex)
      {
        return CreateInsufficientTokensResult(ex);
      }
    });

    app.MapPost("/courses/build/ai/generatekeyknowledge", [Authorize(Roles = Roles.Teacher)] async (HttpContext context, IAntiforgery antiforgery, SingleValueModel model, CourseService storage, ConfigService config, AIService ai) =>
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

      var courses = await storage.ListCoursesAsync(context.RequestAborted);
      if (!courses.Any(course => context.User.CanEditCourse(course, config)))
      {
        return Results.Forbid();
      }

      return StreamAiOperation(ct => ai.GenerateKeyKnowledgeAsync(model.Value, ct), context.RequestAborted);
    });

    app.MapPost("/courses/build/ai/generatequestions", [Authorize(Roles = Roles.Teacher)] async (HttpContext context, IAntiforgery antiforgery, GenerateQuestionsRequest model, CourseService storage, ConfigService config, AIService ai) =>
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

      var courses = await storage.ListCoursesAsync(context.RequestAborted);
      if (!courses.Any(course => context.User.CanEditCourse(course, config)))
      {
        return Results.Forbid();
      }

      return StreamAiOperation(ct => ai.GenerateQuestionsAsync(model, ct), context.RequestAborted);
    });

    app.MapPost("/courses/{courseId}/{unitId}/build/ai/generatequiz", [Authorize(Roles = Roles.Teacher)] async (HttpContext context, IAntiforgery antiforgery, string courseId, string unitId, CourseService storage, ConfigService config, AIService ai) =>
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

      var course = await storage.TryGetCourseAsync(courseId, context.RequestAborted);
      var unit = await storage.TryGetUnitAsync(courseId, unitId, context.RequestAborted);
      if (course is null || unit is null)
      {
        return Results.NotFound("Assessment not found.");
      }

      if (!context.User.CanEditCourse(course, config))
      {
        return Results.Forbid();
      }

      var keyKnowledge = await storage.GetBlobAsync<KeyKnowledge>(unitId, context.RequestAborted);
      if (keyKnowledge.DeclarativeKnowledge.Count == 0)
      {
        return Results.BadRequest("Key knowledge is required before generating quiz questions.");
      }

      return StreamAiOperation(async ct =>
      {
        var questions = await ai.GenerateQuizQuestionsAsync(unit, keyKnowledge, ct);
        return new QuestionBank { Questions = questions };
      }, context.RequestAborted);
    });

    app.MapGet("/courses/{courseId}/build/summary", [Authorize(Roles = Roles.Teacher)] async (HttpContext context, string courseId, CourseService storage, ConfigService config, AIService ai, CancellationToken cancellationToken) =>
    {
      var course = await storage.TryGetCourseAsync(courseId, cancellationToken);
      if (course is null)
      {
        return Results.NotFound("Course not found.");
      }

      if (!context.User.CanEditCourse(course, config))
      {
        return Results.Forbid();
      }

      var units = await storage.ListUnitsAsync(courseId, cancellationToken);
      if (units.Count == 0)
      {
        return Results.Text("No units found for this course.", "text/plain", Encoding.UTF8);
      }

      var summary = await ai.SummariseCourseAsync(course, units, cancellationToken);
      return Results.Text(summary, "text/plain", Encoding.UTF8);
    });

    app.MapPost("/courses/{courseId}/evaluate", [Authorize(Roles = Roles.Admin)] async (HttpContext context, IAntiforgery antiforgery, string courseId, CourseService storage, ConfigService config, AIService ai) =>
    {
      var csrfError = await ValidateAntiForgeryAsync(context, antiforgery);
      if (csrfError is not null)
      {
        return csrfError;
      }

      var course = await storage.TryGetCourseAsync(courseId, context.RequestAborted);
      if (course is null)
      {
        return Results.NotFound("Course not found.");
      }

      if (!context.User.CanEditCourse(course, config))
      {
        return Results.Forbid();
      }

      if (course.KeyStage != 3)
      {
        return Results.BadRequest("Course evaluations are only available for Key Stage 3 courses.");
      }

      var units = await storage.ListUnitsAsync(courseId, context.RequestAborted);
      return CreateProgressStream(async (reportProgress, ct) =>
      {
        var result = await ai.EvaluateCourseAsync(course, units, reportProgress, ct);
        var evaluation = new CourseEvaluation
        {
          GeneratedAt = DateTimeOffset.UtcNow,
          Overall = result.Overall,
          Units = result.Units
        };
        await storage.UploadCourseEvaluationAsync(courseId, evaluation, CancellationToken.None);

        return new { message = "The evaluation has been saved.", generatedAt = evaluation.GeneratedAt, url = $"/courses/{Uri.EscapeDataString(courseId)}/evaluation" };
      }, context.RequestAborted);
    });

    app.MapPost("/courses/{courseId}/evaluate/overview", [Authorize(Roles = Roles.Teacher)] async (HttpContext context, IAntiforgery antiforgery, string courseId, CourseService storage, ConfigService config, AIService ai) =>
    {
      var csrfError = await ValidateAntiForgeryAsync(context, antiforgery);
      if (csrfError is not null)
      {
        return csrfError;
      }

      var course = await storage.TryGetCourseAsync(courseId, context.RequestAborted);
      if (course is null)
      {
        return Results.NotFound("Course not found.");
      }

      if (!context.User.CanEditCourse(course, config))
      {
        return Results.Forbid();
      }

      if (course.KeyStage != 3)
      {
        return Results.BadRequest("Course evaluations are only available for Key Stage 3 courses.");
      }

      var evaluation = await storage.TryGetCourseEvaluationAsync(courseId, context.RequestAborted);
      if (evaluation is null)
      {
        return Results.NotFound("Evaluation not found.");
      }

      var units = await storage.ListUnitsAsync(courseId, context.RequestAborted);
      return CreateProgressStream(async (reportProgress, ct) =>
      {
        evaluation.Overall = await ai.EvaluateCourseOverviewAsync(course, units, reportProgress, ct);
        evaluation.GeneratedAt = DateTimeOffset.UtcNow;
        await storage.UploadCourseEvaluationAsync(courseId, evaluation, CancellationToken.None);

        return new { message = "The evaluation has been saved.", generatedAt = evaluation.GeneratedAt, url = $"/courses/{Uri.EscapeDataString(courseId)}/evaluation" };
      }, context.RequestAborted);
    });

    app.MapPost("/courses/{courseId}/evaluate/units/{unitId}", [Authorize(Roles = Roles.Teacher)] async (HttpContext context, IAntiforgery antiforgery, string courseId, string unitId, CourseService storage, ConfigService config, AIService ai) =>
    {
      var csrfError = await ValidateAntiForgeryAsync(context, antiforgery);
      if (csrfError is not null)
      {
        return csrfError;
      }

      var course = await storage.TryGetCourseAsync(courseId, context.RequestAborted);
      if (course is null)
      {
        return Results.NotFound("Course not found.");
      }

      if (!context.User.CanEditCourse(course, config))
      {
        return Results.Forbid();
      }

      if (course.KeyStage != 3)
      {
        return Results.BadRequest("Course evaluations are only available for Key Stage 3 courses.");
      }

      var evaluation = await storage.TryGetCourseEvaluationAsync(courseId, context.RequestAborted);
      if (evaluation is null)
      {
        return Results.NotFound("Evaluation not found.");
      }

      var units = await storage.ListUnitsAsync(courseId, context.RequestAborted);
      var unit = units.FirstOrDefault(o => o.RowKey == unitId);
      if (unit is null)
      {
        return Results.NotFound("Unit not found.");
      }

      evaluation.Units ??= [];
      var unitIndex = units.FindIndex(o => o.RowKey == unitId);
      var evaluationUnitIndex = evaluation.Units.FindIndex(o => string.Equals(o.UnitId, unitId, StringComparison.Ordinal));
      if (evaluationUnitIndex < 0 && unitIndex >= 0 && unitIndex < evaluation.Units.Count)
      {
        evaluationUnitIndex = unitIndex;
      }

      if (evaluationUnitIndex < 0)
      {
        return Results.NotFound("Unit evaluation not found.");
      }

      return CreateProgressStream(async (reportProgress, ct) =>
      {
        evaluation.Units[evaluationUnitIndex] = await ai.EvaluateCourseUnitAsync(course, units, unit, reportProgress, ct);
        evaluation.GeneratedAt = DateTimeOffset.UtcNow;
        await storage.UploadCourseEvaluationAsync(courseId, evaluation, CancellationToken.None);

        return new { message = "The evaluation has been saved.", generatedAt = evaluation.GeneratedAt, url = $"/courses/{Uri.EscapeDataString(courseId)}/evaluation" };
      }, context.RequestAborted);
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

