using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using System.Net;
using System.Security.Claims;

namespace CurriculumPortal;

public static class AuthConfig
{
  public static void ConfigureAuth(this WebApplicationBuilder builder, ConfigService config)
  {
    ArgumentNullException.ThrowIfNull(builder);
    builder.Services
      .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
      .AddCookie(options =>
      {
        options.Cookie.Path = "/";
        options.Cookie.HttpOnly = true;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.LoginPath = "/login";
        options.LogoutPath = "/logout";
        options.AccessDeniedPath = "/denied";
        options.ExpireTimeSpan = TimeSpan.FromDays(60);
        options.SlidingExpiration = true;
        options.ReturnUrlParameter = "path";
        options.Events = new CookieAuthenticationEvents
        {
          OnRedirectToAccessDenied = context =>
          {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
          },
          OnValidatePrincipal = async context =>
          {
            var issued = context.Properties.IssuedUtc;
            if (issued.HasValue && issued.Value > DateTimeOffset.UtcNow.AddDays(-1))
            {
              return;
            }
            var email = context.Principal.GetEmail();
            if (TryCreatePrincipal(email, config, out var principal))
            {
              context.ReplacePrincipal(principal);
              context.ShouldRenew = true;
            }
            else
            {
              context.RejectPrincipal();
              await context.HttpContext.SignOutAsync();
            }
          }
        };
      })
      .AddOpenIdConnect("Microsoft", options =>
      {
        options.Authority = $"https://login.microsoftonline.com/{builder.Configuration["MicrosoftTenantId"]}/v2.0/";
        options.ClientId = builder.Configuration["MicrosoftClientId"];
        options.ClientSecret = builder.Configuration["MicrosoftClientSecret"];
        options.ResponseType = OpenIdConnectResponseType.Code;
        options.MapInboundClaims = false;
        options.Scope.Clear();
        options.Scope.Add("openid");
        options.Scope.Add("profile");
        options.Events = new OpenIdConnectEvents
        {
          OnTicketReceived = context =>
          {
            var email = context.Principal.FindFirstValue("upn")?.ToLowerInvariant();
            if (TryCreatePrincipal(email, config, out var principal))
            {
              context.Principal = principal;
            }
            else
            {
              context.Fail("Unauthorised");
              context.Response.Redirect("/denied");
              context.HandleResponse();
            }
            return Task.CompletedTask;
          }
        };
      });

    builder.Services.AddAuthorizationBuilder().SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
  }

  private static bool TryCreatePrincipal(string email, ConfigService config, out ClaimsPrincipal principal)
  {
    if (email is null || !config.UsersByEmail.TryGetValue(email, out var user))
    {
      principal = null;
      return false;
    }
    var identity = new ClaimsIdentity(CookieAuthenticationDefaults.AuthenticationScheme);
    identity.AddClaim(new Claim(ClaimTypes.Name, user.Email));
    identity.AddClaim(new Claim(ClaimTypes.GivenName, user.DisplayName));

    identity.AddClaim(new Claim(ClaimTypes.Role, user.IsTeacher ? Roles.Teacher : Roles.Student));
    if (user.IsAdmin) identity.AddClaim(new Claim(ClaimTypes.Role, Roles.Admin));

    principal = new ClaimsPrincipal(identity);
    return true;
  }

  private static readonly string[] authenticationSchemes = ["Microsoft"];

  public static void MapAuthPaths(this WebApplication app)
  {
    app.MapGet("/", [AllowAnonymous] (HttpContext context) =>
    {
      var destination = context.User.Identity?.IsAuthenticated == true && context.User.IsInRole(Roles.Student)
        ? "/assignments"
        : "/courses";

      return Results.Redirect(destination);
    });

    app.MapGet("/login", [AllowAnonymous] ([FromQuery] string path) =>
    {
      var redirectUri = string.IsNullOrWhiteSpace(path) ? "/" : WebUtility.UrlDecode(path);
      var authProperties = new AuthenticationProperties
      {
        RedirectUri = redirectUri,
        AllowRefresh = true,
        IsPersistent = true
      };
      return Results.Challenge(authProperties, authenticationSchemes);
    });

    app.MapGet("/impersonate/{email}", [Authorize(Roles = Roles.Admin)] async (HttpContext context, string email, ConfigService config) =>
    {
      if (!TryCreatePrincipal(email?.Trim().ToLowerInvariant(), config, out var principal))
      {
        return Results.NotFound("User not found.");
      }

      await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);
      return Results.Redirect("/");
    });

    app.MapGet("/logout", [Authorize] async (HttpContext context) =>
    {
      await context.SignOutAsync();
      return Results.Redirect("/");
    });
  }
}
