namespace Casazen.Core.Entities.Enums;

/// <summary>
/// Where a CRM contact came from (NF-89, CRM-02): the first thing the legal basis depends on. Stored as an integer in
/// <c>GuestContacts.Origin</c>, <b>append only</b>: never renumber a member.
/// </summary>
public enum GuestContactOrigin
{
    /// <summary>
    /// The address that an online travel agency forwards the guest's mail through: it reaches the guest for the stay only, so it is
    /// recognized and kept as service only (<see cref="GuestContactLegalBasis.ServiceOnly"/>), never used for marketing (AC-NF89-5).
    /// </summary>
    OtaForward = 1,

    /// <summary>Given by the guest in the online check-in.</summary>
    CheckIn = 2,

    /// <summary>Given by the guest when booking directly, at the checkout of a direct booking site.</summary>
    DirectBooking = 3,

    /// <summary>Typed in by the host or by a member of the team.</summary>
    Manual = 4,

    /// <summary>Read from the contact data (<c>contactInfo</c>) of a booking imported from Octorate.</summary>
    OctorateContactInfo = 5,

    /// <summary>Left by the guest on the welcome page of a property, reached with the QR code of the property.</summary>
    WelcomePage = 6,

    /// <summary>Left by the guest on the guest page of a stay.</summary>
    GuestPage = 7,
}
