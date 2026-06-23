namespace Casazen.Infrastructure.External;

public record EmailSendResult(bool Success, string? ErrorDetail = null);

public interface IEmailService
{
    Task<EmailSendResult> SendEmailAsync(string to, string subject, string htmlContent);
}
