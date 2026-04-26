using Azure;
using Azure.Core;
using Azure.Storage.Blobs;
using Microsoft.Identity.Client;
using System.Text.Json;

namespace CurriculumPortal;

public sealed class ServiceAccountAuthService : IDisposable
{
  private const string AuthBlobName = "serviceaccount.json";
  private static readonly string[] MailScopes = ["Mail.Send"];
  private static readonly string[] AuthScopes = [.. MailScopes, "offline_access"];
  private static readonly string AuthScopeKey = string.Join(' ', AuthScopes);
  private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
  private readonly SemaphoreSlim _authSemaphore = new(1);
  private readonly BlobContainerClient _configClient;
  private readonly string _tenantId;
  private readonly string _clientId;
  private readonly string _clientSecret;

  public ServiceAccountAuthService(AppOptions options)
  {
    ArgumentNullException.ThrowIfNull(options);
    _tenantId = options.MicrosoftTenantId;
    _clientId = options.MicrosoftClientId;
    _clientSecret = options.MicrosoftClientSecret;
    _configClient = new BlobServiceClient(options.StorageAccountConnectionString).GetBlobContainerClient("config");
  }

  public async Task<ServiceAccountStatus> GetStatusAsync(Uri redirectUri, bool reauthenticate = false)
  {
    ArgumentNullException.ThrowIfNull(redirectUri);
    await _authSemaphore.WaitAsync();
    try
    {
      var authState = await LoadAuthStateAsync();
      var redirectUriText = redirectUri.AbsoluteUri;
      if (!reauthenticate && !string.IsNullOrWhiteSpace(authState.CacheBase64) && authState.Scopes == AuthScopeKey)
      {
        return CreateStatus(authState, "Service account is authenticated.");
      }

      if (reauthenticate || string.IsNullOrWhiteSpace(authState.AuthorizationUrl) || string.IsNullOrWhiteSpace(authState.State) ||
          authState.RedirectUri != redirectUriText || !authState.AuthorizationUrl.Contains("prompt=select_account", StringComparison.Ordinal))
      {
        authState.State = Guid.NewGuid().ToString("N");
        authState.RedirectUri = redirectUriText;
        authState.AuthorizationUrl = BuildAuthorizationUrl(authState.State, redirectUri);
        await PersistAuthStateAsync(authState);
      }

      return CreateStatus(authState, reauthenticate
        ? "Open the Microsoft sign-in page to refresh the service account."
        : "Open the Microsoft sign-in page to authenticate the service account.");
    }
    finally
    {
      _authSemaphore.Release();
    }
  }

  public async Task<ServiceAccountStatus> CompleteAuthorizationAsync(string code, string state, Uri redirectUri)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(code);
    ArgumentException.ThrowIfNullOrWhiteSpace(state);
    ArgumentNullException.ThrowIfNull(redirectUri);

    await _authSemaphore.WaitAsync();
    try
    {
      var authState = await LoadAuthStateAsync();
      if (string.IsNullOrWhiteSpace(authState.State) || authState.State != state)
      {
        throw new InvalidOperationException("The Microsoft sign-in state was invalid. Please restart the authentication process.");
      }

      authState.RedirectUri ??= redirectUri.AbsoluteUri;
      var app = CreateConfidentialClient(authState);
      var result = await app.AcquireTokenByAuthorizationCode(AuthScopes, code).ExecuteAsync();

      authState.AuthorizationUrl = null;
      authState.AccountIdentifier = result.Account?.HomeAccountId?.Identifier;
      authState.State = null;
      authState.Scopes = AuthScopeKey;
      authState.AuthenticatedUtc = DateTime.UtcNow;
      authState.RefreshTokenExpiresUtc = DateTime.UtcNow.AddDays(90);
      await PersistAuthStateAsync(authState);

      return CreateStatus(authState, "Service account authentication completed successfully.");
    }
    finally
    {
      _authSemaphore.Release();
    }
  }

  public async Task<AccessToken> GetMailAccessTokenAsync(CancellationToken cancellationToken = default)
  {
    await _authSemaphore.WaitAsync(cancellationToken);
    try
    {
      var authState = await LoadAuthStateAsync();
      if (string.IsNullOrWhiteSpace(authState.CacheBase64) || authState.Scopes != AuthScopeKey || string.IsNullOrWhiteSpace(authState.RedirectUri))
      {
        throw new InvalidOperationException("The Microsoft service account is not authenticated. Reauthenticate it at /serviceaccount.");
      }

      if (string.IsNullOrWhiteSpace(authState.AccountIdentifier))
      {
        throw new InvalidOperationException("The Microsoft service account token cache is missing its account identifier. Reauthenticate it at /serviceaccount.");
      }

      var app = CreateConfidentialClient(authState);
      var account = await app.GetAccountAsync(authState.AccountIdentifier)
        ?? throw new InvalidOperationException("The Microsoft service account token cache is empty. Reauthenticate it at /serviceaccount.");
      try
      {
        var result = await app.AcquireTokenSilent(MailScopes, account).ExecuteAsync(cancellationToken);
        return new AccessToken(result.AccessToken, result.ExpiresOn);
      }
      catch (MsalUiRequiredException ex)
      {
        throw new InvalidOperationException("The Microsoft service account needs to be reauthenticated at /serviceaccount.", ex);
      }
    }
    finally
    {
      _authSemaphore.Release();
    }
  }

  public async Task<DateTime?> GetRefreshTokenExpiryAsync()
  {
    await _authSemaphore.WaitAsync();
    try
    {
      var authState = await LoadAuthStateAsync();
      return authState.RefreshTokenExpiresUtc;
    }
    finally
    {
      _authSemaphore.Release();
    }
  }

  private IConfidentialClientApplication CreateConfidentialClient(ServiceAccountAuthState authState)
  {
    var app = ConfidentialClientApplicationBuilder.Create(_clientId)
      .WithAuthority(new Uri($"https://login.microsoftonline.com/{_tenantId}"))
      .WithClientSecret(_clientSecret)
      .WithRedirectUri(authState.RedirectUri)
      .Build();

    app.UserTokenCache.SetBeforeAccess(args =>
    {
      if (!string.IsNullOrWhiteSpace(authState.CacheBase64))
      {
        args.TokenCache.DeserializeMsalV3(Convert.FromBase64String(authState.CacheBase64), true);
      }
    });

    app.UserTokenCache.SetAfterAccess(args =>
    {
      if (!args.HasStateChanged) return;
      authState.CacheBase64 = Convert.ToBase64String(args.TokenCache.SerializeMsalV3());
      authState.RefreshTokenExpiresUtc = DateTime.UtcNow.AddDays(90);
      authState.AuthenticatedUtc ??= DateTime.UtcNow;
      PersistAuthStateAsync(authState).GetAwaiter().GetResult();
    });

    return app;
  }

  private async Task<ServiceAccountAuthState> LoadAuthStateAsync()
  {
    try
    {
      var response = await _configClient.GetBlobClient(AuthBlobName).DownloadContentAsync();
      var json = response.Value.Content.ToString();
      return JsonSerializer.Deserialize<ServiceAccountAuthState>(json, JsonOptions) ?? new();
    }
    catch (RequestFailedException ex) when (ex.Status == 404)
    {
      var authState = new ServiceAccountAuthState();
      await PersistAuthStateAsync(authState);
      return authState;
    }
  }

  private async Task PersistAuthStateAsync(ServiceAccountAuthState authState)
  {
    await _configClient.GetBlobClient(AuthBlobName).UploadAsync(BinaryData.FromString(JsonSerializer.Serialize(authState, JsonOptions)), true);
  }

  private static ServiceAccountStatus CreateStatus(ServiceAccountAuthState authState, string message)
    => new()
    {
      AuthorizationUrl = authState.AuthorizationUrl,
      IsAuthenticated = !string.IsNullOrWhiteSpace(authState.CacheBase64) && authState.Scopes == AuthScopeKey,
      Message = message,
      RefreshTokenExpiresUtc = authState.RefreshTokenExpiresUtc,
      ReauthenticateUrl = "/serviceaccount?reauth=true"
    };

  private string BuildAuthorizationUrl(string state, Uri redirectUri)
    => "https://login.microsoftonline.com/" + _tenantId + "/oauth2/v2.0/authorize" +
      "?client_id=" + Uri.EscapeDataString(_clientId) +
      "&response_type=code" +
      "&redirect_uri=" + Uri.EscapeDataString(redirectUri.AbsoluteUri) +
      "&response_mode=query" +
      "&scope=" + Uri.EscapeDataString(string.Join(' ', AuthScopes)) +
      "&prompt=select_account" +
      "&state=" + Uri.EscapeDataString(state);

  public void Dispose()
  {
    _authSemaphore.Dispose();
    GC.SuppressFinalize(this);
  }
}

public class ServiceAccountStatus
{
  public string AuthorizationUrl { get; init; }
  public bool HasError { get; init; }
  public bool IsAuthenticated { get; init; }
  public string Message { get; init; }
  public string ReauthenticateUrl { get; init; }
  public DateTime? RefreshTokenExpiresUtc { get; init; }
}

public class ServiceAccountAuthState
{
  public string AccountIdentifier { get; set; }
  public string AuthorizationUrl { get; set; }
  public string CacheBase64 { get; set; }
  public DateTime? AuthenticatedUtc { get; set; }
  public DateTime? RefreshTokenExpiresUtc { get; set; }
  public string RedirectUri { get; set; }
  public string Scopes { get; set; }
  public string State { get; set; }
}
