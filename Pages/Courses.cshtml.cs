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
  public bool IsStaff { get; private set; }
  public bool IsEditableStaff { get; private set; }

  public async Task<IActionResult> OnGetAsync()
  {
    IsStaff = User.Identity?.IsAuthenticated == true && User.IsInRole(Roles.Teacher);
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

    return Page();
  }
}

