using Microsoft.AspNetCore.Mvc;

namespace CurriculumPortal;

public class EmailTemplateService(AppOptions options, IRazorViewRenderer razorViewRenderer)
{
  public static string Font { get; } = "Arial,'Liberation Sans',sans-serif";
  private const string CompletionEmailViewPath = "/Pages/Emails/CompletionEmail.cshtml";
  private const string CompletionEmailPreheader = "Please speak with your students to promote quiz completion.";

  public static string GetTutorCompletionTitle(TutorCompletionReport model)
  {
    ArgumentNullException.ThrowIfNull(model);
    return $"{model.TutorGroup} Completed {model.CompletionPercentage}% of Quizzes";
  }

  public static string GetTeacherCompletionTitle(TeacherCompletionReport model)
  {
    ArgumentNullException.ThrowIfNull(model);
    return $"Your Classes Completed {GetCompletionPercentage(model.Classes.Sum(o => o.CompletedQuestions), model.Classes.Sum(o => o.TotalQuestions))}% of Quizzes";
  }

  public Task<string> BuildTutorCompletionEmailAsync(ActionContext actionContext, TutorCompletionReport model)
  {
    ArgumentNullException.ThrowIfNull(model);
    var title = GetTutorCompletionTitle(model);

    return razorViewRenderer.RenderAsync(actionContext, CompletionEmailViewPath, new CompletionEmailViewModel
    {
      Title = title,
      DueDateLabel = model.DueDateLabel,
      Preheader = CompletionEmailPreheader,
      AssignmentsUrl = BuildAbsoluteUrl(options.Website, "/assignments"),
      LeaderboardTitle = $"Year {GetYearGroup(model.TutorGroup)} Leaderboard",
      Sections =
      [
        new CompletionEmailSection
        {
          Title = "Students",
          Students = OrderStudents(model.Students)
        }
      ],
      TutorGroups = model.TutorGroups
    });
  }

  public Task<string> BuildTeacherCompletionEmailAsync(ActionContext actionContext, TeacherCompletionReport model)
  {
    ArgumentNullException.ThrowIfNull(model);
    var title = GetTeacherCompletionTitle(model);

    return razorViewRenderer.RenderAsync(actionContext, CompletionEmailViewPath, new CompletionEmailViewModel
    {
      Title = title,
      DueDateLabel = model.DueDateLabel,
      Preheader = CompletionEmailPreheader,
      AssignmentsUrl = BuildAbsoluteUrl(options.Website, "/assignments"),
      ClassOverview = model.Classes
        .OrderByDescending(o => o.CompletionPercentage)
        .ThenBy(o => o.ClassName, StringComparer.OrdinalIgnoreCase)
        .ToList(),
      Sections = model.Classes
      .OrderByDescending(o => o.CompletionPercentage)
      .ThenBy(o => o.ClassName, StringComparer.OrdinalIgnoreCase)
      .Select(o => new CompletionEmailSection
      {
        Title = $"{o.ClassName} – {o.CompletionPercentage}%",
        CompletionPercentage = o.CompletionPercentage,
        Students = OrderStudents(o.Students)
      })
      .ToList()
    });
  }

  private static int GetCompletionPercentage(int completed, int total)
  {
    return total <= 0 ? 0 : (int)Math.Round(completed * 100d / total);
  }

  private static string BuildAbsoluteUrl(string website, string path)
  {
    var baseUri = new Uri(website.TrimEnd('/') + "/");
    return new Uri(baseUri, path.TrimStart('/')).AbsoluteUri;
  }

  private static int GetYearGroup(string tutorGroup)
  {
    var digits = new string((tutorGroup ?? string.Empty).TakeWhile(char.IsDigit).ToArray());
    return int.TryParse(digits, out var yearGroup) ? yearGroup : 0;
  }

  private static List<CompletionStudentRow> OrderStudents(IEnumerable<CompletionStudentRow> students)
    => students
      .OrderByDescending(o => o.CompletionPercentage)
      .ThenBy(o => o.LastName, StringComparer.OrdinalIgnoreCase)
      .ThenBy(o => o.FirstName, StringComparer.OrdinalIgnoreCase)
      .ToList();
}

public class CompletionEmailViewModel
{
  public string Title { get; set; } = string.Empty;
  public string DueDateLabel { get; set; } = string.Empty;
  public string Preheader { get; set; } = string.Empty;
  public string AssignmentsUrl { get; set; } = string.Empty;
  public string LeaderboardTitle { get; set; } = string.Empty;
  public string Summary { get; set; } = string.Empty;
  public List<ClassCompletionReport> ClassOverview { get; set; } = [];
  public List<CompletionEmailSection> Sections { get; set; } = [];
  public List<TutorGroupCompletionRow> TutorGroups { get; set; } = [];
}

public class CompletionEmailSection
{
  public string Title { get; set; } = string.Empty;
  public string Summary { get; set; } = string.Empty;
  public int CompletionPercentage { get; set; }
  public List<CompletionStudentRow> Students { get; set; } = [];
}
