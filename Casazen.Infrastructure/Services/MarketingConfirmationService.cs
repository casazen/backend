using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Email.Templates;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IMarketingConfirmationService"/>
/// <remarks>
/// <list type="bullet">
/// <item><b>Rules before anything is queued</b>, in this order: the contact exists in the org; it is not suppressed; it is an e-mail address
/// with a consent that is not confirmed yet; fewer than <c>Crm:ConfirmationMaxSends</c> messages were sent; the last one was sent ten minutes
/// ago or more. A message that could not be queued is not counted.</item>
/// <item><b>The e-mail</b> is rendered in the language of the contact (<see cref="EmailTemplates.CultureOf"/>: English for <c>en</c>, Italian
/// for everything else) with the public name of the org, and queued through <see cref="IEmailQueue"/> (Hangfire), never sent inside the
/// request.</item>
/// <item><b>Tenant</b>: <see cref="SendConfirmationAsync"/> names the org and finds the contact only in it. <see cref="ConfirmAsync"/> finds the
/// contact by the id the signed link names: the signature is the proof that CasaZen made the link for that contact.</item>
/// <item><b>Privacy</b>: no address, no name and no hash is written to a log; the logs carry the ids of the org and of the contact.</item>
/// </list>
/// </remarks>
public class MarketingConfirmationService(
    AppDbContext db,
    MarketingConfirmationLinks links,
    PublicSiteLinks siteLinks,
    IEmailQueue emailQueue,
    ICrmContactService contacts,
    IOptions<CrmOptions> options,
    TimeProvider timeProvider,
    ILogger<MarketingConfirmationService> logger) : IMarketingConfirmationService
{
    /// <summary>The shortest time between two confirmation messages to the same contact: a repeat inside it sends nothing.</summary>
    public static readonly TimeSpan MinimumInterval = TimeSpan.FromMinutes(10);

    /// <summary>The name the e-mail gives the sender when the org has no public name.</summary>
    private const string FallbackOrgName = "CasaZen";

    public async Task<MarketingConfirmationSendOutcome> SendConfirmationAsync(
        Guid orgId,
        Guid contactId,
        CancellationToken cancellationToken = default)
    {
        var contact = await db.GuestContacts.FirstOrDefaultAsync(c => c.OrgId == orgId && c.Id == contactId, cancellationToken);
        if (contact is null)
            return MarketingConfirmationSendOutcome.ContactNotFound;

        // The person asked not to be written to: nothing goes out, whatever consent was recorded on the contact afterwards.
        if (contact.IsSuppressed)
            return MarketingConfirmationSendOutcome.Suppressed;

        if (!NeedsConfirmation(contact))
            return MarketingConfirmationSendOutcome.NotRequired;

        if (contact.ConfirmationSentCount >= options.Value.ConfirmationMaxSends)
            return MarketingConfirmationSendOutcome.LimitReached;

        var now = timeProvider.GetUtcNow().UtcDateTime;
        if (contact.ConfirmationSentAt is { } sentAt && now - sentAt < MinimumInterval)
            return MarketingConfirmationSendOutcome.TooSoon;

        string confirmUrl;
        MarketingConfirmationLink link;
        try
        {
            link = links.Create(contact.Id);
            confirmUrl = siteLinks.MarketingConfirm(link.SignedQuery);
        }
        catch (EmailConfigurationException ex)
        {
            // The message names the variable that is missing, never the contact.
            logger.LogError(
                "Marketing confirmation of contact {ContactId} of org {OrgId} not sent: {Problem}", contact.Id, orgId, ex.Message);
            return MarketingConfirmationSendOutcome.Unavailable;
        }

        var org = await db.Orgs.AsNoTracking()
            .Where(o => o.Id == orgId)
            .Select(o => new { o.DisplayName, o.Name })
            .FirstOrDefaultAsync(cancellationToken);
        var orgName = org is null ? null : !string.IsNullOrWhiteSpace(org.DisplayName) ? org.DisplayName : org.Name;
        var content = EmailTemplates.MarketingConfirmation(
            EmailTemplates.CultureOf(contact.Language),
            string.IsNullOrWhiteSpace(orgName) ? FallbackOrgName : orgName.Trim(),
            confirmUrl,
            link.ExpiresAtUtc);

        // Queued before it is counted: a message the queue refused (no e-mail provider configured) must not use up one of the sends.
        if (!emailQueue.Enqueue(contact.Value, content, EmailTemplates.Names.MarketingConfirmation))
        {
            logger.LogWarning(
                "Marketing confirmation of contact {ContactId} of org {OrgId} not queued: the e-mail queue refused it", contact.Id, orgId);
            return MarketingConfirmationSendOutcome.Unavailable;
        }

        contact.Confirmation = GuestContactConfirmation.Pending;
        contact.ConfirmationSentAt = now;
        contact.ConfirmationSentCount++;
        contact.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Marketing confirmation of contact {ContactId} of org {OrgId} queued (send {SendCount})",
            contact.Id,
            orgId,
            contact.ConfirmationSentCount);
        return MarketingConfirmationSendOutcome.Queued;
    }

    public async Task<MarketingConfirmationOutcome> ConfirmAsync(
        Guid contactId,
        string expiresAt,
        string signature,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expiresAt);
        ArgumentNullException.ThrowIfNull(signature);

        switch (links.Verify(contactId, expiresAt, signature))
        {
            case MarketingConfirmationCheck.Valid:
                break;
            case MarketingConfirmationCheck.InvalidTimestamp:
                return MarketingConfirmationOutcome.InvalidTimestamp;
            case MarketingConfirmationCheck.Expired:
                return MarketingConfirmationOutcome.LinkExpired;
            case MarketingConfirmationCheck.NotConfigured:
                return MarketingConfirmationOutcome.NotConfigured;
            default:
                return MarketingConfirmationOutcome.InvalidSignature;
        }

        // Only now, with a valid signature, the contact is looked for: a forged link learns nothing about which ids exist.
        var contact = await db.GuestContacts.FirstOrDefaultAsync(c => c.Id == contactId, cancellationToken);
        if (contact is null)
            return MarketingConfirmationOutcome.ContactNotFound;

        // A person who opted out is not brought back by an old link, and the click is not recorded as a confirmation.
        if (contact.IsSuppressed)
            return MarketingConfirmationOutcome.ContactSuppressed;

        var alreadyConfirmed = contact.Confirmation == GuestContactConfirmation.Confirmed;
        if (!alreadyConfirmed)
        {
            var confirmed = await contacts.MarkConfirmedAsync(contact.OrgId, contact.Id, cancellationToken);
            if (confirmed is null)
                return MarketingConfirmationOutcome.ContactNotFound;

            logger.LogInformation("Marketing consent of contact {ContactId} of org {OrgId} confirmed (double opt-in)", contact.Id, contact.OrgId);
        }

        await ConfirmLegacyRowsAsync(contact, cancellationToken);
        return alreadyConfirmed ? MarketingConfirmationOutcome.AlreadyConfirmed : MarketingConfirmationOutcome.Confirmed;
    }

    /// <summary>
    /// A contact is sent a confirmation when it is an e-mail address, not an OTA forward, whose basis is the person's consent, and the person
    /// did not confirm yet: the only case in which the confirmation changes whether the address can be written to.
    /// </summary>
    private static bool NeedsConfirmation(GuestContact contact) =>
        contact.Channel == GuestContactChannel.Email
        && contact.Origin != GuestContactOrigin.OtaForward
        && contact.LegalBasis == GuestContactLegalBasis.Consent
        && contact.Confirmation != GuestContactConfirmation.Confirmed;

    /// <summary>
    /// The per-channel consent that the online check-in recorded before the contacts existed (<see cref="MarketingConsentRecord"/>) holds its own
    /// confirmation flag, which the profile of the guest reads: the rows of the same org and guest with the same address, normalized, are
    /// confirmed together with the contact.
    /// </summary>
    private async Task ConfirmLegacyRowsAsync(GuestContact contact, CancellationToken cancellationToken)
    {
        if (contact.Channel != GuestContactChannel.Email || contact.GuestId is not { } guestId)
            return;

        var address = CrmNormalizer.NormalizeEmail(contact.Value);
        if (address.Length == 0)
            return;

        var rows = await db.MarketingConsentRecords
            .Where(r => r.OrgId == contact.OrgId
                && r.GuestId == guestId
                && r.ConsentEmail
                && !r.EmailConfirmed
                && r.MarketingEmail.ToLower() == address)
            .ToListAsync(cancellationToken);
        if (rows.Count == 0)
            return;

        foreach (var row in rows)
            row.EmailConfirmed = true;

        await db.SaveChangesAsync(cancellationToken);
    }
}
