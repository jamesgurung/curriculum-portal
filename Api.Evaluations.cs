using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;

namespace CurriculumPortal;

public static partial class Api
{
  private static void MapEvaluationPaths(WebApplication app)
  {
    app.MapPost("/courses/{courseId}/evaluate", [Authorize(Roles = Roles.Admin)] async (HttpContext context, IAntiforgery antiforgery, string courseId, CourseService courseService, ConfigService config, AIService ai) =>
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
        var result = await ai.EvaluateCourseAsync(course, units, reportProgress, ct);
        var generatedAt = DateTimeOffset.UtcNow;
        result.Overall.GeneratedAt = generatedAt;
        foreach (var unit in result.Units)
          unit.GeneratedAt = generatedAt;

        var evaluation = new CourseEvaluation
        {
          Overall = result.Overall,
          Units = result.Units
        };
        await courseService.UploadCourseEvaluationAsync(courseId, evaluation, CancellationToken.None);

        return new { message = "The evaluation has been saved.", generatedAt, url = $"/courses/{Uri.EscapeDataString(courseId)}/evaluation" };
      }, context.RequestAborted);
    });

    app.MapPost("/courses/{courseId}/evaluate/overview", [Authorize(Roles = Roles.Teacher)] async (HttpContext context, IAntiforgery antiforgery, string courseId, CourseService courseService, ConfigService config, AIService ai) =>
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
        evaluation.Overall = await ai.EvaluateCourseOverviewAsync(course, units, reportProgress, ct);
        evaluation.Overall.GeneratedAt = DateTimeOffset.UtcNow;
        await courseService.UploadCourseEvaluationAsync(courseId, evaluation, CancellationToken.None);

        return new { message = "The evaluation has been saved.", generatedAt = evaluation.Overall.GeneratedAt, url = $"/courses/{Uri.EscapeDataString(courseId)}/evaluation" };
      }, context.RequestAborted);
    });

    app.MapPost("/courses/{courseId}/evaluate/units/{unitId}", [Authorize(Roles = Roles.Teacher)] async (HttpContext context, IAntiforgery antiforgery, string courseId, string unitId, CourseService courseService, ConfigService config, AIService ai) =>
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
        evaluation.Units[evaluationUnitIndex] = await ai.EvaluateCourseUnitAsync(course, units, unit, reportProgress, ct);
        evaluation.Units[evaluationUnitIndex].GeneratedAt = DateTimeOffset.UtcNow;
        await courseService.UploadCourseEvaluationAsync(courseId, evaluation, CancellationToken.None);

        return new { message = "The evaluation has been saved.", generatedAt = evaluation.Units[evaluationUnitIndex].GeneratedAt, url = $"/courses/{Uri.EscapeDataString(courseId)}/evaluation" };
      }, context.RequestAborted);
    });
  }
}
