namespace Casazen.Core.Entities.Enums;

/// <summary>
/// Where a CRM contact stands in the double opt-in (NF-89, NF-90, CRM-02): whether the person confirmed that the address or the number is
/// theirs and that they want to be written to. Stored as an integer in <c>GuestContacts.Confirmation</c>, <b>append only</b>: never
/// renumber a member.
/// </summary>
public enum GuestContactConfirmation
{
    /// <summary>No confirmation was asked or sent yet.</summary>
    Unconfirmed = 1,

    /// <summary>The confirmation was sent and waits for the person's answer.</summary>
    Pending = 2,

    /// <summary>The person confirmed.</summary>
    Confirmed = 3,
}
