using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SendGrid;
using SendGrid.Helpers.Mail;

namespace Casazen.Infrastructure.External;

public interface ISendGridService
{
    Task<bool> SendEmailAsync(string to, string subject, string htmlContent);
}

public class SendGridService(IConfiguration configuration, ILogger<SendGridService> logger, ISendGridClient client) : ISendGridService
{
    private readonly string _fromEmail =  configuration["Email:FromAddress"] ?? "noreply@casazen.app";

    public async Task<bool> SendEmailAsync(string to, string subject, string htmlContent)
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
            logger.LogInformation("Email sent to {To}: {StatusCode}", to, response.StatusCode);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error sending email to {To}", to);
            return false;
        }
    }
}