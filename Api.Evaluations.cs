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
      var sourceUpdatedAt = units.Select(o => o.Timestamp ?? DateTimeOffset.MinValue)
        .Append(course.Timestamp ?? DateTimeOffset.MinValue)
        .Max();
      var sourceUnitIds = units.Select(o => o.RowKey).ToList();
      var unitSourceUpdatedAt = units.ToDictionary(o => o.RowKey, o => o.Timestamp ?? DateTimeOffset.MinValue, StringComparer.Ordinal);
      return CreateProgressStream(async (reportProgress, ct) =>
      {
        var result = await ai.EvaluateCourseAsync(course, units, reportProgress, ct);
        var generatedAt = DateTimeOffset.UtcNow;
        result.Overall.GeneratedAt = generatedAt;
        result.Overall.Model = ai.ModelName;
        result.Overall.EvaluationSourceUpdatedAt = sourceUpdatedAt;
        result.Overall.SourceUnitIds = sourceUnitIds;
        foreach (var unit in result.Units)
        {
          unit.GeneratedAt = generatedAt;
          unit.Model = ai.ModelName;
          unit.EvaluationSourceUpdatedAt = unitSourceUpdatedAt.GetValueOrDefault(unit.UnitId, DateTimeOffset.MinValue);
        }

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
      var sourceUpdatedAt = units.Select(o => o.Timestamp ?? DateTimeOffset.MinValue)
        .Append(course.Timestamp ?? DateTimeOffset.MinValue)
        .Max();
      var sourceUnitIds = units.Select(o => o.RowKey).ToList();
      return CreateProgressStream(async (reportProgress, ct) =>
      {
        evaluation.Overall = await ai.EvaluateCourseOverviewAsync(course, units, reportProgress, ct);
        evaluation.Overall.GeneratedAt = DateTimeOffset.UtcNow;
        evaluation.Overall.Model = ai.ModelName;
        evaluation.Overall.EvaluationSourceUpdatedAt = sourceUpdatedAt;
        evaluation.Overall.SourceUnitIds = sourceUnitIds;
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

      var sourceUpdatedAt = unit.Timestamp ?? DateTimeOffset.MinValue;
      return CreateProgressStream(async (reportProgress, ct) =>
      {
        evaluation.Units[evaluationUnitIndex] = await ai.EvaluateCourseUnitAsync(course, units, unit, reportProgress, ct);
        evaluation.Units[evaluationUnitIndex].GeneratedAt = DateTimeOffset.UtcNow;
        evaluation.Units[evaluationUnitIndex].Model = ai.ModelName;
        evaluation.Units[evaluationUnitIndex].EvaluationSourceUpdatedAt = sourceUpdatedAt;
        await courseService.UploadCourseEvaluationAsync(courseId, evaluation, CancellationToken.None);

        return new { message = "The evaluation has been saved.", generatedAt = evaluation.Units[evaluationUnitIndex].GeneratedAt, url = $"/courses/{Uri.EscapeDataString(courseId)}/evaluation" };
      }, context.RequestAborted);
    });
  }
}
