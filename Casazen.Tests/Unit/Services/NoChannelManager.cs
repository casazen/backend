using Casazen.Core.Services;

namespace Casazen.Tests.Unit.Services;

internal sealed class NoChannelManager : IChannelManagerAvailability
{
    public static NoChannelManager Instance { get; } = new();

    public Task<ChannelManagerAvailability> CheckAsync(
        Guid propertyId,
        Guid orgId,
        DateOnly from,
        DateOnly to,
        int guests,
        CancellationToken ct) => Task.FromResult(ChannelManagerAvailability.NotMirrored);
}
