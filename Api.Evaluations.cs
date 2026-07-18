using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;

namespace CurriculumPortal;

public static partial class Api
{
  private static void MapEvaluationPaths(WebApplication app)
  {
    if (app.Environment.IsDevelopment())
    {
      app.MapGet("/courses/evaluate", [Authorize(Roles = Roles.Admin)] async (CourseEvaluationService evaluationService, CancellationToken cancellationToken) =>
      {
        var result = await evaluationService.RefreshOutdatedEvaluationsAsync(cancellationToken);
        return Results.Json(result);
      });
    }

    app.MapPost("/courses/{courseId}/evaluate", [Authorize(Roles = Roles.Admin)] async (HttpContext context, IAntiforgery antiforgery, string courseId, CourseService courseService, CourseEvaluationService evaluationService, ConfigService config) =>
    {
      var csrfError = await ValidateAntiForgeryAsync(context, antiforgery);
      if (csrfError is not null)
      {
        return csrfError;
      }

      var course = await courseService.TryGetCourseAsync(courseId, context.RequestAborted);
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

      var units = await courseService.ListUnitsAsync(courseId, context.RequestAborted);
      return CreateProgressStream(async (reportProgress, ct) =>
      {
        var generatedAt = await evaluationService.RegenerateSectionsAsync(course, units, new CourseEvaluation(), units, true, reportProgress, ct);

        return new { message = "The evaluation has been saved.", generatedAt, url = $"/courses/{Uri.EscapeDataString(courseId)}/evaluation" };
      }, context.RequestAborted);
    });

    app.MapPost("/courses/{courseId}/evaluate/overview", [Authorize(Roles = Roles.Teacher)] async (HttpContext context, IAntiforgery antiforgery, string courseId, CourseService courseService, CourseEvaluationService evaluationService, ConfigService config) =>
    {
      var csrfError = await ValidateAntiForgeryAsync(context, antiforgery);
      if (csrfError is not null)
      {
        return csrfError;
      }

      var course = await courseService.TryGetCourseAsync(courseId, context.RequestAborted);
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

      var evaluation = await courseService.TryGetCourseEvaluationAsync(courseId, context.RequestAborted);
      if (evaluation is null)
      {
        return Results.NotFound("Evaluation not found.");
      }

      var units = await courseService.ListUnitsAsync(courseId, context.RequestAborted);
      return CreateProgressStream(async (reportProgress, ct) =>
      {
        var generatedAt = await evaluationService.RegenerateSectionsAsync(course, units, evaluation, [], true, reportProgress, ct);

        return new { message = "The evaluation has been saved.", generatedAt, url = $"/courses/{Uri.EscapeDataString(courseId)}/evaluation" };
      }, context.RequestAborted);
    });

    app.MapPost("/courses/{courseId}/evaluate/units/{unitId}", [Authorize(Roles = Roles.Teacher)] async (HttpContext context, IAntiforgery antiforgery, string courseId, string unitId, CourseService courseService, CourseEvaluationService evaluationService, ConfigService config) =>
    {
      var csrfError = await ValidateAntiForgeryAsync(context, antiforgery);
      if (csrfError is not null)
      {
        return csrfError;
      }

      var course = await courseService.TryGetCourseAsync(courseId, context.RequestAborted);
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

      var evaluation = await courseService.TryGetCourseEvaluationAsync(courseId, context.RequestAborted);
      if (evaluation is null)
      {
        return Results.NotFound("Evaluation not found.");
      }

      var units = await courseService.ListUnitsAsync(courseId, context.RequestAborted);
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
        var generatedAt = await evaluationService.RegenerateSectionsAsync(course, units, evaluation, [unit], false, reportProgress, ct);

        return new { message = "The evaluation has been saved.", generatedAt, url = $"/courses/{Uri.EscapeDataString(courseId)}/evaluation" };
      }, context.RequestAborted);
    });
  }
}
