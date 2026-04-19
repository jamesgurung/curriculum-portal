using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System.Text.Json;

namespace CurriculumPortal;

[Authorize(Roles = Roles.Teacher)]
public class BuildModel(CourseService storage, IAntiforgery antiforgery) : PageModel
{
  public string PageTitle { get; private set; } = string.Empty;
  public string CourseIdJson { get; private set; } = "\"\"";
  public string UnitIdJson { get; private set; } = "\"\"";
  public string UnitJson { get; private set; } = "{}";
  public string KeyKnowledgeJson { get; private set; } = "{}";
  public string AssessmentJson { get; private set; } = "{}";
  public string QuestionBankJson { get; private set; } = "{}";
  public string IsEditorJson { get; private set; } = "false";
  public string IsAdminJson { get; private set; } = "false";
  public string KeyKnowledgeCompleteJson { get; private set; } = "false";
  public string AssessmentCompleteJson { get; private set; } = "false";
  public string QuizCompleteJson { get; private set; } = "false";
  public string CourseId { get; private set; } = string.Empty;
  public string CourseTitle { get; private set; } = string.Empty;
  public string UnitTitle { get; private set; } = string.Empty;
  public bool IsEditor { get; private set; }
  public string CsrfToken { get; private set; } = string.Empty;

  public async Task<IActionResult> OnGetAsync(string courseId, string unitId)
  {
    if (string.IsNullOrWhiteSpace(courseId) || string.IsNullOrWhiteSpace(unitId))
    {
      return BadRequest("Course ID and unit ID are required.");
    }

    var course = await storage.TryGetCourseAsync(courseId);
    var unit = await storage.TryGetUnitAsync(courseId, unitId);
    if (course is null || unit is null)
    {
      return NotFound("Assessment not found.");
    }

    var keyKnowledge = await storage.GetBlobAsync<KeyKnowledge>(unitId);
    var assessment = await storage.GetBlobAsync<Assessment>(unitId);
    var questionBank = await storage.GetBlobAsync<QuestionBank>(unitId);

    var editable = User.CanEditCourse(course);

    CourseId = courseId;
    CourseTitle = BuildCourseTitle(course, unit);
    UnitTitle = unit.Title;
    PageTitle = $"{unit.Title} - {CourseTitle}";
    IsEditor = editable;
    CsrfToken = antiforgery.GetAndStoreTokens(HttpContext).RequestToken ?? string.Empty;

    CourseIdJson = JsonSerializer.Serialize(courseId, JsonDefaults.CamelCase);
    UnitIdJson = JsonSerializer.Serialize(unitId, JsonDefaults.CamelCase);
    UnitJson = JsonSerializer.Serialize(unit, JsonDefaults.CamelCase);
    KeyKnowledgeJson = JsonSerializer.Serialize(keyKnowledge, JsonDefaults.CamelCase);
    AssessmentJson = JsonSerializer.Serialize(assessment, JsonDefaults.CamelCase);
    QuestionBankJson = JsonSerializer.Serialize(questionBank, JsonDefaults.CamelCase);
    IsEditorJson = JsonSerializer.Serialize(editable, JsonDefaults.CamelCase);
    IsAdminJson = JsonSerializer.Serialize(User.IsInRole(Roles.Admin), JsonDefaults.CamelCase);
    KeyKnowledgeCompleteJson = JsonSerializer.Serialize(unit.KeyKnowledgeStatus == 2, JsonDefaults.CamelCase);
    AssessmentCompleteJson = JsonSerializer.Serialize(unit.AssessmentStatus == 2, JsonDefaults.CamelCase);
    QuizCompleteJson = JsonSerializer.Serialize(unit.RevisionQuizStatus == 2, JsonDefaults.CamelCase);

    return Page();
  }

  private static string BuildCourseTitle(CourseEntity course, UnitEntity unit)
  {
    var parts = course.Name.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
    var suffix = parts.Length == 2 ? parts[1] : course.Name;
    return $"Year {unit.YearGroup} {suffix}";
  }
}

