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
/// <item><b>The send is booked, then queued.</b> The same rules are the <c>WHERE</c> of one conditional <c>UPDATE</c> that also counts the
/// send, and only the request whose update touched the row queues the message: requests that reach the same contact together (a script on the
/// public pages, a double click) find the send already booked and queue nothing, so the limits hold whatever the concurrency. When the queue
/// refuses the message the send is given back.</item>
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
    public static readonly TimeSpan MinimumInterval = TimeSpan.FromMinutes(10);

    private const string FallbackOrgName = "CasaZen";

    public async Task<MarketingConfirmationSendOutcome> SendConfirmationAsync(
        Guid orgId,
        Guid contactId,
        CancellationToken cancellationToken = default)
    {
        var contact = await db.GuestContacts.AsNoTracking().FirstOrDefaultAsync(c => c.OrgId == orgId && c.Id == contactId, cancellationToken);
        if (contact is null)
            return MarketingConfirmationSendOutcome.ContactNotFound;

        var now = timeProvider.GetUtcNow().UtcDateTime;
        if (RefusalOf(contact, now) is { } refusal)
            return refusal;

        string confirmUrl;
        MarketingConfirmationLink link;
        try
        {
            link = links.Create(contact.Id);
            confirmUrl = siteLinks.MarketingConfirm(link.SignedQuery);
        }
        catch (EmailConfigurationException ex)
        {
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

        if (!await BookSendAsync(contact, now, cancellationToken))
        {
            var current = await db.GuestContacts.AsNoTracking().FirstOrDefaultAsync(c => c.OrgId == orgId && c.Id == contactId, cancellationToken);
            return current is null
                ? MarketingConfirmationSendOutcome.ContactNotFound
                : RefusalOf(current, now) ?? MarketingConfirmationSendOutcome.TooSoon;
        }

        var queued = false;
        try
        {
            queued = emailQueue.Enqueue(contact.Value, content, EmailTemplates.Names.MarketingConfirmation);
        }
        finally
        {
            if (!queued)
                await GiveBackSendAsync(contact, CancellationToken.None);
        }

        if (!queued)
        {
            logger.LogWarning(
                "Marketing confirmation of contact {ContactId} of org {OrgId} not queued: the e-mail queue refused it", contact.Id, orgId);
            return MarketingConfirmationSendOutcome.Unavailable;
        }

        logger.LogInformation(
            "Marketing confirmation of contact {ContactId} of org {OrgId} queued (send {SendCount})",
            contact.Id,
            orgId,
            contact.ConfirmationSentCount + 1);
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

        var contact = await db.GuestContacts.FirstOrDefaultAsync(c => c.Id == contactId, cancellationToken);
        if (contact is null)
            return MarketingConfirmationOutcome.ContactNotFound;

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

    private MarketingConfirmationSendOutcome? RefusalOf(GuestContact contact, DateTime now)
    {
        if (contact.IsSuppressed)
            return MarketingConfirmationSendOutcome.Suppressed;

        if (!NeedsConfirmation(contact))
            return MarketingConfirmationSendOutcome.NotRequired;

        if (contact.ConfirmationSentCount >= options.Value.ConfirmationMaxSends)
            return MarketingConfirmationSendOutcome.LimitReached;

        if (contact.ConfirmationSentAt is { } sentAt && now - sentAt < MinimumInterval)
            return MarketingConfirmationSendOutcome.TooSoon;

        return null;
    }

    /// <summary>
    /// Counts one more send on the contact, once and only if the limits are still open in the database: the person is not suppressed nor
    /// confirmed, fewer than <c>Crm:ConfirmationMaxSends</c> messages were sent, and the last one went out at least ten minutes ago. One
    /// conditional <c>UPDATE</c>, so of the requests that arrive together exactly one gets <c>true</c>. Without a relational database (the
    /// in-memory store of the unit tests: no bulk update, no concurrent writer) the same rules are applied to the tracked row.
    /// </summary>
    private async Task<bool> BookSendAsync(GuestContact seen, DateTime now, CancellationToken cancellationToken)
    {
        var maxSends = options.Value.ConfirmationMaxSends;
        var latestSentAt = now - MinimumInterval;

        if (db.Database.IsRelational())
        {
            var booked = await db.GuestContacts
                .Where(c => c.OrgId == seen.OrgId
                    && c.Id == seen.Id
                    && !c.IsSuppressed
                    && c.Confirmation != GuestContactConfirmation.Confirmed
                    && c.ConfirmationSentCount < maxSends
                    && (c.ConfirmationSentAt == null || c.ConfirmationSentAt <= latestSentAt))
                .ExecuteUpdateAsync(
                    set => set
                        .SetProperty(c => c.Confirmation, GuestContactConfirmation.Pending)
                        .SetProperty(c => c.ConfirmationSentAt, (DateTime?)now)
                        .SetProperty(c => c.ConfirmationSentCount, c => c.ConfirmationSentCount + 1)
                        .SetProperty(c => c.UpdatedAt, now),
                    cancellationToken);
            if (booked == 0)
                return false;

            await ReloadTrackedAsync(seen.Id, cancellationToken);
            return true;
        }

        var contact = await db.GuestContacts.FirstOrDefaultAsync(c => c.OrgId == seen.OrgId && c.Id == seen.Id, cancellationToken);
        if (contact is null
            || contact.IsSuppressed
            || contact.Confirmation == GuestContactConfirmation.Confirmed
            || contact.ConfirmationSentCount >= maxSends
            || (contact.ConfirmationSentAt is { } sentAt && sentAt > latestSentAt))
        {
            return false;
        }

        contact.Confirmation = GuestContactConfirmation.Pending;
        contact.ConfirmationSentAt = now;
        contact.ConfirmationSentCount++;
        contact.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>
    /// Takes back the send <see cref="BookSendAsync"/> counted, because the message was not queued: the counter, the time of the last send and
    /// the confirmation go back to what <paramref name="before"/> (the contact as it was read) held. No other request can have booked a send
    /// in between: the one just booked closes the contact for ten minutes.
    /// </summary>
    private async Task GiveBackSendAsync(GuestContact before, CancellationToken cancellationToken)
    {
        if (db.Database.IsRelational())
        {
            await db.GuestContacts
                .Where(c => c.OrgId == before.OrgId && c.Id == before.Id && c.ConfirmationSentCount > 0)
                .ExecuteUpdateAsync(
                    set => set
                        .SetProperty(c => c.Confirmation, before.Confirmation)
                        .SetProperty(c => c.ConfirmationSentAt, before.ConfirmationSentAt)
                        .SetProperty(c => c.ConfirmationSentCount, c => c.ConfirmationSentCount - 1)
                        .SetProperty(c => c.UpdatedAt, before.UpdatedAt),
                    cancellationToken);
            await ReloadTrackedAsync(before.Id, cancellationToken);
            return;
        }

        var contact = await db.GuestContacts.FirstOrDefaultAsync(c => c.OrgId == before.OrgId && c.Id == before.Id, cancellationToken);
        if (contact is null || contact.ConfirmationSentCount == 0)
            return;

        contact.Confirmation = before.Confirmation;
        contact.ConfirmationSentAt = before.ConfirmationSentAt;
        contact.ConfirmationSentCount--;
        contact.UpdatedAt = before.UpdatedAt;
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// The conditional updates bypass the change tracker: a copy of the contact that this context tracks (the one a collection point has just
    /// recorded) is read again, so that no later save of the same unit of work writes the old counters back.
    /// </summary>
    private async Task ReloadTrackedAsync(Guid contactId, CancellationToken cancellationToken)
    {
        var entry = db.ChangeTracker.Entries<GuestContact>().FirstOrDefault(e => e.Entity.Id == contactId && e.State == EntityState.Unchanged);
        if (entry is not null)
            await entry.ReloadAsync(cancellationToken);
    }

    private static bool NeedsConfirmation(GuestContact contact) =>
        contact.Channel == GuestContactChannel.Email
        && contact.Origin != GuestContactOrigin.OtaForward
        && (contact.LegalBasis == GuestContactLegalBasis.Consent
            || (contact.Origin == GuestContactOrigin.Imported && contact.LegalBasis == GuestContactLegalBasis.SoftOptIn))
        && contact.Confirmation != GuestContactConfirmation.Confirmed;

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
