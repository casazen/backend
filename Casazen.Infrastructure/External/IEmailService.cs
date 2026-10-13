namespace Casazen.Infrastructure.External;

/// <summary>Outcome of one email handed to the provider.</summary>
/// <param name="Success">The provider accepted the email.</param>
/// <param name="ErrorDetail">Short reason when not sent (no recipient data).</param>
/// <param name="Skipped">Not sent because the provider is not configured (Development/Testing only).</param>
/// <param name="IsTransient">The failure may succeed on retry (timeouts, rate limits, provider 5xx).</param>
public record EmailSendResult(bool Success, string? ErrorDetail = null, bool Skipped = false, bool IsTransient = false)
{
    public const string NotConfiguredDetail = "email_not_configured";

    public static EmailSendResult Sent() => new(true);

    public static EmailSendResult NotConfigured() => new(false, NotConfiguredDetail, Skipped: true);
}

/// <summary>
/// The single email sender of the application (implemented by <see cref="ResendEmailService"/>, configured through
/// <see cref="Email.EmailOptions"/>). Request handlers do not call it directly: they render a template
/// (<see cref="Email.Templates.EmailTemplates"/>) and queue it with <see cref="Email.IEmailQueue"/>; code already
/// running in a Hangfire job may send directly.
/// </summary>
public interface IEmailService
{
    Task<EmailSendResult> SendEmailAsync(string to, string subject, string htmlContent);
}
