using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CurriculumPortal;

[Authorize(Roles = Roles.Admin)]
public class AuditModel(CourseService storage, ConfigService config) : PageModel
{
  public IReadOnlyList<FollowupDepartment> Departments { get; private set; } = [];

  public async Task OnGetAsync()
  {
    var courses = await storage.ListCoursesAsync(HttpContext.RequestAborted);
    var units = await storage.ListUnitsAsync(cancellationToken: HttpContext.RequestAborted);
    var unitsByCourse = units.ToLookup(o => o.PartitionKey);
    var departments = new Dictionary<string, FollowupDepartment>(StringComparer.OrdinalIgnoreCase);

    foreach (var course in courses)
    {
      var leaderEmail = course.LeadersList?.Select(o => o.Trim()).FirstOrDefault(o => !string.IsNullOrWhiteSpace(o));
      var departmentName = "No named leader";
      if (!string.IsNullOrWhiteSpace(leaderEmail))
      {
        config.UsersByEmail.TryGetValue(leaderEmail, out var leader);
        departmentName = leader?.DisplayName ?? leaderEmail;
      }

      if (!departments.TryGetValue(departmentName, out var department))
      {
        department = new FollowupDepartment { Name = departmentName };
        departments.Add(departmentName, department);
      }

      var courseUnits = unitsByCourse[course.RowKey].ToList();
      var evaluation = course.KeyStage == 3
        ? await storage.TryGetCourseEvaluationAsync(course.RowKey, HttpContext.RequestAborted)
        : null;
      department.Courses.Add(BuildCourseFollowup(course, courseUnits, evaluation, config.ChecklistItems));
    }

    Departments = departments.Values.OrderBy(o => o.Name).ToList();
  }

  private static FollowupCourse BuildCourseFollowup(
    CourseEntity course,
    List<UnitEntity> units,
    CourseEvaluation evaluation,
    IReadOnlyList<ChecklistItemConfig> checklistItems)
  {
    var followup = new FollowupCourse { Name = course.Name, HasUnits = units.Count > 0 };
    followup.Criteria.Add(new FollowupCriterion { Name = "'Why this?' and 'why now?' rationale" });
    followup.Criteria.Add(new FollowupCriterion { Name = "Linked scheme of work" });
    followup.Criteria.Add(new FollowupCriterion { Name = "Assessment and mark scheme" });

    if (course.KeyStage == 3)
      followup.Criteria.Add(new FollowupCriterion { Name = "Key knowledge defined" });

    foreach (var item in checklistItems)
      followup.Criteria.Add(new FollowupCriterion { Name = item.Title });
    if (course.KeyStage == 3 && evaluation is not null)
    {
      followup.Criteria.Add(new FollowupCriterion { Name = "Priority AI feedback addressed" });
      followup.Rows.Add(BuildOverviewRow(
        "Coverage and balance",
        followup.Criteria.Count,
        Clean(evaluation.Overall?.CoverageBalanceRecommendedActions).Count()));
      followup.Rows.Add(BuildOverviewRow(
        "Sequencing",
        followup.Criteria.Count,
        Clean(evaluation.Overall?.SequencingRecommendedActions).Count()));
      followup.Rows.Add(BuildOverviewRow(
        "Assessment recap",
        followup.Criteria.Count,
        Clean(evaluation.Overall?.AssessmentRecapRecommendedActions).Count()));
    }

    var unitEvaluations = evaluation is null
      ? new Dictionary<string, CourseEvaluationUnitResult>(StringComparer.Ordinal)
      : ResolveUnitEvaluations(units, evaluation);
    foreach (var unit in units)
    {
      unitEvaluations.TryGetValue(unit.RowKey, out var unitEvaluation);
      followup.Rows.Add(BuildUnitRow(unit, unitEvaluation, course.KeyStage == 3, evaluation is not null, checklistItems));
    }

    return followup;
  }

  private static FollowupRow BuildOverviewRow(string name, int criterionCount, int priorityActionCount)
  {
    return new FollowupRow
    {
      Name = name,
      Cells = Enumerable.Range(0, criterionCount - 1)
        .Select(_ => new FollowupCell { Applicable = false })
        .Append(new FollowupCell { Complete = priorityActionCount == 0, Important = true, Count = priorityActionCount })
        .ToList()
    };
  }

  private static FollowupRow BuildUnitRow(
    UnitEntity unit,
    CourseEvaluationUnitResult evaluation,
    bool includeKeyKnowledge,
    bool includeAi,
    IReadOnlyList<ChecklistItemConfig> checklistItems)
  {
    var row = new FollowupRow
    {
      Name = UnitName(unit),
      Cells =
      [
        new() { Complete = !string.IsNullOrWhiteSpace(unit.WhyThis) && !string.IsNullOrWhiteSpace(unit.WhyNow) },
        new() { Complete = !string.IsNullOrWhiteSpace(unit.SchemeUrl) },
        new()
        {
          Complete = unit.AssessmentStatus == 2
            || (!string.IsNullOrWhiteSpace(unit.AssessmentUrl) && !string.IsNullOrWhiteSpace(unit.MarkSchemeUrl))
        }
      ]
    };

    if (includeKeyKnowledge)
      row.Cells.Add(new FollowupCell { Complete = unit.KeyKnowledgeStatus == 2 });

    foreach (var item in checklistItems)
    {
      row.Cells.Add(new FollowupCell
      {
        Complete = IsChecklistItemCompleteOrExempt(unit.Checklist, item.Id),
        Important = string.Equals(item.Id, "assessedTasks", StringComparison.OrdinalIgnoreCase)
      });
    }

    if (includeAi)
    {
      var priorityActionCount = evaluation is null
        ? 0
        : Clean(evaluation.KeyKnowledge?.RecommendedActions).Count()
          + Clean(evaluation.Assessment?.AlignmentRecommendedActions).Count()
          + Clean(evaluation.Assessment?.DesignRecommendedActions).Count();
      row.Cells.Add(new FollowupCell { Complete = priorityActionCount == 0, Important = true, Count = priorityActionCount });
    }

    return row;
  }

  private static Dictionary<string, CourseEvaluationUnitResult> ResolveUnitEvaluations(List<UnitEntity> units, CourseEvaluation evaluation)
  {
    var currentUnitsById = units.ToDictionary(o => o.RowKey, StringComparer.Ordinal);
    var resolved = new Dictionary<string, (CourseEvaluationUnitResult Evaluation, bool IsLegacy)>(StringComparer.Ordinal);
    for (var index = 0; index < (evaluation.Units?.Count ?? 0); index++)
    {
      var unitEvaluation = evaluation.Units[index];
      var isLegacy = string.IsNullOrWhiteSpace(unitEvaluation.UnitId);
      var sourceUnitId = evaluation.Overall?.SourceUnitIds?.ElementAtOrDefault(index);
      var unitId = isLegacy
        ? !string.IsNullOrWhiteSpace(sourceUnitId) ? sourceUnitId : units.ElementAtOrDefault(index)?.RowKey
        : unitEvaluation.UnitId;
      if (string.IsNullOrWhiteSpace(unitId) || !currentUnitsById.ContainsKey(unitId))
        continue;

      if (!resolved.TryGetValue(unitId, out var existing) || (existing.IsLegacy && !isLegacy))
        resolved[unitId] = (unitEvaluation, isLegacy);
    }

    return resolved.ToDictionary(o => o.Key, o => o.Value.Evaluation, StringComparer.Ordinal);
  }

  private static IEnumerable<CourseEvaluationRecommendedAction> Clean(IEnumerable<CourseEvaluationRecommendedAction> actions)
  {
    return (actions ?? []).Where(o => o.Priority == 1 && !string.IsNullOrWhiteSpace(o.Action));
  }

  private static bool IsChecklistItemCompleteOrExempt(string checklist, string id)
  {
    if (string.IsNullOrWhiteSpace(checklist)) return false;

    foreach (var pair in checklist.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
      var parts = pair.Split(',', 2, StringSplitOptions.TrimEntries);
      if (parts.Length != 2 || !string.Equals(parts[0], id, StringComparison.OrdinalIgnoreCase))
        continue;

      return parts[1] is "1" or "2";
    }

    return false;
  }

  private static string UnitName(UnitEntity unit) => $"Year {unit.YearGroup} – {unit.Title}";

}

public sealed class FollowupDepartment
{
  public string Name { get; set; } = string.Empty;
  public List<FollowupCourse> Courses { get; set; } = [];
}

public sealed class FollowupCourse
{
  public string Name { get; set; } = string.Empty;
  public bool HasUnits { get; set; }
  public List<FollowupCriterion> Criteria { get; set; } = [];
  public List<FollowupRow> Rows { get; set; } = [];
}

public sealed class FollowupCriterion
{
  public string Name { get; set; } = string.Empty;
}

public sealed class FollowupRow
{
  public string Name { get; set; } = string.Empty;
  public List<FollowupCell> Cells { get; set; } = [];
}

public sealed class FollowupCell
{
  public bool Applicable { get; set; } = true;
  public bool Complete { get; set; }
  public bool Important { get; set; } = true;
  public int Count { get; set; }
}
