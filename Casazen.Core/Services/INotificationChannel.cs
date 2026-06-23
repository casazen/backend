namespace Casazen.Core.Services;

public enum NotificationChannelType { Email, Dashboard, WhatsApp, Sms }

public record NotificationMessage(
    string Recipient,
    string Subject,
    string Body,
    NotificationChannelType Channel,
    IDictionary<string, string>? Metadata = null);

public record NotificationResult(bool Success, string? Error = null);

public interface INotificationChannel
{
    NotificationChannelType ChannelType { get; }
    Task<NotificationResult> SendAsync(NotificationMessage message, CancellationToken ct = default);
}
