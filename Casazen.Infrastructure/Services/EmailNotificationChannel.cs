using Casazen.Core.Services;
using Casazen.Infrastructure.External;

namespace Casazen.Infrastructure.Services;

public class EmailNotificationChannel : INotificationChannel
{
    private readonly IEmailService _emailService;

    public EmailNotificationChannel(IEmailService emailService) => _emailService = emailService;
    public NotificationChannelType ChannelType => NotificationChannelType.Email;

    public async Task<NotificationResult> SendAsync(NotificationMessage message, CancellationToken ct = default)
    {
        try
        {
            await _emailService.SendEmailAsync(message.Recipient, message.Subject, message.Body);
            return new NotificationResult(true);
        }
        catch (Exception ex)
        {
            return new NotificationResult(false, ex.Message);
        }
    }
}
