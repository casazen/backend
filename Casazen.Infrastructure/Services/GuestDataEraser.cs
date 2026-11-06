using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// Removes the personal data of guests (CO-15, A5-12, A9-18): the one place that knows which fields are personal, used
/// by the erasure, the anonymization and the retention job. Stages the changes on the context without saving; the only
/// immediate effect is the deletion of the document scan objects in the private storage, done <b>before</b> the caller
/// saves, so a failure of the storage leaves the reference in place and the operation can be retried.
/// </summary>
/// <remarks>
/// What stays after a full anonymization, and why (docs/runbooks/gdpr.md): the guest row with placeholders (bookings and
/// Alloggiati reports keep a foreign key to it); bookings with dates, amounts and payments (fiscal and accounting records,
/// <c>.claude/context/regulations/gdpr.md</c> § 5); the kind and position of the guests of each stay; the consent events
/// without IP and note, and the per-channel marketing consents without e-mail and IP (proof of consent, art. 7.1 GDPR); the
/// Alloggiati communication status and receipt reference; of the CRM (E07/F06/US02/T01), only the keyed hash of a contact that
/// carries a suppression, with the flag and its date (the opt-out survives like the entry of the suppression list), and the
/// profile that other guests are still linked to, without its keyed hashes, tags and country.
/// </remarks>
public sealed class GuestDataEraser(AppDbContext db, IFileStorage fileStorage, ILogger<GuestDataEraser> logger)
{
    /// <summary>Placeholder of the names of an anonymized guest.</summary>
    public const string AnonymizedName = "ANONYMIZED";

    /// <summary>Prefix of the private storage keys of the document scans (<see cref="StorageKeys.GuestDocument"/>).</summary>
    private const string GuestDocumentKeyPrefix = "guest-documents/";

    private static readonly GuestCheckInSessionStatus[] OpenSessionStatuses =
        [GuestCheckInSessionStatus.Inviato, GuestCheckInSessionStatus.InCompilazione];

    /// <summary>The anonymized e-mail of a guest: unique per record, never deliverable.</summary>
    public static string AnonymizedEmail(Guid guestId) => $"ANON-{guestId:N}@deleted.local";

    /// <summary>
    /// Full anonymization of <paramref name="guest"/>: every personal field, its document scan, the guests of its stays,
    /// the IP and note of its consent events, the marketing e-mail and IP of its per-channel marketing consents (the consent
    /// ends), the special requests of its bookings, the name shown on its reviews (DB-12a: the
    /// mark and the text stay, anonymous); open check-in links of its bookings expire; and what the CRM holds of the guest
    /// (<see cref="EraseCrmDataAsync"/>), in the same unit of work. <see cref="GuestErasureOutcome.Changed"/> is false when there was nothing left to remove (idempotent).
    /// </summary>
    public async Task<GuestErasureOutcome> AnonymizeAsync(Guest guest, DateTime now, CancellationToken cancellationToken)
    {
        var before = Fingerprint(guest);
        var filesDeleted = await DeleteDocumentScanAsync(guest, cancellationToken);
        EraseAlloggiatiFields(guest);
        EraseIdentity(guest);
        guest.MarketingConsent = false;

        var stayGuests = await AnonymizeStayGuestsOfBookerAsync(guest.Id, now, cancellationToken);
        var consentEvidence = await ClearConsentEvidenceAsync(guest.Id, cancellationToken);
        var marketingConsents = await ClearMarketingConsentRecordsAsync(guest.Id, cancellationToken);
        var specialRequests = await ClearSpecialRequestsAsync(guest.Id, now, cancellationToken);
        var reviews = await AnonymizeReviewsAsync(guest.Id, cancellationToken);
        await ExpireOpenCheckInLinksAsync(guest.Id, now, cancellationToken);
        var threads = await DeleteMessageThreadsAsync(guest.Id, cancellationToken);
        var crm = await EraseCrmDataAsync(guest, now, cancellationToken);

        var changed = Fingerprint(guest) != before
            || filesDeleted > 0 || stayGuests > 0 || consentEvidence > 0 || marketingConsents > 0 || specialRequests > 0
            || reviews > 0 || threads > 0 || crm.Changed
            || guest.DataAnonymizedDate is null;
        guest.AlloggiatiDataErasedAt ??= now;
        guest.DataAnonymizedDate ??= now;
        if (changed)
            guest.UpdatedAt = now;

        return new GuestErasureOutcome(changed, stayGuests, filesDeleted, crm.ContactsRemoved, crm.OptOutsKept, crm.ProfileRemoved);
    }

    /// <summary>
    /// What the CRM holds of <paramref name="guest"/> (E07/F06/US02/T01, NF-95, NF-20), staged on the context without saving, in the unit of work
    /// of the caller:
    /// <list type="bullet">
    /// <item>the contacts the guest gave (<see cref="GuestContact.GuestId"/>) are deleted, and with them, when the profile is the guest's alone,
    /// every contact of that profile; <b>a contact that carries a suppression is not deleted</b>: it stays as a row with no value, no name and no
    /// IP, only its keyed hash, the flag and its date (<see cref="StripToOptOutMarker"/>), so the opt-out survives as the entry of the suppression
    /// list does and a returning address is flagged again;</item>
    /// <item>the guest is unlinked from its profile; the profile is deleted when no other guest of the org is linked to it, with the proposals of
    /// merge that involve it; otherwise it is <b>never deleted</b> (a profile of several guests is not the erasure of one) and loses its keyed
    /// hashes, its tags and its country, which may be the erased guest's;</item>
    /// <item>the rows of the campaigns that name a contact or a profile keep the row and lose the person (their references become null by the
    /// foreign keys of the campaign model, which no feature of this plan has yet).</item>
    /// </list>
    /// Idempotent: once the guest is unlinked and its contacts are gone or stripped, a second run finds nothing. Nothing is logged but counts.
    /// </summary>
    public async Task<CrmErasureOutcome> EraseCrmDataAsync(Guest guest, DateTime now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(guest);

        var orgId = guest.OrgId;
        var profileId = guest.GuestCrmProfileId;

        // The profile is the guest's alone when no other guest of the org is linked to it. Read before the guest is unlinked.
        var alone = false;
        if (profileId is { } linkedId)
        {
            alone = !await db.Guests.AnyAsync(
                g => g.OrgId == orgId && g.GuestCrmProfileId == linkedId && g.Id != guest.Id,
                cancellationToken);
        }

        List<GuestContact> contacts;
        if (alone && profileId is { } ownProfileId)
        {
            contacts = await db.GuestContacts
                .Where(c => c.OrgId == orgId && (c.GuestId == guest.Id || c.GuestCrmProfileId == ownProfileId))
                .ToListAsync(cancellationToken);
        }
        else
        {
            contacts = await db.GuestContacts
                .Where(c => c.OrgId == orgId && c.GuestId == guest.Id)
                .ToListAsync(cancellationToken);
        }

        int removed = 0, kept = 0;
        foreach (var contact in contacts)
        {
            if (contact.IsSuppressed)
            {
                if (StripToOptOutMarker(contact, now))
                    kept++;
            }
            else
            {
                db.GuestContacts.Remove(contact);
                removed++;
            }
        }

        var profileRemoved = false;
        var profileTouched = false;
        if (profileId is { } id)
        {
            // Unlinked first: the foreign key of the guest must not point at a profile that goes in the same save.
            guest.GuestCrmProfileId = null;
            profileTouched = true;

            var profile = await db.GuestCrmProfiles.FirstOrDefaultAsync(p => p.OrgId == orgId && p.Id == id, cancellationToken);
            if (profile is not null && alone)
            {
                var proposals = await db.GuestMergeCandidates
                    .Where(m => m.OrgId == orgId && (m.ProfileAId == id || m.ProfileBId == id))
                    .ToListAsync(cancellationToken);
                db.GuestMergeCandidates.RemoveRange(proposals);
                db.GuestCrmProfiles.Remove(profile);
                profileRemoved = true;
            }
            else if (profile is not null)
            {
                profile.PhoneKeyHash = null;
                profile.NameBirthKeyHash = null;
                profile.CompositeKeyHash = string.Empty;
                profile.TagsJson = "[]";
                profile.Country = string.Empty;
                if (profile.PrimaryGuestId == guest.Id)
                    profile.PrimaryGuestId = null;
                profile.UpdatedAt = now;
            }
        }

        if (removed > 0 || kept > 0 || profileTouched)
        {
            logger.LogInformation(
                "CRM data of guest {GuestId}: {Contacts} contacts removed, {OptOuts} opt-outs kept without personal data, profile {Profile}",
                guest.Id,
                removed,
                kept,
                profileRemoved ? "removed" : profileTouched ? "cleared" : "none");
        }

        return new CrmErasureOutcome(removed, kept, profileRemoved, removed > 0 || kept > 0 || profileTouched);
    }

    /// <summary>
    /// Takes a contact that carries a suppression down to what the opt-out needs: the keyed hash of the value (with the org and the channel, the
    /// key of the row), the flag, its date and its source. No value, no name, no IP, no guest, no profile, no language, and no basis, evidence or
    /// confirmation of a consent that is gone. False when it already was a marker.
    /// </summary>
    private static bool StripToOptOutMarker(GuestContact contact, DateTime now)
    {
        var before = Fingerprint(contact);
        contact.Value = string.Empty;
        contact.DisplayName = null;
        contact.Language = string.Empty;
        contact.GuestId = null;
        contact.GuestCrmProfileId = null;
        contact.ConsentIpAddress = null;
        contact.LegalBasis = GuestContactLegalBasis.None;
        contact.BasisVersion = string.Empty;
        contact.BasisRecordedAt = null;
        contact.ConsentSource = null;
        contact.Confirmation = GuestContactConfirmation.Unconfirmed;
        contact.ConfirmedAt = null;
        contact.ConfirmationSentAt = null;
        contact.ConfirmationSentCount = 0;

        var changed = Fingerprint(contact) != before;
        if (changed)
            contact.UpdatedAt = now;
        return changed;
    }

    /// <summary>
    /// Alloggiati data of the booker's own record (retention <c>AlloggiatiData</c>): birth, citizenship, sex, document and
    /// its scan. Names and contacts stay (fiscal data). The guests of the stays are anonymized per booking by
    /// <see cref="AnonymizeStayGuestsOfBookingsAsync"/>.
    /// </summary>
    public async Task<GuestErasureOutcome> EraseAlloggiatiDataAsync(Guest guest, DateTime now, CancellationToken cancellationToken)
    {
        var before = Fingerprint(guest);
        var filesDeleted = await DeleteDocumentScanAsync(guest, cancellationToken);
        EraseAlloggiatiFields(guest);
        var changed = Fingerprint(guest) != before || filesDeleted > 0;
        guest.AlloggiatiDataErasedAt ??= now;
        if (changed)
            guest.UpdatedAt = now;

        return new GuestErasureOutcome(changed, 0, filesDeleted);
    }

    /// <summary>
    /// Deletes the document scan object of <paramref name="guest"/> from the private storage and clears the reference.
    /// The object is kept when another guest record still points to it (a snapshot of the same guest,
    /// <see cref="Guest.CreateSnapshot"/>): it goes with the last reference. A reference that is not a private storage key
    /// (legacy local path never migrated) is cleared with a warning: there is nothing in the storage to delete. Returns
    /// the number of objects deleted.
    /// </summary>
    public async Task<int> DeleteDocumentScanAsync(Guest guest, CancellationToken cancellationToken)
    {
        var key = guest.DocumentScanUrl?.Trim();
        guest.DocumentScanUrl = null;
        if (string.IsNullOrEmpty(key))
            return 0;

        if (!StorageKeys.IsValid(key) || !key.StartsWith(GuestDocumentKeyPrefix, StringComparison.Ordinal))
        {
            logger.LogWarning(
                "Document scan reference of guest {GuestId} is not a private storage key (legacy path): reference cleared, " +
                "nothing deleted from the storage (docs/runbooks/gdpr.md)",
                guest.Id);
            return 0;
        }

        // IgnoreQueryFilters: whichever org the caller works in, any other record pointing to the object keeps it alive.
        var sharedWithAnotherRecord = await db.Guests.IgnoreQueryFilters()
            .AnyAsync(g => g.Id != guest.Id && g.DocumentScanUrl == key, cancellationToken);
        if (sharedWithAnotherRecord)
        {
            logger.LogInformation(
                "Document scan of guest {GuestId} is still referenced by another guest record: reference cleared, object kept",
                guest.Id);
            return 0;
        }

        await fileStorage.DeleteAsync(StorageBucket.Private, key, cancellationToken);
        logger.LogInformation("Document scan of guest {GuestId} deleted from the private storage", guest.Id);
        return 1;
    }

    /// <summary>
    /// Anonymizes the guests of the stays of the given bookings (retention <c>AlloggiatiData</c>, every kind of guest).
    /// Rows already anonymized are left alone. Returns the rows changed per booker (<see cref="Booking.GuestId"/>).
    /// </summary>
    public async Task<Dictionary<(Guid OrgId, Guid GuestId), int>> AnonymizeStayGuestsOfBookingsAsync(
        IReadOnlyCollection<Guid> bookingIds,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var rows = await db.StayGuests
            .Include(s => s.Booking)
            .Where(s => bookingIds.Contains(s.BookingId))
            .ToListAsync(cancellationToken);

        var perBooker = new Dictionary<(Guid, Guid), int>();
        foreach (var row in rows)
        {
            if (!AnonymizeStayGuest(row, now))
                continue;
            var booker = (row.Booking.OrgId, row.Booking.GuestId);
            perBooker[booker] = perBooker.GetValueOrDefault(booker) + 1;
        }

        return perBooker;
    }

    /// <summary>
    /// CO-12 rule, per category of guest (docs/runbooks/gdpr.md): the booker's anonymization takes with it every guest of
    /// the booker's stays, family and group members included, and any row linked to the booker. They exist in CasaZen only
    /// as part of the booker's Alloggiati registration, have no contact data of their own and no documented obligation
    /// keeps them once the communication is done (the Police receipt is the proof, alloggiati.md § 4).
    /// </summary>
    private async Task<int> AnonymizeStayGuestsOfBookerAsync(Guid guestId, DateTime now, CancellationToken cancellationToken)
    {
        var rows = await db.StayGuests
            .Where(s => s.GuestId == guestId || db.Bookings.Any(b => b.Id == s.BookingId && b.GuestId == guestId))
            .ToListAsync(cancellationToken);

        return rows.Count(row => AnonymizeStayGuest(row, now));
    }

    /// <summary>Clears every personal field of a guest of a stay; kind and position stay. False when nothing changed.</summary>
    public static bool AnonymizeStayGuest(StayGuest row, DateTime now)
    {
        var before = Fingerprint(row);
        row.FirstName = AnonymizedName;
        row.LastName = AnonymizedName;
        row.Gender = null;
        row.DateOfBirth = null;
        row.BornInItaly = null;
        row.BirthComuneCode = null;
        row.BirthComuneName = string.Empty;
        row.BirthProvince = null;
        row.BirthCountryCode = null;
        row.BirthCountryName = string.Empty;
        row.CitizenshipCode = null;
        row.CitizenshipName = string.Empty;
        row.DocumentType = null;
        row.DocumentTypeCode = null;
        row.DocumentNumber = string.Empty;
        row.DocumentIssuePlaceCode = null;
        row.DocumentIssuePlaceName = string.Empty;

        var changed = Fingerprint(row) != before || row.AnonymizedAt is null;
        row.AnonymizedAt ??= now;
        if (changed)
            row.UpdatedAt = now;
        return changed;
    }

    private static void EraseAlloggiatiFields(Guest guest)
    {
        guest.DateOfBirth = null;
        guest.PlaceOfBirth = string.Empty;
        guest.Nationality = string.Empty;
        guest.Gender = null;
        guest.DocumentType = null;
        guest.DocumentNumber = string.Empty;
        guest.DocumentIssueDate = null;
        guest.DocumentExpiryDate = null;
        guest.DocumentIssuingCountry = string.Empty;
    }

    private static void EraseIdentity(Guest guest)
    {
        guest.FirstName = AnonymizedName;
        guest.LastName = AnonymizedName;
        guest.Email = AnonymizedEmail(guest.Id);
        guest.PhoneNumber = string.Empty;
        guest.Address = string.Empty;
        guest.City = string.Empty;
        guest.PostalCode = string.Empty;
        guest.Country = string.Empty;
        guest.Notes = string.Empty;
        guest.ConsentIpAddress = string.Empty;
    }

    /// <summary>IP and note of the consent events: the events themselves stay as proof, without personal data.</summary>
    private async Task<int> ClearConsentEvidenceAsync(Guid guestId, CancellationToken cancellationToken)
    {
        var records = await db.GuestConsentRecords
            .Where(r => r.GuestId == guestId && (r.IpAddress != null || r.Note != null))
            .ToListAsync(cancellationToken);
        foreach (var record in records)
        {
            record.IpAddress = null;
            record.Note = null;
        }

        return records.Count;
    }

    /// <summary>
    /// The per-channel marketing consents of the check-in (NF-05, <see cref="MarketingConsentRecord"/>): the marketing e-mail
    /// and the IP go, and the consent ends with them (no channel, e-mail not confirmed, so nothing can be sent). Time, version
    /// of the text and booking stay as the proof that a consent was given. Returns the rows changed (none when already clear).
    /// </summary>
    private async Task<int> ClearMarketingConsentRecordsAsync(Guid guestId, CancellationToken cancellationToken)
    {
        var records = await db.MarketingConsentRecords
            .Where(r => r.GuestId == guestId
                && (r.MarketingEmail != string.Empty || r.IpAddress != string.Empty
                    || r.ConsentEmail || r.ConsentWhatsApp || r.EmailConfirmed))
            .ToListAsync(cancellationToken);
        foreach (var record in records)
        {
            record.MarketingEmail = string.Empty;
            record.IpAddress = string.Empty;
            record.ConsentEmail = false;
            record.ConsentWhatsApp = false;
            record.EmailConfirmed = false;
        }

        return records.Count;
    }

    /// <summary>
    /// Special requests are written by the guest (preferences, possibly health data): not needed for the accounts. The expected arrival
    /// time the guest gave at the checkout (DB-03b) goes with them: a preference of that person, not an accounting record.
    /// </summary>
    private async Task<int> ClearSpecialRequestsAsync(Guid guestId, DateTime now, CancellationToken cancellationToken)
    {
        var bookings = await db.Bookings
            .Where(b => b.GuestId == guestId && (b.SpecialRequests != "" || b.ExpectedArrivalTime != null))
            .ToListAsync(cancellationToken);
        foreach (var booking in bookings)
        {
            booking.SpecialRequests = string.Empty;
            booking.ExpectedArrivalTime = null;
            booking.UpdatedAt = now;
        }

        return bookings.Count;
    }

    /// <summary>
    /// The first name the public reads on the reviews of the guest's stays (DB-12a) goes back to «Ospite»; the mark and the text of the
    /// review stay, with no name: they are the host's reputation and the guest's own words, not an identity.
    /// </summary>
    private async Task<int> AnonymizeReviewsAsync(Guid guestId, CancellationToken cancellationToken)
    {
        var reviews = await db.Reviews
            .Where(r => r.AuthorName != ReviewRules.AnonymousAuthorName
                        && db.Bookings.Any(b => b.Id == r.BookingId && b.GuestId == guestId))
            .ToListAsync(cancellationToken);
        foreach (var review in reviews)
            review.AuthorName = ReviewRules.AnonymousAuthorName;

        return reviews.Count;
    }

    /// <summary>
    /// The messages of the guest with the host (DB-14a) are free text written by the guest and can hold anything: the threads of the
    /// guest's bookings are deleted, with their messages (the foreign key cascades). The host keeps the bookings, not the conversations.
    /// </summary>
    private async Task<int> DeleteMessageThreadsAsync(Guid guestId, CancellationToken cancellationToken)
    {
        var threads = await db.GuestThreads
            .Where(t => db.Bookings.Any(b => b.Id == t.BookingId && b.GuestId == guestId))
            .ToListAsync(cancellationToken);
        db.GuestThreads.RemoveRange(threads);
        return threads.Count;
    }

    /// <summary>An open check-in link would let someone enter personal data again for an anonymized guest.</summary>
    private async Task ExpireOpenCheckInLinksAsync(Guid guestId, DateTime now, CancellationToken cancellationToken)
    {
        var sessions = await db.GuestCheckInSessions
            .Where(s => OpenSessionStatuses.Contains(s.Status) && db.Bookings.Any(b => b.Id == s.BookingId && b.GuestId == guestId))
            .ToListAsync(cancellationToken);
        foreach (var session in sessions)
        {
            session.Status = GuestCheckInSessionStatus.Scaduto;
            session.UpdatedAt = now;
        }
    }

    private static string Fingerprint(Guest g) => string.Join(
        '\u001f',
        g.FirstName, g.LastName, g.Email, g.PhoneNumber, g.Address, g.City, g.PostalCode, g.Country, g.Notes,
        g.DateOfBirth?.ToString("O"), g.PlaceOfBirth, g.Nationality, g.Gender?.ToString(), g.DocumentType?.ToString(),
        g.DocumentNumber, g.DocumentIssueDate?.ToString("O"), g.DocumentExpiryDate?.ToString("O"), g.DocumentIssuingCountry,
        g.DocumentScanUrl, g.ConsentIpAddress, g.MarketingConsent.ToString());

    private static string Fingerprint(StayGuest s) => string.Join(
        '\u001f',
        s.FirstName, s.LastName, s.Gender?.ToString(), s.DateOfBirth?.ToString("O"), s.BornInItaly?.ToString(),
        s.BirthComuneCode, s.BirthComuneName, s.BirthProvince, s.BirthCountryCode, s.BirthCountryName,
        s.CitizenshipCode, s.CitizenshipName, s.DocumentType?.ToString(), s.DocumentTypeCode, s.DocumentNumber,
        s.DocumentIssuePlaceCode, s.DocumentIssuePlaceName);

    private static string Fingerprint(GuestContact c) => string.Join(
        '\u001f',
        c.Value, c.DisplayName, c.Language, c.GuestId?.ToString(), c.GuestCrmProfileId?.ToString(), c.ConsentIpAddress,
        c.LegalBasis.ToString(), c.BasisVersion, c.BasisRecordedAt?.ToString("O"), c.ConsentSource?.ToString(),
        c.Confirmation.ToString(), c.ConfirmedAt?.ToString("O"), c.ConfirmationSentAt?.ToString("O"), c.ConfirmationSentCount);
}

/// <summary>
/// What an erasure changed: false <see cref="Changed"/> means it had already been done. The CRM counters are those of
/// <see cref="GuestDataEraser.EraseCrmDataAsync"/> (counts only, never an address): the contacts deleted, the opt-outs that stay as a row with
/// no personal data, and whether the profile of the guest was deleted.
/// </summary>
public sealed record GuestErasureOutcome(
    bool Changed,
    int StayGuestsAnonymized,
    int FilesDeleted,
    int CrmContactsRemoved = 0,
    int CrmOptOutsKept = 0,
    bool CrmProfileRemoved = false);

/// <summary>What the erasure of the CRM data of a guest did (<see cref="GuestDataEraser.EraseCrmDataAsync"/>).</summary>
/// <param name="ContactsRemoved">Contacts deleted.</param>
/// <param name="OptOutsKept">Suppressed contacts taken down to an opt-out marker (the ones that already were one are not counted).</param>
/// <param name="ProfileRemoved">The profile of the guest was deleted because no other guest was linked to it.</param>
/// <param name="Changed">Anything was staged, the unlink of the guest from its profile included.</param>
public sealed record CrmErasureOutcome(int ContactsRemoved, int OptOutsKept, bool ProfileRemoved, bool Changed);
