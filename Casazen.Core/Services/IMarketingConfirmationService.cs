namespace Casazen.Core.Services;

/// <summary>
/// The double opt-in of the marketing consent (E07/F04/US01/T01, NF-90, CRM-03, AC-NF90-5): a consent given on an e-mail address is not
/// marketable until the person follows the link of an e-mail sent to that address (<c>CrmContactRules.CanReceiveMarketing</c>). This service
/// sends that e-mail and records the click. Every collection point of the epic (checkout, check-in, welcome page, guest page, the host) calls
/// <see cref="SendConfirmationAsync"/> after it recorded a consent; the page of the link calls <see cref="ConfirmAsync"/>.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>The link is signed and carries no personal data</b>: the id of the contact, the expiry and the signature (HMAC-SHA256 with the
/// secret of the signed links, <c>SIGNED_LINK_SECRET</c>), never the address or the org, and it travels in the fragment of the address of the
/// page, which a browser never sends to a server.</item>
/// <item><b>Limits</b>: at most <c>Crm:ConfirmationMaxSends</c> messages for each contact and one every ten minutes. A repeat inside the ten
/// minutes, or past the limit, sends nothing.</item>
/// <item><b>Never to an address that opted out</b>: a suppressed contact gets nothing, whatever consent is recorded on it, and following an old
/// link cannot lift the suppression.</item>
/// <item>The logs carry ids, never an address.</item>
/// </list>
/// </remarks>
public interface IMarketingConfirmationService
{
    /// <summary>
    /// Queues the double opt-in e-mail of a contact, in the language of the contact (<c>it</c> or <c>en</c>, Italian when empty), through the
    /// e-mail queue; sets the confirmation to <c>Pending</c> and records the time and the count of the sends. Sends only to an e-mail contact
    /// whose basis is a consent, that is not suppressed and not confirmed yet. Never throws for a rule: the outcome says what happened, so the
    /// callers that must not fail (the checkout, the check-in) check nothing and the host's endpoint maps the outcome to its error.
    /// </summary>
    Task<MarketingConfirmationSendOutcome> SendConfirmationAsync(Guid orgId, Guid contactId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records the click on the link: verifies the signed values (timestamp, expiry, signature in constant time), then marks the contact confirmed
    /// (<c>ICrmContactService.MarkConfirmedAsync</c>) and, for the rows of the old per-channel consent of the check-in, the confirmation of the
    /// same guest and address. Idempotent: a second click changes nothing. The contact is looked for only after the signature is valid.
    /// </summary>
    /// <param name="contactId">The contact named by the link.</param>
    /// <param name="expiresAt">The expiry of the link, <c>yyyy-MM-ddTHH:mm:ssZ</c>.</param>
    /// <param name="signature">The signature of the link (hex).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<MarketingConfirmationOutcome> ConfirmAsync(Guid contactId, string expiresAt, string signature, CancellationToken cancellationToken = default);
}

public enum MarketingConfirmationSendOutcome
{
    Queued = 1,

    ContactNotFound = 2,

    Suppressed = 3,

    NotRequired = 4,

    LimitReached = 5,

    TooSoon = 6,

    Unavailable = 7,
}

public enum MarketingConfirmationOutcome
{
    Confirmed = 1,

    AlreadyConfirmed = 2,

    InvalidTimestamp = 3,

    LinkExpired = 4,

    InvalidSignature = 5,

    NotConfigured = 6,

    ContactNotFound = 7,

    ContactSuppressed = 8,
}
