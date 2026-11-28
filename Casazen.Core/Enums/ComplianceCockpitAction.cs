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
    ActivateProperty,

    /// <summary>
    /// Guest data of the stay incomplete for Alloggiati Web: where the host sees what is missing and completes it, or
    /// sends the check-in link to the guest. Target: the booking.
    /// </summary>
    CompleteGuestCheckIn,

    CheckOut,

    /// <summary>Alloggiati communication the host must send on the Questura portal. Target: the booking.</summary>
    SendAlloggiati,

    ResolveAlloggiatiFailure,

    ConfirmPropertyReady,

    VerifyIdentity,

    /// <summary>
    /// The Alloggiati Web communication of the stay was sent, entrusted to the channel manager or declared sent, and its receipt is not archived
    /// yet: the host uploads it (CHK-08). Target: the booking.
    /// </summary>
    UploadAlloggiatiReceipt,

    ActivateOctorateSend,

    SendAlloggiatiManually,

    /// <summary>The proof of the ISTAT communication of a period is not archived: the host uploads it. Target: the property, with the period (<c>yyyy-MM</c>).</summary>
    UploadIstatProof,

    EnterMunicipalityTaxRate,

    DeclareTouristTax,

    /// <summary>The police (Alloggiati Web) credentials kept in CasaZen were refused: the host sets them again. Target: the property.</summary>
    RenewPoliceCredentials,

    NotifyTicinoPolice,

    /// <summary>
    /// The guests who arrived at a Swiss property on a day are not notified to the cantonal authority yet, in a canton other than Ticino that asks for an
    /// electronic notification (E31/F02/US01/T01, NF-131, casazen/backend#979): CasaZen never transmits, the host copies the data CasaZen prepared on the
    /// official portal of the canton and declares the day notified. Only where the duty is <c>Required</c> (the host confirmed it, or the regime is verified as
    /// covering private rentals) and only with <c>Features:GuestRegistrationRegimes</c> on. Target: the property, with the arrival day in <c>Period</c>
    /// (<c>yyyy-MM-dd</c>) and the deadline of the regime in <c>DueAt</c>.
    /// </summary>
    NotifyGuestsAuthority,
}
