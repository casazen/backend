using Casazen.Core.Services;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// The channel manager of a test that is not about it (E08/F01/US02/T01): nothing is mirrored, so nothing is asked and nothing refuses.
/// <c>BookingService</c> needs one: it asks it before it writes a booking.
/// </summary>
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
