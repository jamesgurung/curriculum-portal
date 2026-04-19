using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System.Globalization;

namespace CurriculumPortal;

[Authorize(Roles = Roles.Teacher)]
public class RecordSheetsModel(CourseService storage, ConfigService config) : PageModel
{
  public RecordSheets Data { get; private set; } = new();

  public async Task<IActionResult> OnGetAsync(string courseId, string unitId)
  {
    var unit = await storage.TryGetUnitAsync(courseId, unitId);
    if (unit is null)
    {
      return NotFound("Unit not found.");
    }

    Data.UnitTitle = unit.Title;
    var assessment = await storage.GetBlobAsync<Assessment>(unitId);
    if (assessment.Sections.Count == 0)
    {
      return NotFound("No assessment found for this unit.");
    }

    var applicationSection = assessment.Sections.FirstOrDefault(o => o.Title.Equals("Application", StringComparison.OrdinalIgnoreCase));
    if (applicationSection is null)
    {
      return NotFound("No 'Application' section found in the assessment.");
    }

    Data.Elements = applicationSection.Questions.Select(o => new RecordSheetElement { Title = o.Question, MaxMarks = o.Marks }).ToList();
    Data.Elements.Add(new RecordSheetElement
    {
      Title = "Knowledge Quiz",
      MaxMarks = assessment.Sections.Where(o => !o.Title.Equals("Application", StringComparison.OrdinalIgnoreCase)).SelectMany(o => o.Questions).Sum(o => o.Marks)
    });

    var yearString = unit.YearGroup.ToString(CultureInfo.InvariantCulture);
    var students = config.Students
      .Where(o => o.TutorGroup.StartsWith(yearString, StringComparison.Ordinal))
      .ToLookup(o => o.Classes.FirstOrDefault(c => c.Contains("/Wa", StringComparison.Ordinal)), o => $"{o.LastName}, {o.FirstName}");

    Data.Classes = students.Where(o => !string.IsNullOrWhiteSpace(o.Key)).OrderBy(o => o.Key)
      .Select(o => new RecordSheetClass { Name = o.Key, Students = o.Order().ToList() })
      .ToList();

    return Page();
  }
}
