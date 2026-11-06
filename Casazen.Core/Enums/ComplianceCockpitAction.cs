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

    // E03/F02/US01/T03 (NF-52, CHK-12): the actions below are appended, never renamed or renumbered; the clients map them to a page
    // (COMPLIANCE_ACTION_TARGETS) and show an item whose action they do not know without a link.

    /// <summary>
    /// The identity of the guest who leaves the document is not verified yet: their selfie is awaited before the arrival, or an operator has
    /// to decide (CHK-11, NF-50). Target: the booking.
    /// </summary>
    VerifyIdentity,

    /// <summary>
    /// The Alloggiati Web communication of the stay was sent, entrusted to the channel manager or declared sent, and its receipt is not archived
    /// yet: the host uploads it (CHK-08). Target: the booking.
    /// </summary>
    UploadAlloggiatiReceipt,

    /// <summary>
    /// The police send of the channel manager is not active or cannot be verified for the property: activate it or reconnect it (CHK-03, CHK-12).
    /// Target: the property.
    /// </summary>
    ActivateOctorateSend,

    /// <summary>
    /// A stay the channel manager does not send by itself: one over 30 nights, or a short stay not covered by its daily run. The host sends it
    /// by hand (CHK-12). Target: the booking.
    /// </summary>
    SendAlloggiatiManually,

    /// <summary>The proof of the ISTAT communication of a period is not archived: the host uploads it. Target: the property, with the period (<c>yyyy-MM</c>).</summary>
    UploadIstatProof,

    /// <summary>The tourist tax rate of the property's comune is not known (never taken as zero): the host enters it. Target: the property.</summary>
    EnterMunicipalityTaxRate,

    /// <summary>The tourist tax of a closed period is not declared and paid with its receipts archived. Target: the property, with the period (<c>yyyy-MM</c>).</summary>
    DeclareTouristTax,

    /// <summary>The police (Alloggiati Web) credentials kept in CasaZen were refused: the host sets them again. Target: the property.</summary>
    RenewPoliceCredentials,
}
