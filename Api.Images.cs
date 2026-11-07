using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;

namespace CurriculumPortal;

public static partial class Api
{
  private static void MapImagePaths(WebApplication app)
  {
    app.MapGet("/courses/build/ai/generateimages", [Authorize(Roles = Roles.Admin)] (HttpContext context, AIService ai) =>
    {
      context.Response.Headers.CacheControl = "no-store";
      return CreateProgressStream((report, ct) => ai.GenerateMissingImagesAsync(report, ct), context.RequestAborted);
    });

    app.MapPost("/courses/{courseId}/build/ai/generateimages", [Authorize(Roles = Roles.Admin)] async (HttpContext context, IAntiforgery antiforgery, string courseId, CourseService courseService, AIService ai) =>
    {
      var csrfError = await ValidateAntiForgeryAsync(context, antiforgery);
      if (csrfError is not null) return csrfError;

      var course = await courseService.TryGetCourseAsync(courseId, context.RequestAborted);
      if (course is null) return Results.NotFound();

      var units = await courseService.ListUnitsAsync(courseId, context.RequestAborted);
      if (units.Count == 0) return Results.BadRequest("The course has no units.");

      return CreateProgressStream((report, ct) => ai.GenerateCourseImagesAsync(course, units, report, ct), context.RequestAborted);
    });

    app.MapDelete("/courses/{courseId}/{unitId}/build/image", [Authorize(Roles = Roles.Admin)] async (HttpContext context, IAntiforgery antiforgery, string courseId, string unitId, CourseService courseService, CacheService cache) =>
    {
      var csrfError = await ValidateAntiForgeryAsync(context, antiforgery);
      if (csrfError is not null) return csrfError;

      var unit = await courseService.TryGetUnitAsync(courseId, unitId, context.RequestAborted);
      if (unit is null) return Results.NotFound();

      try
      {
        await courseService.DeleteUnitImageAsync(unit, context.RequestAborted);
      }
      finally
      {
        cache.Invalidate("units");
      }
      return Results.NoContent();
    });
  }
}
