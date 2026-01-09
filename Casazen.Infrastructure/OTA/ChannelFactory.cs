using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.OTA;

public interface IChannelFactory
{
    IChannelAdapter GetAdapter(string platform);
}

public class ChannelFactory(IServiceProvider serviceProvider, ILogger<ChannelFactory> logger) : IChannelFactory
{
    public IChannelAdapter GetAdapter(string platform)
    {
        return platform.ToLower() switch
        {
            "airbnb" => serviceProvider.GetRequiredService<AirbnbAdapter>(),
            "booking.com" => serviceProvider.GetRequiredService<BookingComAdapter>(),
            "expedia" => serviceProvider.GetRequiredService<ExpediaAdapter>(),
            "vrbo" => serviceProvider.GetRequiredService<VrboAdapter>(),
            "tripadvisor" => serviceProvider.GetRequiredService<TripAdvisorAdapter>(),
            "agoda" => serviceProvider.GetRequiredService<AgodaAdapter>(),
            _ => throw new NotImplementedException($"Adapter for {platform} not implemented")
        };
    }
}