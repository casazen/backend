namespace Casazen.Core.Enums;

/// <summary>
/// What the host must do for an item of the compliance cockpit (<c>GET /api/compliance/summary</c>, CO-04, A5-09).
/// The API sends the action and the id of its target (<c>propertyId</c> or <c>bookingId</c>), never a front-end path:
/// each client builds its own route (web: <c>src/lib/compliance-routes.ts</c> from the <c>ROUTE_MANIFEST</c>), so a
/// renamed page can no longer leave the cockpit with dead links. Serialized by name: rename or remove a value only
/// together with the clients.
/// </summary>
public enum ComplianceCockpitAction
{
    /// <summary>Property not active (pending or suspended): the activation wizard. Target: the property.</summary>
    ActivateProperty,

    /// <summary>
    /// Guest data of the stay incomplete for Alloggiati Web: where the host sees what is missing and completes it, or
    /// sends the check-in link to the guest. Target: the booking.
    /// </summary>
    CompleteGuestCheckIn,

    /// <summary>Departure due: the check-out wizard of the booking. Target: the booking.</summary>
    CheckOut,

    /// <summary>Alloggiati communication the host must send on the Questura portal. Target: the booking.</summary>
    SendAlloggiati,

    /// <summary>Alloggiati communication in error or rejected. Target: the booking.</summary>
    ResolveAlloggiatiFailure,

    /// <summary>
    /// Stay checked out whose property was not declared ready for the next guest: the check-out wizard of the booking,
    /// on its last step (CO-17). Target: the booking.
    /// </summary>
    ConfirmPropertyReady,
}
