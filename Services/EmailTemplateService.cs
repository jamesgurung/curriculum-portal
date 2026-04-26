using Microsoft.AspNetCore.Mvc;

namespace CurriculumPortal;

public class EmailTemplateService(AppOptions options, IRazorViewRenderer razorViewRenderer)
{
  public const string LogoContentId = "school-logo";
  public static string Font { get; } = "system-ui,-apple-system,'Segoe UI','Liberation Sans',Arial,sans-serif";
  private const string CompletionEmailViewPath = "/Pages/Emails/CompletionEmail.cshtml";

  public Task<string> BuildTutorCompletionEmailAsync(ActionContext actionContext, TutorCompletionReport model)
  {
    ArgumentNullException.ThrowIfNull(model);

    return razorViewRenderer.RenderAsync(actionContext, CompletionEmailViewPath, new CompletionEmailViewModel
    {
      SchoolName = options.SchoolName,
      LogoContentId = LogoContentId,
      Title = "Tutor group completion",
      DueDateLabel = model.DueDateLabel,
      Preheader = $"{model.TutorGroup} is {model.CompletionPercentage}% complete for assignments due {model.DueDateLabel}.",
      Summary = $"{model.TutorGroup} is {model.CompletionPercentage}% complete ({model.CompletedQuestions}/{model.TotalQuestions} questions).",
      Sections =
      [
        new CompletionEmailSection
        {
          Title = "Students",
          Students = model.Students
        }
      ],
      TutorGroups = model.TutorGroups
    });
  }

  public Task<string> BuildTeacherCompletionEmailAsync(ActionContext actionContext, TeacherCompletionReport model)
  {
    ArgumentNullException.ThrowIfNull(model);

    return razorViewRenderer.RenderAsync(actionContext, CompletionEmailViewPath, new CompletionEmailViewModel
    {
      SchoolName = options.SchoolName,
      LogoContentId = LogoContentId,
      Title = "Class completion",
      DueDateLabel = model.DueDateLabel,
      Preheader = $"{model.Classes.Count} class completion report(s) for assignments due {model.DueDateLabel}.",
      Sections = model.Classes.Select(o => new CompletionEmailSection
      {
        Title = o.ClassName,
        Summary = $"{o.CourseName}: {o.CompletionPercentage}% complete ({o.CompletedQuestions}/{o.TotalQuestions} questions).",
        Students = o.Students
      }).ToList()
    });
  }

  public EmailAttachment CreateLogoAttachment(byte[] logoBytes)
    => new()
    {
      Name = "school-logo.png",
      ContentType = "image/png",
      ContentBytes = logoBytes,
      ContentId = LogoContentId,
      IsInline = true
    };
}

public class CompletionEmailViewModel
{
  public string SchoolName { get; set; } = string.Empty;
  public string LogoContentId { get; set; } = string.Empty;
  public string Title { get; set; } = string.Empty;
  public string DueDateLabel { get; set; } = string.Empty;
  public string Preheader { get; set; } = string.Empty;
  public string Summary { get; set; } = string.Empty;
  public List<CompletionEmailSection> Sections { get; set; } = [];
  public List<TutorGroupCompletionRow> TutorGroups { get; set; } = [];
}

public class CompletionEmailSection
{
  public string Title { get; set; } = string.Empty;
  public string Summary { get; set; } = string.Empty;
  public List<CompletionStudentRow> Students { get; set; } = [];
}
