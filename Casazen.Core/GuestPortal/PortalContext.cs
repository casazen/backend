using Casazen.Core.Entities;

namespace Casazen.Core.GuestPortal;

/// <summary>
/// What a request of the guest portal is about (E50/F01/US01/T02, NF-152, NF-153, decision A3): the stay the token of the link opens, resolved once
/// per request by the token filter of the portal and handed to every section. It is the <b>only</b> source of the booking, the org and the property of
/// a portal endpoint: no endpoint of the portal reads a booking id from the client, and every query of a section names <see cref="OrgId"/> and
/// <see cref="BookingId"/> (or <see cref="PropertyId"/>) explicitly. The tenant of a portal request is the token, whoever is signed in.
/// </summary>
/// <remarks>
/// Made only by <c>PortalContextFactory</c> (Infrastructure), which is also the only place that sets <see cref="CanSeeReserved"/>: the check-in of
/// the portal (E51) refines it there. Keys are only ever added.
/// </remarks>
public sealed record PortalContext
{
    public required Guid LinkId { get; init; }

    public required Guid OrgId { get; init; }

    public required Guid BookingId { get; init; }

    public required Guid PropertyId { get; init; }

    public required string Language { get; init; }

    public required BookingStatus Status { get; init; }

    public required BookingSource Source { get; init; }

    public required bool IsOta { get; init; }

    public required bool IsDirect { get; init; }

    public required bool CheckInCompleted { get; init; }

    /// <summary>
    /// Whether the portal may show the reserved data of the stay (the precise address, the access instructions, the door code, the Wi-Fi): only
    /// after the check-in is completed (§5.1 of the specification, AC-NF153-3) and only for a stay to come or in progress, a confirmed or checked-in
    /// booking that is not a no-show: a cancelled stay, a no-show and a stay already checked out keep their link but never get it, even with a completed
    /// check-in (<see cref="GuestPortalRules.IsStayOpenToReservedData"/>, the rule of the lock code). Set only by <c>PortalContextFactory</c>.
    /// </summary>
    public required bool CanSeeReserved { get; init; }

    public required DateTime CheckInDate { get; init; }

    public required DateTime CheckOutDate { get; init; }
}

public interface IGuestPortalContextAccessor
{
    PortalContext? Current { get; }
}
