using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

/// <summary>
/// The one writer of the CRM contacts (E07/F01/US02/T02, NF-89, CRM-02): creates and changes <see cref="GuestContact"/> rows, with where each
/// contact came from, whether the person confirmed it (double opt-in), the legal basis per channel and the suppression. What a contact may be
/// used for is decided by <see cref="CrmContactRules"/>, nowhere else. A value is normalized by <see cref="CrmNormalizer"/> and found by its
/// keyed hash (<see cref="ICrmContactIndex"/>), so the same address given twice is one contact.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>A weaker basis never overwrites a stronger one</b> (consent over soft opt-in over service only over none), and a suppression is
/// never cleared: not by a later booking, not by a later consent.</item>
/// <item><b>An OTA forward address stays service only</b>: once stored as <see cref="GuestContactOrigin.OtaForward"/> no call raises it.</item>
/// <item>Every method names the org it works for and finds a contact only in it. No address, hash or name is written to a log.</item>
/// </list>
/// </remarks>
public interface ICrmContactService
{
    /// <summary>
    /// Creates the contact or changes the one with the same keyed value (org, channel, hash). Fills the profile, the guest, the display name and
    /// the language when the contact has none; sets the basis the origin gives (<see cref="CrmContactRules.ResolveBasis"/>) when it is stronger
    /// than the one the contact has. An e-mail address that arrives with an OTA booking, or is on a relay domain of <c>Crm:OtaForwardDomains</c>,
    /// is stored as <see cref="GuestContactOrigin.OtaForward"/> and service only. An address already on the suppression list is flagged
    /// suppressed. Returns <c>null</c> when the value is not an e-mail address or a phone number that can be stored; nothing is written then.
    /// </summary>
    Task<GuestContact?> UpsertContactAsync(CrmContactInput input, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records a person's consent on a contact (art. 6.1.a GDPR): the basis becomes <see cref="GuestContactLegalBasis.Consent"/> and the
    /// confirmation <see cref="GuestContactConfirmation.Unconfirmed"/> (a consent already confirmed that is renewed stays confirmed), with the
    /// version of the text, the source, the address of the person's connection, the time and the language. With a guest known it also appends one
    /// <see cref="GuestConsentRecord"/> (marketing, granted), so the privacy history stays complete (unless <see cref="CrmConsentInput.RecordHistory"/>
    /// says the caller wrote that row already). An OTA forward address keeps its origin and
    /// its service-only basis (the consent is in the history, the address is not marketable), and a suppressed contact stays suppressed.
    /// Returns <c>null</c> when the value cannot be stored.
    /// </summary>
    Task<GuestContact?> RecordConsentAsync(CrmConsentInput input, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records the double opt-in of a contact: confirmation <see cref="GuestContactConfirmation.Confirmed"/> and the time. The only call that
    /// does it, besides the read of a confirmation the check-in recorded before (<see cref="SyncFromGuestAsync"/>). Idempotent. Returns <c>null</c>
    /// when the org has no such contact.
    /// </summary>
    Task<GuestContact?> MarkConfirmedAsync(Guid orgId, Guid contactId, CancellationToken cancellationToken = default);

    /// <summary>
    /// A person withdraws, or objects: the contact is suppressed (and, for an e-mail address, the address goes on the suppression list), its
    /// basis goes back to service only, and with a guest known a <see cref="GuestConsentRecord"/> (marketing, withdrawn) is appended. Nothing
    /// clears the suppression afterwards. Idempotent. Returns <c>null</c> when the org has no such contact.
    /// </summary>
    /// <param name="orgId">The org of the contact.</param>
    /// <param name="contactId">The contact.</param>
    /// <param name="source">How it was suppressed, as <c>SuppressionListEntry.Source</c>: <c>unsubscribe</c>, <c>manual</c>, <c>gdpr</c>.</param>
    /// <param name="consentSource">Who recorded it, for the privacy history.</param>
    /// <param name="recordedByUserId">The user (Auth0 subject) who recorded it, when it was not the person.</param>
    /// <param name="note">How the person asked, when a host records it for them; at most 500 characters.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<GuestContact?> WithdrawAsync(
        Guid orgId,
        Guid contactId,
        string source,
        GuestConsentSource consentSource,
        string? recordedByUserId = null,
        string? note = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Builds the contacts of a guest from what CasaZen already has, and links them to the guest's profile: the e-mail address and the phone
    /// number of the guest record (origin from the guest's latest booking: an OTA source is a forward, <c>Direct</c> a direct booking, anything
    /// else manual; a direct booking is soft opt-in from the moment the guest has a recorded soft opt-in notice and a confirmed booking), the
    /// per-channel consents of the check-in (<c>MarketingConsentRecord</c>: the e-mail is a consent, confirmed only when the guest confirmed
    /// it; WhatsApp is a consent on the phone), and the single yes/no flag of the old check-in and checkout (<c>Guest.MarketingConsent</c>: a
    /// consent, unconfirmed, so not marketable until confirmed). Flags the addresses already on the suppression list. Idempotent: running it
    /// twice changes nothing. Reads the booker only, never <see cref="StayGuest"/>, a document or an Alloggiati field, and nothing of an erased
    /// guest. Returns the contacts of the guest, empty for a guest that is not found or is erased.
    /// </summary>
    Task<IReadOnlyList<GuestContact>> SyncFromGuestAsync(Guid guestId, CancellationToken cancellationToken = default);
}

/// <summary>What a basis rests on: the version of the text or of the notice the person saw, when, where, and from which address.</summary>
/// <param name="Version">Version of the consent text or of the soft opt-in notice; at most 100 characters.</param>
/// <param name="RecordedAt">When the person saw it or gave it (UTC).</param>
/// <param name="Source">Where it happened, as the privacy history records it; <c>null</c> when not known.</param>
/// <param name="IpAddress">Address of the person's connection, as evidence; <c>null</c> when there is none; at most 50 characters.</param>
public sealed record CrmBasisEvidence(string Version, DateTime RecordedAt, GuestConsentSource? Source = null, string? IpAddress = null);

/// <summary>A contact to create or to change (<see cref="ICrmContactService.UpsertContactAsync"/>).</summary>
/// <param name="OrgId">The org of the contact (server-set, never from a client).</param>
/// <param name="Channel">The channel of <paramref name="Value"/>.</param>
/// <param name="Value">An e-mail address or a phone number, as the person gave it; normalized by the service.</param>
/// <param name="Origin">Where it came from.</param>
/// <param name="GuestCrmProfileId">The profile it belongs to; <c>null</c> for a lead with no profile yet.</param>
/// <param name="GuestId">The guest that gave it; <c>null</c> for a lead or a manual entry.</param>
/// <param name="DisplayName">The name that goes with it, when the person gave one.</param>
/// <param name="Language">The language of the page the person was on (<c>it</c>, <c>en</c>).</param>
/// <param name="BookedVia">The source of the booking the value arrived with; an OTA source makes an e-mail address a forward.</param>
/// <param name="Notice">The soft opt-in notice shown to the person, when there is evidence of it (a notice with no version is none).</param>
/// <param name="PurchaseConfirmed">True when the booking the contact comes from is a purchase (<see cref="CrmContactRules.IsPurchase"/>).</param>
public sealed record CrmContactInput(
    Guid OrgId,
    GuestContactChannel Channel,
    string? Value,
    GuestContactOrigin Origin,
    Guid? GuestCrmProfileId = null,
    Guid? GuestId = null,
    string? DisplayName = null,
    string? Language = null,
    BookingSource? BookedVia = null,
    CrmBasisEvidence? Notice = null,
    bool PurchaseConfirmed = false);

/// <summary>A consent to record (<see cref="ICrmContactService.RecordConsentAsync"/>).</summary>
/// <param name="OrgId">The org of the contact (server-set, never from a client).</param>
/// <param name="Channel">The channel of <paramref name="Value"/>.</param>
/// <param name="Value">An e-mail address or a phone number, as the person gave it; normalized by the service.</param>
/// <param name="Origin">Where the contact came from (the page or portal the person was on).</param>
/// <param name="Version">Version of the consent text the person agreed to; required.</param>
/// <param name="Source">Where the consent was given, for the privacy history.</param>
/// <param name="IpAddress">Address of the person's connection, as evidence; <c>null</c> when there is none.</param>
/// <param name="RecordedAt">When the person consented (UTC); now when omitted.</param>
/// <param name="Language">The language of the page the person was on.</param>
/// <param name="DisplayName">The name that goes with the contact.</param>
/// <param name="GuestCrmProfileId">The profile it belongs to; <c>null</c> for a lead with no profile yet.</param>
/// <param name="GuestId">The guest that consented; with it the privacy history gets its row.</param>
/// <param name="BookedVia">The source of the booking the value arrived with.</param>
/// <param name="RecordHistory">
/// True (the default) to append the <see cref="GuestConsentRecord"/> of the privacy history when a guest is known. False when the caller
/// already wrote that row itself, in the same insert as the booking (the public checkout, E07/F04/US01/T02), so the history holds the act once.
/// </param>
public sealed record CrmConsentInput(
    Guid OrgId,
    GuestContactChannel Channel,
    string? Value,
    GuestContactOrigin Origin,
    string Version,
    GuestConsentSource Source,
    string? IpAddress = null,
    DateTime? RecordedAt = null,
    string? Language = null,
    string? DisplayName = null,
    Guid? GuestCrmProfileId = null,
    Guid? GuestId = null,
    BookingSource? BookedVia = null,
    bool RecordHistory = true);
