using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CurriculumPortal;

[Authorize(Roles = Roles.Admin + "," + Roles.Teacher)]
public class LeaderboardModel(XpService xpService, AppOptions options) : PageModel
{
  public IReadOnlyList<XpLeaderboardEntry> Entries { get; private set; } = [];

  public async Task OnGetAsync()
  {
    ViewData["Title"] = $"Leaderboard - {options.SchoolName}";
    Entries = await xpService.GetLeaderboardAsync(HttpContext.RequestAborted);
  }
}
