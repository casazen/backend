using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="ICrmContactService"/>
/// <remarks>
/// <list type="bullet">
/// <item><b>One contact for each org, channel and value</b>: found by the keyed hash of the normalized value, never by the stored value (which
/// is encrypted at rest). Two writers racing to make the same contact end with one: the database refuses the second row and the service
/// stages its change again on top of the first.</item>
/// <item><b>What a contact may be used for</b> is <see cref="CrmContactRules"/>: this class records the facts the rules read. The basis only
/// goes up (<see cref="CrmContactRules.Stronger"/>), except in <see cref="WithdrawAsync"/>; an OTA forward stays what it was; a suppression is
/// never cleared and an address already on the suppression list is flagged.</item>
/// <item><b>Tenant</b>: every query names the org, on top of the tenant filter of the scope the service runs in.</item>
/// <item><b>Privacy</b>: reads the booker's columns it needs (name, e-mail, phone, consent flag), the consents of the booker and the latest
/// booking, never <see cref="StayGuest"/>, a document or an Alloggiati field. The logs carry ids, never an address, a number or a hash.</item>
/// </list>
/// </remarks>
public class CrmContactService(
    AppDbContext db,
    ICrmContactIndex contactIndex,
    ISuppressionService suppression,
    IOptions<CrmOptions> options,
    TimeProvider timeProvider,
    ILogger<CrmContactService> logger) : ICrmContactService
{
    /// <summary>The unique index of <c>GuestContacts</c> on the org, the channel and the keyed hash.</summary>
    private const string ContactIndexName = "UIX_GuestContacts_OrgId_Channel_ValueHash";

    private const int NoteMaxLength = 500;

    public async Task<GuestContact?> UpsertContactAsync(CrmContactInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        return await StageAndSaveAsync(() => StageAsync(input, null, cancellationToken), cancellationToken);
    }

    public async Task<GuestContact?> RecordConsentAsync(CrmConsentInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (string.IsNullOrWhiteSpace(input.Version))
            throw new ArgumentException("A consent is recorded with the version of its text.", nameof(input));

        var recordedAt = input.RecordedAt ?? Now();
        var evidence = new CrmBasisEvidence(Truncate(input.Version, GuestContact.BasisVersionMaxLength), recordedAt, input.Source, TruncateOrNull(input.IpAddress, GuestContact.ConsentIpMaxLength));
        var contactInput = new CrmContactInput(
            input.OrgId,
            input.Channel,
            input.Value,
            input.Origin,
            input.GuestCrmProfileId,
            input.GuestId,
            input.DisplayName,
            input.Language,
            input.BookedVia);

        // The history row is staged once: a second attempt after a conflict must not add it again.
        var historyStaged = false;
        return await StageAndSaveAsync(
            async () =>
            {
                var contact = await StageAsync(contactInput, new ConsentToApply(evidence, null, Fresh: true), cancellationToken);
                if (contact is not null && input.RecordHistory && input.GuestId is { } guestId && !historyStaged)
                {
                    db.GuestConsentRecords.Add(new GuestConsentRecord
                    {
                        OrgId = input.OrgId,
                        GuestId = guestId,
                        Purpose = GuestConsentPurpose.Marketing,
                        Action = GuestConsentAction.Granted,
                        Version = evidence.Version,
                        Source = input.Source,
                        IpAddress = evidence.IpAddress,
                        Note = GuestConsentChannelNote.For(input.Channel),
                        RecordedAt = recordedAt,
                    });
                    historyStaged = true;
                }

                return contact;
            },
            cancellationToken);
    }

    public async Task<GuestContact?> MarkConfirmedAsync(Guid orgId, Guid contactId, CancellationToken cancellationToken = default)
    {
        var contact = await db.GuestContacts.FirstOrDefaultAsync(c => c.OrgId == orgId && c.Id == contactId, cancellationToken);
        if (contact is null)
            return null;

        var now = Now();
        if (ApplyConfirmed(contact, now))
        {
            contact.UpdatedAt = now;
            await db.SaveChangesAsync(cancellationToken);
        }

        return contact;
    }

    public async Task<GuestContact?> WithdrawAsync(
        Guid orgId,
        Guid contactId,
        string source,
        GuestConsentSource consentSource,
        string? recordedByUserId = null,
        string? note = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);

        var contact = await db.GuestContacts.FirstOrDefaultAsync(c => c.OrgId == orgId && c.Id == contactId, cancellationToken);
        if (contact is null)
            return null;

        var now = Now();
        var suppressionSource = Truncate(source, GuestContact.SuppressedSourceMaxLength);
        var alreadySuppressed = contact.IsSuppressed;

        // The address goes on the suppression list too (the second barrier of every campaign), in the same unit of work as the contact.
        if (contact.Channel == GuestContactChannel.Email)
            await suppression.StageSuppressionAsync(orgId, contact.Value, suppressionSource, cancellationToken);

        if (!contact.IsSuppressed)
        {
            contact.IsSuppressed = true;
            contact.SuppressedAt = now;
            contact.SuppressedSource = suppressionSource;
        }

        // Back to what the stay needs: a marketing opt-out does not stop the messages of a stay. A contact with no basis stays without one.
        if (contact.LegalBasis > GuestContactLegalBasis.ServiceOnly)
            contact.LegalBasis = GuestContactLegalBasis.ServiceOnly;

        contact.UpdatedAt = now;

        if (contact.GuestId is { } guestId && !alreadySuppressed)
        {
            db.GuestConsentRecords.Add(new GuestConsentRecord
            {
                OrgId = orgId,
                GuestId = guestId,
                Purpose = GuestConsentPurpose.Marketing,
                Action = GuestConsentAction.Withdrawn,
                Version = Truncate(contact.BasisVersion, GuestContact.BasisVersionMaxLength),
                Source = consentSource,
                IpAddress = null,
                Note = TruncateOrNull(note, NoteMaxLength),
                RecordedByUserId = TruncateOrNull(recordedByUserId, 200),
                RecordedAt = now,
            });
        }

        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("CRM contact {ContactId} of org {OrgId} withdrawn ({Source})", contact.Id, orgId, suppressionSource);
        return contact;
    }

    public async Task<IReadOnlyList<GuestContact>> SyncFromGuestAsync(Guid guestId, CancellationToken cancellationToken = default)
    {
        // Only the columns the contacts are built from: no document, no place of birth, no other Alloggiati field is read.
        var guest = await db.Guests
            .AsNoTracking()
            .Where(g => g.Id == guestId)
            .Select(g => new
            {
                g.Id,
                g.OrgId,
                g.FirstName,
                g.LastName,
                g.Email,
                g.PhoneNumber,
                g.GuestCrmProfileId,
                g.MarketingConsent,
                g.MarketingConsentDate,
                g.CreatedAt,
                Erased = g.DataAnonymizedDate != null || g.IsDeleted || g.ErasureRequested,
            })
            .FirstOrDefaultAsync(cancellationToken);
        if (guest is null || guest.Erased)
            return [];

        var orgId = guest.OrgId;
        var latest = await db.Bookings
            .AsNoTracking()
            .Where(b => b.OrgId == orgId && b.GuestId == guestId)
            .OrderByDescending(b => b.CreatedAt)
            .ThenByDescending(b => b.Id)
            .Select(b => new { b.Source, b.Status })
            .FirstOrDefaultAsync(cancellationToken);

        // The soft opt-in notice the guest saw (art. 130 c. 4 Codice privacy): only a recorded one, with its version, is evidence.
        var noticeRow = await db.GuestConsentRecords
            .AsNoTracking()
            .Where(r => r.OrgId == orgId && r.GuestId == guestId && r.Purpose == GuestConsentPurpose.SoftOptInNotice && r.Version != string.Empty)
            .OrderByDescending(r => r.RecordedAt)
            .Select(r => new { r.Version, r.RecordedAt, r.Source, r.IpAddress })
            .FirstOrDefaultAsync(cancellationToken);
        var notice = noticeRow is null ? null : new CrmBasisEvidence(noticeRow.Version, noticeRow.RecordedAt, noticeRow.Source, noticeRow.IpAddress);

        // The single yes/no flag of the check-in and of the checkout before the per-channel consents: a consent, its details from the history.
        CrmBasisEvidence? legacyConsent = null;
        if (guest.MarketingConsent)
        {
            var grantedRow = await db.GuestConsentRecords
                .AsNoTracking()
                .Where(r => r.OrgId == orgId
                    && r.GuestId == guestId
                    && r.Purpose == GuestConsentPurpose.Marketing
                    && r.Action == GuestConsentAction.Granted)
                .OrderByDescending(r => r.RecordedAt)
                .Select(r => new { r.Version, r.RecordedAt, r.Source, r.IpAddress })
                .FirstOrDefaultAsync(cancellationToken);
            legacyConsent = grantedRow is null
                ? new CrmBasisEvidence(string.Empty, guest.MarketingConsentDate ?? guest.CreatedAt)
                : new CrmBasisEvidence(grantedRow.Version, grantedRow.RecordedAt, grantedRow.Source, grantedRow.IpAddress);
        }

        var consentRecords = await db.MarketingConsentRecords
            .AsNoTracking()
            .Where(r => r.OrgId == orgId && r.GuestId == guestId)
            .OrderBy(r => r.ConsentTimestamp)
            .ThenBy(r => r.Id)
            .Select(r => new { r.MarketingEmail, r.ConsentEmail, r.ConsentWhatsApp, r.EmailConfirmed, r.ConsentTimestamp, r.ConsentTextVersion, r.IpAddress })
            .ToListAsync(cancellationToken);

        return await StageAndSaveAsync(
            async () =>
            {
                var contacts = new List<GuestContact>();
                void Keep(GuestContact? contact)
                {
                    if (contact is not null && !contacts.Contains(contact))
                        contacts.Add(contact);
                }

                var settings = options.Value;
                var now = Now();
                var source = latest?.Source;
                var displayName = $"{guest.FirstName} {guest.LastName}".Trim();
                var purchase = latest is not null && CrmContactRules.IsPurchase(latest.Status);
                var phone = CrmNormalizer.NormalizePhone(guest.PhoneNumber, settings.DefaultCallingCode);

                // 1. The e-mail address of the guest record. The origin is where the address came from: the booking.
                if (CrmNormalizer.TryNormalizeEmail(guest.Email, out var email))
                {
                    var isForward = OtaForwardAddress.IsForward(email, source, settings.OtaForwardDomains);
                    var origin = CrmContactRules.OriginOfBooking(source, isForward, GuestContactChannel.Email);
                    var input = new CrmContactInput(
                        orgId,
                        GuestContactChannel.Email,
                        email,
                        origin,
                        guest.GuestCrmProfileId,
                        guest.Id,
                        displayName,
                        null,
                        source,
                        notice,
                        purchase);
                    var consent = legacyConsent is null ? null : new ConsentToApply(legacyConsent, null, Fresh: false);
                    Keep(await StageAsync(input, consent, cancellationToken));
                }

                // 2. The phone number of the guest record: service only, as every phone number a booking brings.
                if (phone is not null)
                {
                    var origin = CrmContactRules.OriginOfBooking(source, false, GuestContactChannel.Phone);
                    var input = new CrmContactInput(
                        orgId,
                        GuestContactChannel.Phone,
                        phone,
                        origin,
                        guest.GuestCrmProfileId,
                        guest.Id,
                        displayName,
                        null,
                        source);
                    Keep(await StageAsync(input, null, cancellationToken));
                }

                // 3. The per-channel consents of the check-in. The e-mail is a consent, confirmed only when the guest confirmed it; the time of
                // the confirmation is not in the record (a flag), so it is the time the sync learned of it. WhatsApp is a consent on the phone.
                // The marketing e-mail is typed at the check-in, so it does not arrive with the booking: only the domain can make it a forward.
                foreach (var record in consentRecords)
                {
                    var evidence = new CrmBasisEvidence(record.ConsentTextVersion, record.ConsentTimestamp, GuestConsentSource.GuestPortal, record.IpAddress);
                    if (record.ConsentEmail && CrmNormalizer.TryNormalizeEmail(record.MarketingEmail, out var marketingEmail))
                    {
                        var input = new CrmContactInput(
                            orgId,
                            GuestContactChannel.Email,
                            marketingEmail,
                            GuestContactOrigin.CheckIn,
                            guest.GuestCrmProfileId,
                            guest.Id,
                            displayName);
                        Keep(await StageAsync(input, new ConsentToApply(evidence, record.EmailConfirmed ? now : null, Fresh: false), cancellationToken));
                    }

                    if (record.ConsentWhatsApp && phone is not null)
                    {
                        var input = new CrmContactInput(
                            orgId,
                            GuestContactChannel.WhatsApp,
                            phone,
                            GuestContactOrigin.CheckIn,
                            guest.GuestCrmProfileId,
                            guest.Id,
                            displayName);
                        Keep(await StageAsync(input, new ConsentToApply(evidence, null, Fresh: false), cancellationToken));
                    }
                }

                return contacts;
            },
            cancellationToken);
    }

    // ---- staging ------------------------------------------------------------------------------------------------------------------

    /// <summary>A consent to put on a contact: what it rests on, when the person confirmed it, and whether it is a new act of the person.</summary>
    /// <param name="Evidence">Version, time, source and address of the consent.</param>
    /// <param name="ConfirmedAt">When the person confirmed the address (double opt-in), <c>null</c> when they did not.</param>
    /// <param name="Fresh">True for a consent the person has just given (it must be confirmed again, unless it renews a confirmed one); false for a consent read from what the check-in recorded before.</param>
    private sealed record ConsentToApply(CrmBasisEvidence Evidence, DateTime? ConfirmedAt, bool Fresh);

    /// <summary>
    /// Stages the change, saves it and, when a second writer made the same contact at the same moment (the unique index refused the row),
    /// stages it again on top of theirs and saves once more. Inside a transaction of the caller the conflict is not caught: the transaction
    /// is aborted by the database and only the caller can end it.
    /// </summary>
    private async Task<T> StageAndSaveAsync<T>(Func<Task<T>> stage, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            var result = await stage();
            try
            {
                await db.SaveChangesAsync(cancellationToken);
                return result;
            }
            catch (DbUpdateException ex) when (attempt == 0
                && CrmDatabaseErrors.IsUniqueViolation(ex, ContactIndexName)
                && db.Database.CurrentTransaction is null)
            {
                foreach (var entry in db.ChangeTracker.Entries<GuestContact>().Where(e => e.State == EntityState.Added).ToList())
                    entry.State = EntityState.Detached;

                logger.LogInformation("A CRM contact was made by another writer at the same moment: the change is applied on top of it");
            }
        }
    }

    /// <summary>
    /// Creates or changes the contact in the unit of work, without saving, and returns it tracked; <c>null</c> when the value cannot be stored.
    /// </summary>
    private async Task<GuestContact?> StageAsync(CrmContactInput input, ConsentToApply? consent, CancellationToken cancellationToken)
    {
        if (input.OrgId == Guid.Empty)
            throw new ArgumentException("A contact needs an org.", nameof(input));

        var value = NormalizeValue(input.Channel, input.Value);
        if (value is null)
            return null;

        var settings = options.Value;
        var now = Now();
        var hash = contactIndex.Hash(value);
        var isForward = input.Channel == GuestContactChannel.Email
            && OtaForwardAddress.IsForward(value, input.BookedVia, settings.OtaForwardDomains);

        var contact = await FindAsync(input.OrgId, input.Channel, hash, cancellationToken);
        var created = contact is null;
        if (contact is null)
        {
            contact = new GuestContact
            {
                OrgId = input.OrgId,
                Channel = input.Channel,
                Value = value,
                ValueHash = hash,
                Origin = isForward ? GuestContactOrigin.OtaForward : input.Origin,
                Confirmation = GuestContactConfirmation.Unconfirmed,
                LegalBasis = GuestContactLegalBasis.None,
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.GuestContacts.Add(contact);
        }

        var changed = FillIn(contact, input);

        // An address seen later as a forward (its domain was added to the list) is one, unless somebody already stands behind it.
        if (isForward && contact.Origin != GuestContactOrigin.OtaForward && contact.LegalBasis <= GuestContactLegalBasis.ServiceOnly)
        {
            contact.Origin = GuestContactOrigin.OtaForward;
            changed = true;
        }

        // An opt-out made before the contact existed, or before this address was a contact, flags it: nothing re-enables it afterwards.
        if (input.Channel == GuestContactChannel.Email
            && !contact.IsSuppressed
            && await suppression.IsSuppressedAsync(input.OrgId, value, cancellationToken))
        {
            contact.IsSuppressed = true;
            contact.SuppressedAt = now;
            contact.SuppressedSource = CrmContactRules.SuppressionSourceList;
            changed = true;
        }

        if (contact.Origin == GuestContactOrigin.OtaForward)
        {
            // Service only, and nothing raises it: a consent on it is in the history, and the address is not marketable.
            changed |= ApplyBasis(contact, GuestContactLegalBasis.ServiceOnly, null, now);
        }
        else
        {
            var noticeShown = input.Notice is not null && !string.IsNullOrWhiteSpace(input.Notice.Version);
            if (input.Origin == GuestContactOrigin.OctorateContactInfo && !settings.BookingEngineSoftOptIn)
                noticeShown = false;

            // A person who objected is not raised above what a stay needs by a booking.
            var basis = CrmContactRules.ResolveBasis(input.Origin, noticeShown, input.PurchaseConfirmed, input.Channel);
            if (contact.IsSuppressed && basis > GuestContactLegalBasis.ServiceOnly)
                basis = GuestContactLegalBasis.ServiceOnly;

            changed |= ApplyBasis(contact, basis, basis == GuestContactLegalBasis.SoftOptIn ? input.Notice : null, now);

            // A consent read from what the check-in recorded before cannot undo a withdrawal. A consent the person gives now is stored, and the
            // contact stays suppressed.
            if (consent is not null && (consent.Fresh || !contact.IsSuppressed))
                changed |= ApplyConsent(contact, consent, now);
        }

        if (changed && !created)
            contact.UpdatedAt = now;

        return contact;
    }

    /// <summary>The contact in the unit of work (made and not saved yet) or in the database.</summary>
    private async Task<GuestContact?> FindAsync(Guid orgId, GuestContactChannel channel, string hash, CancellationToken cancellationToken)
    {
        var staged = db.GuestContacts.Local.FirstOrDefault(c => c.OrgId == orgId && c.Channel == channel && c.ValueHash == hash);
        return staged ?? await db.GuestContacts.FirstOrDefaultAsync(
            c => c.OrgId == orgId && c.Channel == channel && c.ValueHash == hash,
            cancellationToken);
    }

    /// <summary>The value in the form the index hashes and the contact stores: a plain e-mail address, or an E.164 number. <c>null</c> if it is neither.</summary>
    private string? NormalizeValue(GuestContactChannel channel, string? value) =>
        channel == GuestContactChannel.Email
            ? CrmNormalizer.TryNormalizeEmail(value, out var email) ? email : null
            : CrmNormalizer.NormalizePhone(value, options.Value.DefaultCallingCode);

    /// <summary>Fills what the contact has not got and the input gives: profile, guest, display name; sets the language of the latest page. True if something changed.</summary>
    private static bool FillIn(GuestContact contact, CrmContactInput input)
    {
        var changed = false;
        if (contact.GuestCrmProfileId is null && input.GuestCrmProfileId is { } profileId)
        {
            contact.GuestCrmProfileId = profileId;
            changed = true;
        }

        if (contact.GuestId is null && input.GuestId is { } guestId)
        {
            contact.GuestId = guestId;
            changed = true;
        }

        if (string.IsNullOrWhiteSpace(contact.DisplayName) && !string.IsNullOrWhiteSpace(input.DisplayName))
        {
            contact.DisplayName = input.DisplayName.Trim();
            changed = true;
        }

        var language = NormalizeLanguage(input.Language);
        if (language.Length > 0 && !string.Equals(contact.Language, language, StringComparison.Ordinal))
        {
            contact.Language = language;
            changed = true;
        }

        return changed;
    }

    /// <summary>
    /// Raises the basis of the contact to <paramref name="basis"/> with its evidence, never lowers it. The same basis again replaces the evidence
    /// only for a notice or a consent, and only with newer evidence. True if the contact changed.
    /// </summary>
    private static bool ApplyBasis(GuestContact contact, GuestContactLegalBasis basis, CrmBasisEvidence? evidence, DateTime now)
    {
        if (basis == GuestContactLegalBasis.None || basis < contact.LegalBasis)
            return false;

        var recordedAt = evidence?.RecordedAt ?? now;
        if (basis == contact.LegalBasis
            && (basis < GuestContactLegalBasis.SoftOptIn
                || evidence is null
                || (contact.BasisRecordedAt is { } recorded && recordedAt <= recorded)))
        {
            return false;
        }

        contact.LegalBasis = basis;
        contact.BasisVersion = Truncate(evidence?.Version, GuestContact.BasisVersionMaxLength);
        contact.BasisRecordedAt = recordedAt;
        contact.ConsentSource = evidence?.Source;
        contact.ConsentIpAddress = TruncateOrNull(evidence?.IpAddress, GuestContact.ConsentIpMaxLength);
        return true;
    }

    /// <summary>
    /// Puts a consent on the contact. A new consent of the person (<see cref="ConsentToApply.Fresh"/>) starts unconfirmed, unless it renews a
    /// consent that was confirmed; a confirmation the check-in recorded is read as confirmed. <b>A consent the person confirmed keeps its
    /// evidence</b> (the version of the text, the time, the source and the address of the connection of the act they confirmed): a later consent
    /// that is not confirmed does not replace it, because anybody can type an address on a public page and the proof of a double opt-in is worth
    /// what the person confirmed. A consent that is not confirmed yet has nothing to protect: the newer act takes its place.
    /// </summary>
    private static bool ApplyConsent(GuestContact contact, ConsentToApply consent, DateTime now)
    {
        var wasConsent = contact.LegalBasis == GuestContactLegalBasis.Consent;
        var keepsConfirmedEvidence = wasConsent
            && contact.Confirmation == GuestContactConfirmation.Confirmed
            && consent.ConfirmedAt is null;
        var changed = !keepsConfirmedEvidence && ApplyBasis(contact, GuestContactLegalBasis.Consent, consent.Evidence, now);

        if (consent.Fresh
            && !wasConsent
            && (contact.Confirmation != GuestContactConfirmation.Unconfirmed || contact.ConfirmedAt is not null))
        {
            contact.Confirmation = GuestContactConfirmation.Unconfirmed;
            contact.ConfirmedAt = null;
            changed = true;
        }

        if (consent.ConfirmedAt is { } confirmedAt)
            changed |= ApplyConfirmed(contact, confirmedAt);

        return changed;
    }

    /// <summary>Records the double opt-in: the one place that sets <see cref="GuestContactConfirmation.Confirmed"/>. Never takes it back. True if it changed.</summary>
    private static bool ApplyConfirmed(GuestContact contact, DateTime at)
    {
        if (contact.Confirmation == GuestContactConfirmation.Confirmed)
            return false;

        contact.Confirmation = GuestContactConfirmation.Confirmed;
        contact.ConfirmedAt = at;
        return true;
    }

    // ---- small helpers ------------------------------------------------------------------------------------------------------------

    private DateTime Now() => timeProvider.GetUtcNow().UtcDateTime;

    private static string NormalizeLanguage(string? language) =>
        Truncate(language, GuestContact.LanguageMaxLength).ToLowerInvariant();

    private static string Truncate(string? value, int maxLength)
    {
        var text = value?.Trim() ?? string.Empty;
        return text.Length <= maxLength ? text : text[..maxLength];
    }

    private static string? TruncateOrNull(string? value, int maxLength)
    {
        var text = Truncate(value, maxLength);
        return text.Length == 0 ? null : text;
    }
}
