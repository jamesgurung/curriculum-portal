using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CurriculumPortal;

[Authorize(Roles = Roles.Admin)]
public class ServiceAccountModel(ServiceAccountAuthService serviceAccountAuthService) : PageModel
{
  public ServiceAccountStatus Status { get; private set; }

  public async Task OnGetAsync(string code, string state, string error, bool reauth = false)
  {
    var redirectUri = new Uri($"{Request.Scheme}://{Request.Host}/serviceaccount");
    if (!string.IsNullOrWhiteSpace(error))
    {
      Status = new ServiceAccountStatus
      {
        HasError = true,
        Message = "Microsoft sign-in was cancelled or failed. Please try again.",
        ReauthenticateUrl = "/serviceaccount?reauth=true"
      };
      return;
    }

    try
    {
      Status = !string.IsNullOrWhiteSpace(code)
        ? await serviceAccountAuthService.CompleteAuthorizationAsync(code, state, redirectUri)
        : await serviceAccountAuthService.GetStatusAsync(redirectUri, reauth);
    }
    catch (InvalidOperationException ex)
    {
      Status = new ServiceAccountStatus
      {
        HasError = true,
        Message = ex.Message,
        ReauthenticateUrl = "/serviceaccount?reauth=true"
      };
    }
  }
}
