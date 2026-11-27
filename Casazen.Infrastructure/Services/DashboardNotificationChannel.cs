using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

public class DashboardNotificationChannel : INotificationChannel
{
    private readonly ILogger<DashboardNotificationChannel> _logger;

    public DashboardNotificationChannel(ILogger<DashboardNotificationChannel> logger) => _logger = logger;
    public NotificationChannelType ChannelType => NotificationChannelType.Dashboard;

    public Task<NotificationResult> SendAsync(NotificationMessage message, CancellationToken ct = default)
    {
        _logger.LogInformation("[Dashboard] notification for {MaskedRecipient}", LogRedaction.MaskEmail(message.Recipient));
        return Task.FromResult(new NotificationResult(true));
    }
}
