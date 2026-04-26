using Azure.Core;
using Microsoft.Graph.Beta;
using Microsoft.Graph.Beta.Me.SendMail;
using Microsoft.Graph.Beta.Models;

namespace CurriculumPortal;

public class MailService(ServiceAccountAuthService authService)
{
  private static readonly SemaphoreSlim Semaphore = new(1);

  public async Task SendAsync(List<Email> emails, CancellationToken cancellationToken = default)
  {
    ArgumentNullException.ThrowIfNull(emails);
    await Semaphore.WaitAsync(cancellationToken);
    try
    {
      using var client = new GraphServiceClient(new ServiceAccountMailCredential(authService), ["Mail.Send"]);
      var exceptions = new List<Exception>();
      foreach (var email in emails)
      {
        cancellationToken.ThrowIfCancellationRequested();
        var message = new Message
        {
          Body = new ItemBody
          {
            Content = email.Body,
            ContentType = BodyType.Html
          },
          Subject = email.Subject,
          ToRecipients = CreateRecipients(email.To),
          CcRecipients = CreateRecipients(email.Cc),
          BccRecipients = CreateRecipients(email.Bcc),
          ReplyTo = CreateRecipients(email.ReplyTo),
          Importance = email.Important ? Importance.High : Importance.Normal
        };

        if (email.Attachments?.Count > 0)
        {
          message.Attachments = email.Attachments
            .Where(attachment => attachment is not null)
            .Select(CreateAttachment)
            .Cast<Microsoft.Graph.Beta.Models.Attachment>()
            .ToList();
        }

        try
        {
          await client.Me.SendMail.PostAsync(new SendMailPostRequestBody { Message = message, SaveToSentItems = true }, cancellationToken: cancellationToken);
          await Task.Delay(10, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
          throw;
        }
        catch (Exception ex)
        {
          exceptions.Add(new InvalidOperationException($"Failed to send email to {FormatRecipients(email.To)}.", ex));
        }
      }

      if (exceptions.Count > 0)
      {
        throw new AggregateException("Failed to send one or more emails.", exceptions);
      }
    }
    finally
    {
      Semaphore.Release();
    }
  }

  private static List<Recipient> CreateRecipients(IEnumerable<string> emailAddresses)
    => emailAddresses?.Select(o => new Recipient { EmailAddress = new EmailAddress { Address = o } }).ToList() ?? [];

  private static string FormatRecipients(IEnumerable<string> emailAddresses)
    => emailAddresses is null ? "(none)" : string.Join(", ", emailAddresses);

  private static FileAttachment CreateAttachment(EmailAttachment attachment)
  {
    return new FileAttachment
    {
      Name = attachment.Name,
      ContentType = attachment.ContentType,
      ContentBytes = attachment.ContentBytes,
      ContentId = attachment.ContentId,
      IsInline = attachment.IsInline
    };
  }

  private sealed class ServiceAccountMailCredential(ServiceAccountAuthService authService) : TokenCredential
  {
    public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
      => authService.GetMailAccessTokenAsync(cancellationToken).GetAwaiter().GetResult();

    public override async ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
      => await authService.GetMailAccessTokenAsync(cancellationToken);
  }
}

public class Email
{
  public List<string> To { get; init; }
  public List<string> Cc { get; init; }
  public List<string> Bcc { get; init; }
  public List<string> ReplyTo { get; init; }
  public string Subject { get; init; }
  public string Body { get; init; }
  public bool Important { get; init; }
  public List<EmailAttachment> Attachments { get; init; }
}

public class EmailAttachment
{
  public string Name { get; init; }
  public string ContentType { get; init; }
  public byte[] ContentBytes { get; init; }
  public string ContentId { get; init; }
  public bool IsInline { get; init; }
}
