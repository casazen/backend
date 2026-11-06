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
    /// <summary>The target of <paramref name="propertyId"/>, or why there is none. A property the caller cannot see is not mirrored.</summary>
    Task<OctorateTargetLookup> LookupAsync(Guid propertyId, CancellationToken ct);
}

/// <summary>Why a property has no target in Octorate (<see cref="IOctorateBookingTargets.LookupAsync"/>).</summary>
public enum OctorateTargetGap
{
    /// <summary>The property has a target.</summary>
    None = 0,

    /// <summary>No connected account, no confirmed mapping, or a mapping still to decide: the property is not mirrored on Octorate.</summary>
    NotMirrored = 1,

    /// <summary>The mapping is confirmed but its accommodation has no single room: the host chooses it in the mapping step.</summary>
    RoomNotChosen = 2,
}

/// <summary>
/// The answer of <see cref="IOctorateBookingTargets.LookupAsync"/>: the target, or <c>null</c> with the <see cref="Gap"/> that says why.
/// </summary>
public sealed record OctorateTargetLookup(OctorateBookingTarget? Target, OctorateTargetGap Gap)
{
    /// <summary>The property is not mirrored on Octorate: nothing is written for it.</summary>
    public static OctorateTargetLookup NotMirrored { get; } = new(null, OctorateTargetGap.NotMirrored);

    /// <summary>The room of the confirmed mapping is not chosen: nothing is written until the host chooses it.</summary>
    public static OctorateTargetLookup RoomNotChosen { get; } = new(null, OctorateTargetGap.RoomNotChosen);

    /// <summary>The property books into <paramref name="target"/>.</summary>
    public static OctorateTargetLookup Of(OctorateBookingTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);

        return new OctorateTargetLookup(target, OctorateTargetGap.None);
    }
}
