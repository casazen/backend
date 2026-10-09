namespace Casazen.Core.Authorization;

/// <summary>
/// The finer host permissions of AM-03, all of the <c>short-rent</c> context. They were carved out of the broad ones
/// (<c>property.write</c>, <c>guest.write</c>, <c>booking.write</c>) so that a role can be given the one action without the
/// rest: the Collaborator creates interventions but cannot change prices or CIN, handles guests but cannot erase them.
/// </summary>
public static class HostPermissions
{
    /// <summary>
    /// Create a request to a supplier (an intervention) for a stay, find a supplier for it, mark it paid. It used to be
    /// <c>property.write</c>. Given to the owner, the property managers and the collaborator.
    /// </summary>
    public const string ServiceRequestWrite = "servicerequest.write";

    /// <summary>
    /// Erase or anonymize a guest and change its consents (art. 17, 7 GDPR): the destructive and legal acts on a guest's data. It
    /// used to be <c>guest.write</c>, which also lets a person register and correct a guest. Given to the owner and the property
    /// managers, not to the collaborator.
    /// </summary>
    public const string GuestManage = "guest.manage";

    /// <summary>
    /// Register the guests of a stay for the police communication and declare the Alloggiati communication sent. It used to be
    /// <c>booking.write</c>, which also creates, cancels and moves bookings. Given to the owner and the property managers; the
    /// collaborator does not have it (a decision of the product owner whether it should).
    /// </summary>
    public const string AlloggiatiSubmit = "alloggiati.submit";

    /// <summary>What the short-rent owner and the property manager hold on top of the permissions they always had.</summary>
    public static IReadOnlyList<string> ShortRentFine { get; } = [ServiceRequestWrite, GuestManage, AlloggiatiSubmit];
}
