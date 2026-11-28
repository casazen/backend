namespace Casazen.Core.Entities.Enums;

/// <summary>
/// The channel a CRM contact is reached on (NF-89, CRM-02). The confirmation, the legal basis and the suppression belong to one channel, so
/// the same person has one <see cref="GuestContact"/> for each channel and value. Stored as an integer in <c>GuestContacts.Channel</c>,
/// <b>append only</b>: never renumber a member.
/// </summary>
public enum GuestContactChannel
{
    Email = 1,

    Phone = 2,

    WhatsApp = 3,
}
