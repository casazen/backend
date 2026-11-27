namespace Casazen.Core.Entities.Enums;

/// <summary>
/// The legal basis a CRM contact can be used on, for one channel (NF-89, CRM-02, PO-07). Stored as an integer in
/// <c>GuestContacts.LegalBasis</c>, <b>append only</b>: never renumber a member.
/// </summary>
public enum GuestContactLegalBasis
{
    /// <summary>No basis is recorded for this channel.</summary>
    None = 0,

    /// <summary>
    /// What the stay or the service needs (messages about the stay, check-in, invoice) and nothing more: never marketing. The addresses
    /// forwarded by the online travel agencies are always this (<see cref="GuestContactOrigin.OtaForward"/>).
    /// </summary>
    ServiceOnly = 1,

    /// <summary>
    /// Marketing to a direct customer for similar services, without consent (art. 130 c. 4 Codice privacy, PO-07): the notice was shown when
    /// the contact was collected and every message carries the unsubscribe.
    /// </summary>
    SoftOptIn = 2,

    /// <summary>The explicit consent of the person for this channel (art. 6.1.a GDPR), recorded with the version of the text.</summary>
    Consent = 3,
}
