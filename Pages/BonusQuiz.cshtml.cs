using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System.Text.Json;

namespace CurriculumPortal;

[Authorize(Roles = Roles.Student)]
public class BonusQuizModel(ConfigService config, BonusQuizService bonusQuizService, AppOptions options, IAntiforgery antiforgery) : PageModel
{
  public BonusQuizPageData PageData { get; private set; } = new();
  public string PageDataJson { get; private set; } = "{}";
  public string SchoolName { get; private set; } = options.SchoolName;
  public string CsrfToken { get; private set; } = string.Empty;

  public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
  {
    if (User.Identity?.IsAuthenticated != true) return StatusCode(401);
    if (!config.UsersByEmail.TryGetValue(User.GetEmail(), out var currentUser)) return StatusCode(403);

    PageData = await bonusQuizService.GetPageAsync(currentUser, DateTimeOffset.UtcNow, cancellationToken);
    if (PageData is null) return NotFound();

    PageDataJson = JsonSerializer.Serialize(PageData, JsonDefaults.CamelCase);
    CsrfToken = antiforgery.GetAndStoreTokens(HttpContext).RequestToken ?? string.Empty;
    ViewData["Title"] = $"Bonus Quiz - {SchoolName}";
    return Page();
  }
}
