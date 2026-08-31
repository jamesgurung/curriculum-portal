using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CurriculumPortal;

[Authorize(Roles = Roles.Teacher)]
public class CourseEvaluationModel(CourseService courseService, CourseEvaluationService evaluationService, ConfigService config, IAntiforgery antiforgery) : PageModel
{
  public string CourseId { get; private set; } = string.Empty;
  public CourseEntity Course { get; private set; }
  public CourseEvaluation Evaluation { get; private set; }
  public IReadOnlyList<UnitEntity> Units { get; private set; } = [];
  public CourseEvaluationReport Report { get; private set; }
  public string CsrfToken { get; private set; } = string.Empty;
  public bool IsAdmin { get; private set; }
  public bool IsOverviewStale { get; private set; }
  private HashSet<string> StaleUnitIds { get; } = new(StringComparer.Ordinal);

  public async Task<IActionResult> OnGetAsync(string courseId)
  {
    if (string.IsNullOrWhiteSpace(courseId))
    {
      return BadRequest("Course ID is required.");
    }

    var course = await courseService.TryGetCourseAsync(courseId);
    if (course is null)
    {
      return NotFound("Course not found.");
    }

    if (!User.CanEditCourse(course, config))
    {
      return Forbid();
    }

    CourseId = courseId;
    Course = course;
    Evaluation = await courseService.TryGetCourseEvaluationAsync(courseId);
    Units = Evaluation is null ? [] : await courseService.ListUnitsAsync(courseId);
    if (Evaluation is not null)
    {
      var status = await evaluationService.GetStatusAsync(Course, Units, Evaluation, HttpContext.RequestAborted);
      IsOverviewStale = status.IsOverviewOutdated;
      StaleUnitIds.UnionWith(status.OutdatedUnitIds);
    }
    CsrfToken = antiforgery.GetAndStoreTokens(HttpContext).RequestToken ?? string.Empty;
    IsAdmin = User.IsInRole(Roles.Admin);
    Report = new CourseEvaluationReport
    {
      CourseId = CourseId,
      Course = Course,
      Evaluation = Evaluation,
      Units = Units,
      ShowBackButton = true,
      ShowControls = true,
      ShowGenerationDetails = true,
      CanEvaluateCourse = IsAdmin,
      IsOverviewStale = IsOverviewStale,
      StaleUnitIds = StaleUnitIds
    };

    return Page();
  }
}
