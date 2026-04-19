using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System.Text.Json;

namespace CurriculumPortal;

[Authorize]
public class AssignmentsModel(ConfigService config, AssignmentService assignments, AppOptions options) : PageModel
{
  public string PageDataJson { get; private set; } = "{}";
  public string SchoolName { get; private set; } = options.SchoolName;

  public async Task<IActionResult> OnGetAsync()
  {
    if (!config.UsersByEmail.TryGetValue(User.GetEmail(), out var currentUser))
    {
      return StatusCode(403);
    }

    if (User.IsInRole(Roles.Teacher))
    {
      PageDataJson = JsonSerializer.Serialize(new AssignmentsPageData
      {
        IsStaff = true,
        Staff = await assignments.GetStaffAssignmentsAsync(currentUser)
      }, JsonDefaults.CamelCase);
      ViewData["Title"] = $"Assignments - {SchoolName}";
      return Page();
    }

    if (User.IsInRole(Roles.Student))
    {
      PageDataJson = JsonSerializer.Serialize(new AssignmentsPageData
      {
        IsStaff = false,
        Student = await assignments.GetStudentAssignmentsAsync(currentUser)
      }, JsonDefaults.CamelCase);
      ViewData["Title"] = $"Assignments - {SchoolName}";
      return Page();
    }

    return StatusCode(403);
  }
}
