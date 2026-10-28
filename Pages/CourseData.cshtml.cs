using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CurriculumPortal;

[Authorize(Roles = Roles.Admin + "," + Roles.Teacher)]
public class CourseDataModel(CourseService storage, ConfigService config, BromcomAssessmentService assessments) : PageModel
{
  public IReadOnlyList<CourseDataDepartment> Departments { get; private set; } = [];

  public async Task<IActionResult> OnGetAsync()
  {
    var terms = new[] { "Autumn", "Spring", "Summer" };
    var courses = (await storage.ListCoursesAsync(HttpContext.RequestAborted))
      .Where(course => User.CanEditCourse(course, config)).ToList();
    if (courses.Count == 0 && !User.IsInRole(Roles.Admin) && !config.SeniorLeaders.Contains(User.GetEmail()))
      return Forbid();

    var units = await storage.ListUnitsAsync(cancellationToken: HttpContext.RequestAborted);
    var unitsByCourse = units
      .Select(unit => new { Unit = unit, TermIndex = string.IsNullOrWhiteSpace(unit.Term) ? terms.Length : Array.FindIndex(terms, value => string.Equals(value, unit.Term, StringComparison.OrdinalIgnoreCase)) })
      .Where(item => item.TermIndex >= 0)
      .OrderBy(item => item.TermIndex)
      .ThenBy(item => item.Unit.Order)
      .ThenBy(item => item.Unit.Title)
      .ToLookup(item => item.Unit.PartitionKey, item => item.Unit);
    var classes = config.Students
      .SelectMany(student => student.Classes ?? [])
      .Select(className => ClassNameParser.TryParseSubjectClass(className, out var parsed) ? parsed : null)
      .Where(cls => cls is not null)
      .DistinctBy(cls => cls.Name, StringComparer.OrdinalIgnoreCase)
      .OrderBy(cls => cls.Name, StringComparer.OrdinalIgnoreCase)
      .ToList();
    var departments = new Dictionary<string, CourseDataDepartment>(StringComparer.OrdinalIgnoreCase);

    foreach (var course in courses)
    {
      var leaderEmail = course.LeadersList?.Select(value => value.Trim()).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
      var departmentName = "No named leader";
      if (!string.IsNullOrWhiteSpace(leaderEmail))
      {
        config.UsersByEmail.TryGetValue(leaderEmail, out var leader);
        departmentName = leader?.DisplayName ?? leaderEmail;
      }

      if (!departments.TryGetValue(departmentName, out var department))
      {
        department = new CourseDataDepartment { Name = departmentName };
        departments.Add(departmentName, department);
      }

      var report = new CourseDataCourse { Name = course.Name };
      var years = course.SubjectCode == "Rg" ? Enumerable.Range(7, 2)
        : course.KeyStage == 3 ? Enumerable.Range(7, 3)
        : Enumerable.Range(course.KeyStage == 4 ? 10 : 12, 2);
      foreach (var year in years)
      {
        var yearClasses = classes.Where(cls => cls.YearGroup == year && string.Equals(cls.SubjectCode, course.SubjectCode, StringComparison.OrdinalIgnoreCase)).ToList();
        if (yearClasses.Count == 0)
          continue;

        var yearUnits = unitsByCourse[course.RowKey].Where(unit => unit.YearGroup == year).ToList();
        if (course.RowKey == "gcse-mathematics")
        {
          var prefixes = new[] { "Higher - ", "Foundation Plus - ", "Foundation - " };
          foreach (var unit in yearUnits)
          {
            var prefix = prefixes.FirstOrDefault(value => unit.Title?.StartsWith(value, StringComparison.OrdinalIgnoreCase) == true);
            if (prefix is not null)
              unit.Title = unit.Title[prefix.Length..].Trim();
          }

          yearUnits = yearUnits.DistinctBy(unit => unit.Title, StringComparer.OrdinalIgnoreCase).ToList();
        }

        var statuses = new List<Dictionary<string, CourseDataStatus>>();
        foreach (var unit in yearUnits)
        {
          var progress = assessments.GetCompletionProgress(course, unit);
          statuses.Add((progress?.EnteredClasses ?? [])
            .Select(cls => KeyValuePair.Create(cls.Name, CourseDataStatus.Entered))
            .Concat((progress?.NotEnteredClasses ?? []).Select(cls => KeyValuePair.Create(cls.Name, CourseDataStatus.NotEntered)))
            .ToDictionary(item => item.Key, item => item.Value, StringComparer.OrdinalIgnoreCase));
        }

        report.YearGroups.Add(new CourseDataYearGroup
        {
          Year = year,
          Units = yearUnits,
          Rows = yearClasses
            .Select(cls => new CourseDataRow
            {
              Name = cls.Name,
              Cells = statuses.Select(status => status.GetValueOrDefault(cls.Name, CourseDataStatus.Unavailable)).ToList()
            }).ToList()
        });
      }

      if (report.YearGroups.Count > 0)
        department.Courses.Add(report);
    }

    Departments = departments.Values.Where(department => department.Courses.Count > 0).OrderBy(department => department.Name).ToList();
    return Page();
  }
}

public sealed class CourseDataDepartment
{
  public string Name { get; set; }
  public List<CourseDataCourse> Courses { get; set; } = [];
}

public sealed class CourseDataCourse
{
  public string Name { get; set; }
  public List<CourseDataYearGroup> YearGroups { get; set; } = [];
}

public sealed class CourseDataYearGroup
{
  public int Year { get; set; }
  public List<UnitEntity> Units { get; set; } = [];
  public List<CourseDataRow> Rows { get; set; } = [];
}

public sealed class CourseDataRow
{
  public string Name { get; set; }
  public List<CourseDataStatus> Cells { get; set; } = [];
}

public enum CourseDataStatus
{
  Unavailable,
  Entered,
  NotEntered
}
