using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SendGrid;
using SendGrid.Helpers.Mail;

namespace Casazen.Infrastructure.External;

public record EmailSendResult(bool Success, string? ErrorDetail = null);

public interface ISendGridService
{
    Task<EmailSendResult> SendEmailAsync(string to, string subject, string htmlContent);
}

public class SendGridService(IConfiguration configuration, ILogger<SendGridService> logger, ISendGridClient client) : ISendGridService
{
    private readonly string _fromEmail = configuration["Email:FromAddress"] ?? "noreply@casazen.app";

    public async Task<EmailSendResult> SendEmailAsync(string to, string subject, string htmlContent)
    {
        try
        {
            var from = new EmailAddress(_fromEmail, "CASAZEN");
            var toEmail = new EmailAddress(to);
            var msg = new SendGridMessage()
            {
                From = from,
                Subject = subject,
                HtmlContent = htmlContent
            };
            msg.AddTo(toEmail);

            var response = await client.SendEmailAsync(msg);
            if (response.IsSuccessStatusCode)
            {
                logger.LogInformation("Email sent to {To}: {StatusCode}", to, response.StatusCode);
                return new EmailSendResult(true);
            }

            var body = await response.Body.ReadAsStringAsync();
            var detail = $"SendGrid {(int)response.StatusCode} {response.StatusCode}: {body}";
            logger.LogError("Email to {To} failed — {Detail}", to, detail);
            return new EmailSendResult(false, detail);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error sending email to {To}", to);
            return new EmailSendResult(false, ex.Message);
        }
    }
}