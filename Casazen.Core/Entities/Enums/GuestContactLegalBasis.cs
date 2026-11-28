namespace Casazen.Core.Entities.Enums;

public enum GuestContactLegalBasis
{
    None = 0,

    ServiceOnly = 1,

    /// <summary>
    /// Marketing to a direct customer for similar services, without consent (art. 130 c. 4 Codice privacy, PO-07): the notice was shown when
    /// the contact was collected and every message carries the unsubscribe.
    /// </summary>
    SoftOptIn = 2,

    /// <summary>The explicit consent of the person for this channel (art. 6.1.a GDPR), recorded with the version of the text.</summary>
    Consent = 3,
}
