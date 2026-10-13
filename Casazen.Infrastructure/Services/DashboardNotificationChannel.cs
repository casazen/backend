using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// In-app notification channel. For MVP, logs the notification.
/// Full implementation will persist to a Notifications table for the dashboard bell.
/// </summary>
public class DashboardNotificationChannel : INotificationChannel
{
    private readonly ILogger<DashboardNotificationChannel> _logger;

    public DashboardNotificationChannel(ILogger<DashboardNotificationChannel> logger) => _logger = logger;
    public NotificationChannelType ChannelType => NotificationChannelType.Dashboard;

    public Task<NotificationResult> SendAsync(NotificationMessage message, CancellationToken ct = default)
    {
        // No subject in the log: it may carry a guest name (FD-17, A9-36).
        _logger.LogInformation("[Dashboard] notification for {MaskedRecipient}", LogRedaction.MaskEmail(message.Recipient));
        return Task.FromResult(new NotificationResult(true));
    }
}
