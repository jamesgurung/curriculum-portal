using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CurriculumPortal;

[Authorize(Roles = Roles.Admin)]
public class CourseEvaluationsModel(CourseService courseService) : PageModel
{
  public IReadOnlyList<CourseEvaluationReport> Reports { get; private set; } = [];

  public async Task OnGetAsync()
  {
    var cancellationToken = HttpContext.RequestAborted;
    var coursesTask = courseService.ListCoursesAsync(cancellationToken);
    var unitsTask = courseService.ListUnitsAsync(cancellationToken: cancellationToken);
    await Task.WhenAll(coursesTask, unitsTask);

    var courses = await coursesTask;
    var unitsByCourse = (await unitsTask).ToLookup(o => o.PartitionKey);
    var reports = await Task.WhenAll(courses.Select(async course => new CourseEvaluationReport
    {
      CourseId = course.RowKey,
      Course = course,
      Evaluation = await courseService.TryGetCourseEvaluationAsync(course.RowKey, cancellationToken),
      Units = unitsByCourse[course.RowKey].ToList(),
      MaximumPriority = 2
    }));

    Reports = reports.Where(o => o.Evaluation is not null).ToList();
  }
}
