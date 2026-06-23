using Casazen.Core.Services;
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
        _logger.LogInformation("[Dashboard] {Subject} → {Recipient}", message.Subject, message.Recipient);
        return Task.FromResult(new NotificationResult(true));
    }
}
