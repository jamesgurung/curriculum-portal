using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System.Text.Json;

namespace CurriculumPortal;

[AllowAnonymous]
public class CoursesModel(CourseService courseService, CacheService cache, ConfigService config, AppOptions options, IAntiforgery antiforgery) : PageModel
{
  public string CoursesJson { get; private set; }
  public string UnitsJson { get; private set; }
  public string EditableCourseIdsJson { get; private set; } = "[]";
  public string IsAdminJson { get; private set; } = "false";
  public string ChecklistItemsJson { get; private set; } = "[]";
  public string CsrfToken { get; private set; }
  public string MicrosoftSharePointSubdomain { get; private set; } = options.MicrosoftSharePointSubdomain;
  public string SchoolName { get; private set; } = options.SchoolName;
  public string PageTitle { get; private set; } = $"Curriculum - {options.SchoolName}";
  public string MetaDescription { get; private set; } = $"Explore the curriculum at {options.SchoolName}, including courses, units, and revision quiz content.";
  public string CanonicalUrl { get; private set; } = $"{options.Website.TrimEnd('/')}/courses";
  public List<CourseEntity> PublicCourses { get; private set; } = [];
  public List<PublicFacingUnit> PublicUnits { get; private set; } = [];
  public CourseEntity SelectedCourse { get; private set; }
  public PublicFacingUnit SelectedUnit { get; private set; }
  public KeyKnowledge KeyKnowledge { get; private set; }
  public bool IsStaff { get; private set; }
  public bool IsEditableStaff { get; private set; }
  public bool ServerRender { get; private set; }
  public bool IsQuiz { get; private set; }

  public async Task<IActionResult> OnGetAsync(string courseId, string unitId, string action)
  {
    var isAuthenticated = User.Identity?.IsAuthenticated == true;
    IsStaff = isAuthenticated && User.IsInRole(Roles.Teacher);
    if (IsStaff)
    {
      var courses = await courseService.ListCoursesAsync();
      var units = await courseService.ListUnitsAsync();
      var isAdmin = User.IsInRole(Roles.Admin);
      var editableCourseIds = courses.Where(o => User.CanEditCourse(o, config)).Select(o => o.RowKey).OrderBy(o => o).ToList();

      CoursesJson = JsonSerializer.Serialize(courses, JsonDefaults.CamelCase);
      UnitsJson = JsonSerializer.Serialize(units, JsonDefaults.CamelCase);
      EditableCourseIdsJson = JsonSerializer.Serialize(editableCourseIds, JsonDefaults.CamelCase);
      IsAdminJson = JsonSerializer.Serialize(isAdmin, JsonDefaults.CamelCase);
      ChecklistItemsJson = JsonSerializer.Serialize(config.ChecklistItems, JsonDefaults.CamelCase);
      CsrfToken = antiforgery.GetAndStoreTokens(HttpContext).RequestToken ?? string.Empty;
      IsEditableStaff = editableCourseIds.Count > 0;

      return Page();
    }

    var cachedCourses = await cache.GetCachedDataAsync<List<CourseEntity>>("courses", () => courseService.ListCoursesAsync());
    var cachedUnits = await cache.GetCachedDataAsync<List<PublicFacingUnit>>("units", async () => (await courseService.ListUnitsAsync()).Select(o => new PublicFacingUnit(o)).ToList());

    CoursesJson = cachedCourses.Data;
    UnitsJson = cachedUnits.Data;

    if (isAuthenticated)
      return Page();

    ServerRender = true;
    PublicCourses = JsonSerializer.Deserialize<List<CourseEntity>>(CoursesJson, JsonDefaults.CamelCase) ?? [];
    PublicUnits = JsonSerializer.Deserialize<List<PublicFacingUnit>>(UnitsJson, JsonDefaults.CamelCase) ?? [];

    if (string.IsNullOrWhiteSpace(courseId))
      return string.IsNullOrWhiteSpace(unitId) && string.IsNullOrWhiteSpace(action) ? Page() : NotFound();

    SelectedCourse = PublicCourses.SingleOrDefault(course => course.RowKey == courseId);
    if (SelectedCourse is null)
      return NotFound();

    PageTitle = $"{SelectedCourse.Name} curriculum - {SchoolName}";
    MetaDescription = string.IsNullOrWhiteSpace(SelectedCourse.Intent) ? MetaDescription : SelectedCourse.Intent;
    CanonicalUrl = $"{options.Website.TrimEnd('/')}/courses/{Uri.EscapeDataString(courseId)}";

    if (string.IsNullOrWhiteSpace(unitId))
      return string.IsNullOrWhiteSpace(action) ? Page() : NotFound();

    SelectedUnit = PublicUnits.SingleOrDefault(unit => unit.CourseId == courseId && unit.Id == unitId);
    if (SelectedUnit is null || (action is not null && action != "quiz"))
      return NotFound();

    PageTitle = $"{SelectedUnit.Title} - {SelectedCourse.Name} - {SchoolName}";
    MetaDescription = !string.IsNullOrWhiteSpace(SelectedUnit.WhyThis)
      ? SelectedUnit.WhyThis
      : !string.IsNullOrWhiteSpace(SelectedUnit.WhyNow) ? SelectedUnit.WhyNow : MetaDescription;
    CanonicalUrl += $"/{Uri.EscapeDataString(unitId)}";
    IsQuiz = action == "quiz";
    if (!IsQuiz && SelectedUnit.HasKeyKnowledge)
    {
      var cachedKeyKnowledge = await cache.GetCachedDataAsync(unitId, () => courseService.GetBlobAsync<KeyKnowledge>(unitId));
      KeyKnowledge = JsonSerializer.Deserialize<KeyKnowledge>(cachedKeyKnowledge.Data, JsonDefaults.CamelCase) ?? new();
    }

    return Page();
  }
}

