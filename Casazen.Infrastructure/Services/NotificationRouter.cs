using Casazen.Core.Services;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

public class NotificationRouter
{
    private readonly IEnumerable<INotificationChannel> _channels;
    private readonly ILogger<NotificationRouter> _logger;

    public NotificationRouter(IEnumerable<INotificationChannel> channels, ILogger<NotificationRouter> logger)
    {
        _channels = channels;
        _logger = logger;
    }

    public async Task SendAsync(
        NotificationMessage message,
        NotificationChannelType? fallback = null,
        CancellationToken ct = default)
    {
        var channel = _channels.FirstOrDefault(c => c.ChannelType == message.Channel);
        if (channel is null)
        {
            _logger.LogWarning("No channel found for {ChannelType}", message.Channel);
            return;
        }

        var result = await channel.SendAsync(message, ct);
        if (result.Success)
        {
            _logger.LogInformation("Notification sent via {Channel} to {Recipient}", message.Channel, message.Recipient);
            return;
        }

        _logger.LogWarning("Channel {Channel} failed: {Error}", message.Channel, result.Error);

        if (fallback.HasValue)
        {
            var fallbackChannel = _channels.FirstOrDefault(c => c.ChannelType == fallback.Value);
            if (fallbackChannel is not null)
            {
                var fbMsg = message with { Channel = fallback.Value };
                var fbResult = await fallbackChannel.SendAsync(fbMsg, ct);
                _logger.LogInformation(
                    fbResult.Success ? "Fallback notification sent via {Channel}" : "Fallback also failed: {Error}",
                    fallback.Value, fbResult.Error);
            }
        }
    }
}
