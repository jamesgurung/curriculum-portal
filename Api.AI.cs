using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using System.Text;
using System.Text.Json;

namespace CurriculumPortal;

public static partial class Api
{
  private static void MapCourseAiPaths(WebApplication app)
  {
    app.MapPost("/courses/build/ai/import", [Authorize(Roles = Roles.Teacher)] async (HttpContext context, IAntiforgery antiforgery, SingleValueModel model, CourseService courseService, ConfigService config, AIService ai) =>
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

      var courses = await courseService.ListCoursesAsync(context.RequestAborted);
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

    app.MapPost("/courses/{courseId}/build/ai/generatemarkscheme", [Authorize(Roles = Roles.Teacher)] async (HttpContext context, IAntiforgery antiforgery, string courseId, AssessmentQuestion question, CourseService courseService, ConfigService config, AIService ai, CancellationToken cancellationToken) =>
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

      var courses = await courseService.ListCoursesAsync(cancellationToken);
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

    app.MapPost("/courses/build/ai/generatekeyknowledge", [Authorize(Roles = Roles.Teacher)] async (HttpContext context, IAntiforgery antiforgery, SingleValueModel model, CourseService courseService, ConfigService config, AIService ai) =>
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

      var courses = await courseService.ListCoursesAsync(context.RequestAborted);
      if (!courses.Any(course => context.User.CanEditCourse(course, config)))
      {
        return Results.Forbid();
      }

      return StreamAiOperation(ct => ai.GenerateKeyKnowledgeAsync(model.Value, ct), context.RequestAborted);
    });

    app.MapPost("/courses/build/ai/generatequestions", [Authorize(Roles = Roles.Teacher)] async (HttpContext context, IAntiforgery antiforgery, GenerateQuestionsRequest model, CourseService courseService, ConfigService config, AIService ai) =>
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

      var courses = await courseService.ListCoursesAsync(context.RequestAborted);
      if (!courses.Any(course => context.User.CanEditCourse(course, config)))
      {
        return Results.Forbid();
      }

      return StreamAiOperation(ct => ai.GenerateQuestionsAsync(model, ct), context.RequestAborted);
    });

    app.MapPost("/courses/{courseId}/{unitId}/build/ai/generatequiz", [Authorize(Roles = Roles.Teacher)] async (HttpContext context, IAntiforgery antiforgery, string courseId, string unitId, CourseService courseService, ConfigService config, AIService ai) =>
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

      var course = await courseService.TryGetCourseAsync(courseId, context.RequestAborted);
      var unit = await courseService.TryGetUnitAsync(courseId, unitId, context.RequestAborted);
      if (course is null || unit is null)
      {
        return Results.NotFound("Assessment not found.");
      }

      if (!context.User.CanEditCourse(course, config))
      {
        return Results.Forbid();
      }

      var keyKnowledge = await courseService.GetBlobAsync<KeyKnowledge>(unitId, context.RequestAborted);
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

    app.MapGet("/courses/{courseId}/build/summary", [Authorize(Roles = Roles.Teacher)] async (HttpContext context, string courseId, CourseService courseService, ConfigService config, AIService ai, CancellationToken cancellationToken) =>
    {
      var course = await courseService.TryGetCourseAsync(courseId, cancellationToken);
      if (course is null)
      {
        return Results.NotFound("Course not found.");
      }

      if (!context.User.CanEditCourse(course, config))
      {
        return Results.Forbid();
      }

      var units = await courseService.ListUnitsAsync(courseId, cancellationToken);
      if (units.Count == 0)
      {
        return Results.Text("No units found for this course.", "text/plain", Encoding.UTF8);
      }

      var summary = await ai.SummariseCourseAsync(course, units, cancellationToken);
      return Results.Text(summary, "text/plain", Encoding.UTF8);
    });
  }
}
