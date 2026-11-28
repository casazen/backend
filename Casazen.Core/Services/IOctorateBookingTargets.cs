namespace Casazen.Core.Services;

/// <summary>
/// The one place that says where in Octorate a CasaZen property books (E08/F01/US01/T02, ADR-023): every writer of a booking (the create, the
/// payments, later the changes and the cancellations) asks it first, and writes nothing to Octorate without a target.
/// </summary>
/// <remarks>
/// The rules, in this order: the org of the property has a <c>Connected</c> Octorate connection in this environment;
/// <see cref="IChannelMappingService.IsWriteBlockedAsync"/> is false for the property; the property has a <c>Confirmed</c> mapping (the
/// accommodation comes from it). Otherwise <see cref="OctorateTargetGap.NotMirrored"/>: nothing is ever written for a property whose mapping is
/// proposed, ambiguous or missing. The room is the one of the mapping; a confirmed mapping with no room reads the rooms of the accommodation and
/// keeps the only one, and with none or several answers <see cref="OctorateTargetGap.RoomNotChosen"/> (remembered for five minutes, so the
/// quota is not spent again) and guesses nothing. A lookup that stores the room saves the mapping: call it before changing anything else in
/// the same unit of work.
/// </remarks>
public interface IOctorateBookingTargets
{
    Task<OctorateTargetLookup> LookupAsync(Guid propertyId, CancellationToken ct);

    /// <summary>
    /// The accommodation of Octorate that <paramref name="propertyId"/> books into, or <c>null</c> when the property is not mirrored (the same
    /// rules as <see cref="LookupAsync"/> up to the confirmed mapping). It reads CasaZen's database only: it never asks Octorate for the rooms,
    /// so a writer that needs the accommodation and nothing else (the payments of a reservation that exists) does not depend on those reads.
    /// </summary>
    Task<string?> AccommodationOfAsync(Guid propertyId, CancellationToken ct);
}

public enum OctorateTargetGap
{
    None = 0,

    NotMirrored = 1,

    RoomNotChosen = 2,
}

public sealed record OctorateTargetLookup(OctorateBookingTarget? Target, OctorateTargetGap Gap)
{
    public static OctorateTargetLookup NotMirrored { get; } = new(null, OctorateTargetGap.NotMirrored);

    public static OctorateTargetLookup RoomNotChosen { get; } = new(null, OctorateTargetGap.RoomNotChosen);

    public static OctorateTargetLookup Of(OctorateBookingTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);

        return new OctorateTargetLookup(target, OctorateTargetGap.None);
    }
}
