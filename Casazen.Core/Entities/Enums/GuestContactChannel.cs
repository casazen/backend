namespace Casazen.Core.Entities.Enums;

/// <summary>
/// The channel a CRM contact is reached on (NF-89, CRM-02). The confirmation, the legal basis and the suppression belong to one channel, so
/// the same person has one <see cref="GuestContact"/> for each channel and value. Stored as an integer in <c>GuestContacts.Channel</c>,
/// <b>append only</b>: never renumber a member.
/// </summary>
public enum GuestContactChannel
{
    /// <summary>An e-mail address.</summary>
    Email = 1,

    /// <summary>A phone number.</summary>
    Phone = 2,

    /// <summary>A phone number that is reached with WhatsApp.</summary>
    WhatsApp = 3,
}
