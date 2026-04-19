namespace CurriculumPortal;

public sealed class AppOptions
{
  public string[] AdminEmails { get; set; }
  public int AssignmentCompletionHighThreshold { get; set; }
  public int AssignmentCompletionLowThreshold { get; set; }
  public string ClassChartsEmail { get; set; }
  public string ClassChartsPassword { get; set; }
  public string DataControllerName { get; set; }
  public string DataProtectionBlobUri { get; set; }
  public string MicrosoftClientId { get; set; }
  public string MicrosoftClientSecret { get; set; }
  public string MicrosoftFoundryEndpoint { get; set; }
  public string MicrosoftSharePointSubdomain { get; set; }
  public string MicrosoftTeamsPrefix { get; set; }
  public string MicrosoftTenantId { get; set; }
  public string OpenAIApiKey { get; set; }
  public string OpenAIModel { get; set; }
  public string PrivacyNoticeUrl { get; set; }
  public string SchoolName { get; set; }
  public string StorageAccountConnectionString { get; set; }
  public string SyncApiKey { get; set; }
  public string Website { get; set; }

  public void Validate()
  {
    EnsureValue(DataControllerName, nameof(DataControllerName));
    EnsureValue(DataProtectionBlobUri, nameof(DataProtectionBlobUri));
    EnsureValue(MicrosoftClientId, nameof(MicrosoftClientId));
    EnsureValue(MicrosoftClientSecret, nameof(MicrosoftClientSecret));
    EnsureValue(MicrosoftSharePointSubdomain, nameof(MicrosoftSharePointSubdomain));
    EnsureValue(MicrosoftTeamsPrefix, nameof(MicrosoftTeamsPrefix));
    EnsureValue(MicrosoftTenantId, nameof(MicrosoftTenantId));
    EnsureValue(OpenAIApiKey, nameof(OpenAIApiKey));
    EnsureValue(OpenAIModel, nameof(OpenAIModel));
    EnsureValue(PrivacyNoticeUrl, nameof(PrivacyNoticeUrl));
    EnsureValue(SchoolName, nameof(SchoolName));
    EnsureValue(StorageAccountConnectionString, nameof(StorageAccountConnectionString));
    EnsureValue(Website, nameof(Website));

    if (!Uri.TryCreate(DataProtectionBlobUri, UriKind.Absolute, out _))
      throw new InvalidOperationException($"{nameof(DataProtectionBlobUri)} is not a valid absolute URI.");

    if (!Uri.TryCreate(PrivacyNoticeUrl, UriKind.Absolute, out _))
      throw new InvalidOperationException($"{nameof(PrivacyNoticeUrl)} is not a valid absolute URI.");

    if (!string.IsNullOrWhiteSpace(MicrosoftFoundryEndpoint) && !Uri.TryCreate(MicrosoftFoundryEndpoint, UriKind.Absolute, out _))
      throw new InvalidOperationException($"{nameof(MicrosoftFoundryEndpoint)} is not a valid absolute URI.");

    if (AdminEmails is null || AdminEmails.Length == 0 || AdminEmails.Any(email => string.IsNullOrWhiteSpace(email)))
      throw new InvalidOperationException($"{nameof(AdminEmails)} must contain at least one valid email address.");
    if (AssignmentCompletionLowThreshold > AssignmentCompletionHighThreshold)
      throw new InvalidOperationException($"{nameof(AssignmentCompletionLowThreshold)} must be less than or equal to {nameof(AssignmentCompletionHighThreshold)}.");
  }

  private static void EnsureValue(string value, string keyName)
  {
    if (string.IsNullOrWhiteSpace(value))
      throw new InvalidOperationException($"{keyName} is not configured.");
  }
}
