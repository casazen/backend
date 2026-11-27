using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

/// <summary>
/// One way to reach a person in the CRM (NF-88, NF-89, CRM-01, CRM-02): an e-mail address, a phone number or a WhatsApp number, with where
/// it came from (<see cref="Origin"/>), whether the person confirmed it (<see cref="Confirmation"/>, the double opt-in), the legal basis it
/// can be used on (<see cref="LegalBasis"/>) and whether the person asked not to be written to (<see cref="IsSuppressed"/>). One row for
/// each org, channel and value: the same address given twice is one contact.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Personal data, encrypted.</b> <see cref="Value"/> and <see cref="DisplayName"/> are encrypted at rest (<c>EncryptedColumns</c>,
/// purpose <c>Casazen.GuestContact</c>): the code reads and writes the clear value through EF, the database only holds the payload.
/// Nothing is searched in them. A contact is found by <see cref="ValueHash"/>, the HMAC-SHA256 (lowercase hex) of the normalized value
/// keyed by <c>Crm:ContactIndexKey</c>, a secret that is not in the database; never compare payloads, the same value encrypts differently
/// every time. The hash is computed by <c>ICrmContactIndex</c>, not by the model.</item>
/// <item><b>Basis per channel.</b> The same person can consent on the e-mail and be service only on the phone: each channel is its own
/// row, with its own <see cref="LegalBasis"/>, <see cref="BasisVersion"/>, <see cref="BasisRecordedAt"/>, <see cref="ConsentSource"/> and
/// <see cref="ConsentIpAddress"/>. The model only records the facts; the rules that decide who may be written to are in the services.</item>
/// <item><b>Profile and lead.</b> <see cref="GuestCrmProfileId"/> is empty for a lead that left a contact on the welcome page and has no
/// profile yet. <see cref="GuestId"/> is the guest the contact was given by, when there is one.</item>
/// <item><b>Anonymization.</b> <see cref="ConsentIpAddress"/> is cleared when the contact is anonymized; the basis, its version and its time
/// stay as proof without personal data.</item>
/// <item><b>Tenant.</b> <see cref="ITenantOwned"/>: <see cref="OrgId"/> is the org of the guest. The row is read and written only in that org.</item>
/// </list>
/// </remarks>
[Table("GuestContacts")]
public class GuestContact : ITenantOwned
{
    /// <summary>Column length of <see cref="ValueHash"/>: an HMAC-SHA256 in lowercase hex.</summary>
    public const int ValueHashLength = 64;

    /// <summary>Column length of <see cref="Language"/>, the one of <c>GuestCrmProfile.Language</c>.</summary>
    public const int LanguageMaxLength = 10;

    /// <summary>Column length of <see cref="BasisVersion"/>, the one of <c>GuestConsentRecord.Version</c>.</summary>
    public const int BasisVersionMaxLength = 100;

    /// <summary>Column length of <see cref="ConsentIpAddress"/>, the one of <c>GuestConsentRecord.IpAddress</c>.</summary>
    public const int ConsentIpMaxLength = 50;

    /// <summary>Column length of <see cref="SuppressedSource"/>, the one of <c>SuppressionListEntry.Source</c>.</summary>
    public const int SuppressedSourceMaxLength = 50;

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Tenant of the row: the org of the guest (TN-2). Server-set, never client-supplied.</summary>
    public Guid OrgId { get; set; }

    /// <summary>The CRM profile the contact belongs to; <c>null</c> for a lead from the welcome page that has no profile yet.</summary>
    public Guid? GuestCrmProfileId { get; set; }

    /// <summary>The guest the contact was given by; <c>null</c> when it did not come from a guest record (a lead, a manual entry).</summary>
    public Guid? GuestId { get; set; }

    public GuestContactChannel Channel { get; set; }

    /// <summary>
    /// The e-mail address or the phone number, as the person gave it. <b>Encrypted at rest</b> (<c>EncryptedColumns</c>): the column is
    /// <c>text</c> because the payload is longer than the value; the length is checked on input.
    /// </summary>
    [Required]
    public string Value { get; set; } = string.Empty;

    /// <summary>
    /// HMAC-SHA256 (lowercase hex, 64 characters) of the normalized <see cref="Value"/>, keyed by <c>Crm:ContactIndexKey</c>: the index the
    /// contact is found by (unique with <see cref="OrgId"/> and <see cref="Channel"/>) without decrypting anything.
    /// </summary>
    [Required, MaxLength(ValueHashLength)]
    public string ValueHash { get; set; } = string.Empty;

    /// <summary>The name that goes with the contact, when the person gave one. <b>Encrypted at rest</b> like <see cref="Value"/>.</summary>
    public string? DisplayName { get; set; }

    /// <summary>The language of the page the person was on when they gave the contact (<c>it</c>, <c>en</c>...): the language of the messages to them.</summary>
    [MaxLength(LanguageMaxLength)]
    public string Language { get; set; } = string.Empty;

    public GuestContactOrigin Origin { get; set; }

    public GuestContactConfirmation Confirmation { get; set; } = GuestContactConfirmation.Unconfirmed;

    /// <summary>UTC instant of the last confirmation message sent to the contact; <c>null</c> = none sent.</summary>
    public DateTime? ConfirmationSentAt { get; set; }

    /// <summary>How many confirmation messages were sent to the contact.</summary>
    public int ConfirmationSentCount { get; set; }

    /// <summary>UTC instant the person confirmed; <c>null</c> = not confirmed.</summary>
    public DateTime? ConfirmedAt { get; set; }

    public GuestContactLegalBasis LegalBasis { get; set; } = GuestContactLegalBasis.None;

    /// <summary>
    /// Version of the text the basis rests on: the consent text of a <see cref="GuestContactLegalBasis.Consent"/>, the notice of a
    /// <see cref="GuestContactLegalBasis.SoftOptIn"/>. Empty while there is none.
    /// </summary>
    [MaxLength(BasisVersionMaxLength)]
    public string BasisVersion { get; set; } = string.Empty;

    /// <summary>UTC instant the current basis was recorded; <c>null</c> while <see cref="LegalBasis"/> is <see cref="GuestContactLegalBasis.None"/>.</summary>
    public DateTime? BasisRecordedAt { get; set; }

    /// <summary>Where the consent or the notice was given (the check-in portal, the welcome page...); <c>null</c> when the basis was not given by the person.</summary>
    public GuestConsentSource? ConsentSource { get; set; }

    /// <summary>Client address of the person's own consent action, as evidence; <c>null</c> when there is none. Cleared on anonymization.</summary>
    [MaxLength(ConsentIpMaxLength)]
    public string? ConsentIpAddress { get; set; }

    /// <summary>The person asked not to be written to on this channel (unsubscribe, request, erasure): no marketing, whatever the basis.</summary>
    public bool IsSuppressed { get; set; }

    /// <summary>UTC instant of the suppression; <c>null</c> while the contact is not suppressed.</summary>
    public DateTime? SuppressedAt { get; set; }

    /// <summary>How it was suppressed, as <c>SuppressionListEntry.Source</c> (<c>unsubscribe</c>, <c>manual</c>, <c>gdpr</c>); <c>null</c> while the contact is not suppressed.</summary>
    [MaxLength(SuppressedSourceMaxLength)]
    public string? SuppressedSource { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
