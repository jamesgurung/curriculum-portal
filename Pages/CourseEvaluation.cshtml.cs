using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System.Globalization;

namespace CurriculumPortal;

[Authorize(Roles = Roles.Teacher)]
public class CourseEvaluationModel(CourseService storage, IAntiforgery antiforgery) : PageModel
{
  public string CourseId { get; private set; } = string.Empty;
  public CourseEntity Course { get; private set; }
  public CourseEvaluation Evaluation { get; private set; }
  public IReadOnlyList<UnitEntity> Units { get; private set; } = [];
  public string CsrfToken { get; private set; } = string.Empty;
  public bool IsAdmin { get; private set; }

  public string GeneratedAtText => Evaluation is null
    ? string.Empty
    : Evaluation.GeneratedAt.UtcDateTime.ToString("d MMMM yyyy", CultureInfo.CurrentCulture);

  public async Task<IActionResult> OnGetAsync(string courseId)
  {
    if (string.IsNullOrWhiteSpace(courseId))
    {
      return BadRequest("Course ID is required.");
    }

    var course = await storage.TryGetCourseAsync(courseId);
    if (course is null)
    {
      return NotFound("Course not found.");
    }

    if (!User.CanEditCourse(course))
    {
      return Forbid();
    }

    CourseId = courseId;
    Course = course;
    Evaluation = await storage.TryGetCourseEvaluationAsync(courseId);
    Units = Evaluation is null ? [] : await storage.ListUnitsAsync(courseId);
    CsrfToken = antiforgery.GetAndStoreTokens(HttpContext).RequestToken ?? string.Empty;
    IsAdmin = User.IsInRole(Roles.Admin);

    return Page();
  }
}
