using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System.Globalization;
using System.Text.Json;

namespace CurriculumPortal;

[Authorize(Roles = Roles.Student)]
public class AssignmentDetailModel(ConfigService config, AssignmentService assignments, CourseService courseService, AppOptions options, IAntiforgery antiforgery) : PageModel
{
  public AssignmentDetailPageData PageData { get; private set; } = new();
  public string PageDataJson { get; private set; } = "{}";
  public string SchoolName { get; private set; } = options.SchoolName;
  public string SubmitUrlJson { get; private set; } = "\"\"";
  public string CsrfToken { get; private set; } = string.Empty;

  public async Task<IActionResult> OnGetAsync(string courseId, int year, string dueDate)
  {
    if (User.Identity?.IsAuthenticated != true)
    {
      return StatusCode(401);
    }

    if (!config.UsersByEmail.TryGetValue(User.GetEmail(), out var currentUser) || !User.IsInRole(Roles.Student))
    {
      return StatusCode(403);
    }

    if (string.IsNullOrWhiteSpace(courseId)
      || year < 1
      || !DateOnly.TryParseExact(dueDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedDueDate))
    {
      return NotFound();
    }

    var course = await courseService.TryGetCourseAsync(courseId);
    if (course is null || string.IsNullOrWhiteSpace(course.SubjectCode))
    {
      return NotFound();
    }

    var partitionKey = $"{year:D2}{course.SubjectCode}";
    var className = currentUser.Classes.FindMatchingClassName(partitionKey);
    if (className is null)
    {
      return NotFound();
    }

    PageData = await assignments.GetStudentAssignmentDetailAsync(currentUser, course, year, parsedDueDate, className);
    if (PageData is null)
    {
      return NotFound();
    }

    PageDataJson = JsonSerializer.Serialize(PageData, JsonDefaults.CamelCase);
    SubmitUrlJson = JsonSerializer.Serialize($"/assignments/{Uri.EscapeDataString(courseId)}/year-{year}/{parsedDueDate:yyyy-MM-dd}/submit", JsonDefaults.CamelCase);
    CsrfToken = antiforgery.GetAndStoreTokens(HttpContext).RequestToken ?? string.Empty;
    ViewData["Title"] = $"{PageData.CourseName} Assignment - {SchoolName}";
    return Page();
  }
}
