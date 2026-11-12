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
/// carries a suppression, with the flag and its date (the opt-out survives like the entry of the suppression list), the
/// profile that other guests are still linked to, without its keyed hashes, tags and country, and the tombstones of the profiles
/// merged into it, emptied but for the pointer to the profile that took their place.
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
    /// mark and the text stay, anonymous); the words and the names of the copy of its conversations with the channels (E05/F01/US01/T03, ADR-032-R13);
    /// open check-in links of its bookings expire; and what the CRM holds of the guest
    /// (<see cref="EraseCrmDataAsync"/>), in the same unit of work. <see cref="GuestErasureOutcome.Changed"/> is false when there was nothing left to remove (idempotent).
    /// </summary>
    public async Task<GuestErasureOutcome> AnonymizeAsync(Guest guest, DateTime now, CancellationToken cancellationToken)
    {
        var before = Fingerprint(guest);
        var filesDeleted = await DeleteDocumentScanAsync(guest, cancellationToken);
        filesDeleted += await DeleteVerificationDocumentImagesAsync(guest, cancellationToken);
        EraseAlloggiatiFields(guest);
        EraseIdentity(guest);
        guest.MarketingConsent = false;

        var stayGuests = await AnonymizeStayGuestsOfBookerAsync(guest.Id, now, cancellationToken);
        var taxReasons = await ClearTaxExemptionReasonsAsync(
            await db.Bookings.Where(b => b.GuestId == guest.Id).Select(b => b.Id).ToListAsync(cancellationToken), cancellationToken);
        var consentEvidence = await ClearConsentEvidenceAsync(guest.Id, cancellationToken);
        var marketingConsents = await ClearMarketingConsentRecordsAsync(guest.Id, cancellationToken);
        var specialRequests = await ClearSpecialRequestsAsync(guest.Id, now, cancellationToken);
        var reviews = await AnonymizeReviewsAsync(guest.Id, cancellationToken);
        await ExpireOpenCheckInLinksAsync(guest.Id, now, cancellationToken);
        var threads = await DeleteMessageThreadsAsync(guest.Id, cancellationToken);
        var otaThreads = (await AnonymizeOtaThreadsOfGuestAsync(guest.Id, now, cancellationToken)).Threads;
        var crm = await EraseCrmDataAsync(guest, now, cancellationToken);

        var changed = Fingerprint(guest) != before
            || filesDeleted > 0 || stayGuests > 0 || taxReasons > 0 || consentEvidence > 0 || marketingConsents > 0
            || specialRequests > 0 || reviews > 0 || threads > 0 || otaThreads > 0 || crm.Changed
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
    /// hashes, its tags and its country, which may be the erased guest's. Guests of one profile erased in the same unit of work (the retention erases
    /// a chunk of guests before one save) do not count for each other: the last of them takes the profile, and nothing is left with no guest;</item>
    /// <item><b>the profiles merged into it follow it</b> (<see cref="GuestCrmProfile.MergedIntoProfileId"/>, chains included, and a link of the guest
    /// that still points to a tombstone is followed to the survivor): when the survivor is deleted its tombstones are deleted with it; when it stays
    /// (other guests are linked to it or to its tombstones) the tombstones are emptied, because what they held cannot be attributed to one guest: tags,
    /// country, language, every keyed hash, the primary guest, the consent flags and the aggregates of the stays go, and only the pointer to the profile
    /// that took their place stays;</item>
    /// <item>the recipients of a campaign would keep their row and lose their references to the contact and to the profile (null, through their own
    /// <c>SetNull</c> foreign keys): the campaign model is deferred (<c>kb/plan/deferred</c>, B05) and does not exist yet, so there is nothing to do
    /// here until it does.</item>
    /// </list>
    /// Idempotent: once the guest is unlinked and its contacts are gone or stripped, a second run finds nothing. Nothing is logged but counts.
    /// </summary>
    public async Task<CrmErasureOutcome> EraseCrmDataAsync(Guest guest, DateTime now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(guest);

        var orgId = guest.OrgId;
        var profileId = guest.GuestCrmProfileId;

        // The profile of the guest is not alone in the CRM: the merges of duplicates (E07/F01/US03/T02) keep the profile that goes as a tombstone, with all
        // it held, and move its guests and contacts to the survivor. The profiles of the guest are the survivor and every tombstone that leads to it, chains
        // included (a link kept from before a merge may still point to a tombstone): the erasure covers all of them, or a tombstone would keep the tags, the
        // country, the language and the keyed hashes of the guest.
        var family = new List<Guid>();
        if (profileId is { } linkedId)
            family = (await CrmProfileFamily.OfAsync(db, orgId, linkedId, cancellationToken)).ProfileIds;

        // The profiles are the guest's alone when no other guest of the org is linked to any of them. Read before the guest is unlinked. A guest that this
        // unit of work has already unlinked no longer counts: an erasure run erases several guests before one save (the retention does), the database still
        // has the first one linked when the second is read, and without this a profile whose guests all go in the same run would never be deleted and would
        // be left with no guest, its aggregates and the contacts of no guest.
        var alone = false;
        if (family.Count > 0)
        {
            var others = await db.Guests
                .AsNoTracking()
                .Where(g => g.OrgId == orgId && g.GuestCrmProfileId != null && family.Contains(g.GuestCrmProfileId.Value) && g.Id != guest.Id)
                .Select(g => g.Id)
                .ToListAsync(cancellationToken);
            alone = others.All(otherId => db.Guests.Local.Any(g => g.Id == otherId && (g.GuestCrmProfileId == null || !family.Contains(g.GuestCrmProfileId.Value))));
        }

        List<GuestContact> contacts;
        if (alone)
        {
            contacts = await db.GuestContacts
                .Where(c => c.OrgId == orgId && (c.GuestId == guest.Id || (c.GuestCrmProfileId != null && family.Contains(c.GuestCrmProfileId.Value))))
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
            // Already taken out by an earlier guest of the same unit of work: not counted twice.
            if (db.Entry(contact).State == EntityState.Deleted)
                continue;

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
        var mergedProfiles = 0;
        if (profileId is not null)
        {
            // Unlinked first: the foreign key of the guest must not point at a profile that goes in the same save.
            guest.GuestCrmProfileId = null;
            profileTouched = true;

            var profiles = await db.GuestCrmProfiles
                .Where(p => p.OrgId == orgId && family.Contains(p.Id))
                .ToListAsync(cancellationToken);
            mergedProfiles = profiles.Count(p => p.MergedIntoProfileId is not null);
            if (alone)
            {
                // The survivor goes, and with it every tombstone that leads to it and every proposal of merge that involves one of them.
                var proposals = await db.GuestMergeCandidates
                    .Where(m => m.OrgId == orgId && (family.Contains(m.ProfileAId) || family.Contains(m.ProfileBId)))
                    .ToListAsync(cancellationToken);
                db.GuestMergeCandidates.RemoveRange(proposals);
                db.GuestCrmProfiles.RemoveRange(profiles);
                profileRemoved = profiles.Count > 0;
            }
            else
            {
                foreach (var profile in profiles)
                {
                    if (profile.MergedIntoProfileId is null)
                        ClearSharedProfile(profile, guest.Id, now);
                    else
                        ClearTombstone(profile, now);
                }
            }
        }

        if (removed > 0 || kept > 0 || profileTouched)
        {
            logger.LogInformation(
                "CRM data of guest {GuestId}: {Contacts} contacts removed, {OptOuts} opt-outs kept without personal data, profile {Profile}, {Merged} merged profiles",
                guest.Id,
                removed,
                kept,
                profileRemoved ? "removed" : profileTouched ? "cleared" : "none",
                mergedProfiles);
        }

        return new CrmErasureOutcome(removed, kept, profileRemoved, removed > 0 || kept > 0 || profileTouched);
    }

    /// <summary>
    /// The profile that other guests are still linked to loses what may be the erased guest's: the keyed hashes, the tags, the country and, when it was
    /// the erased guest, its primary guest. The language and the aggregates stay (the next sync of a guest of the profile calculates the aggregates again).
    /// </summary>
    private static void ClearSharedProfile(GuestCrmProfile profile, Guid guestId, DateTime now)
    {
        profile.PhoneKeyHash = null;
        profile.NameBirthKeyHash = null;
        profile.CompositeKeyHash = string.Empty;
        profile.TagsJson = "[]";
        profile.Country = string.Empty;
        if (profile.PrimaryGuestId == guestId)
            profile.PrimaryGuestId = null;
        profile.UpdatedAt = now;
    }

    /// <summary>
    /// A tombstone of a merged profile (<see cref="GuestCrmProfile.MergedIntoProfileId"/>) of a survivor that stays: whatever it held cannot be told
    /// from the data of the other guests of the survivor, so it is emptied (tags, country, language, every keyed hash, the primary guest, the consent
    /// flags and the aggregates of its stays). What stays is the technical pointer to the profile that took its place, and the ids and times of the row.
    /// </summary>
    private static void ClearTombstone(GuestCrmProfile profile, DateTime now)
    {
        profile.CompositeKeyHash = string.Empty;
        profile.PhoneKeyHash = null;
        profile.NameBirthKeyHash = null;
        profile.TagsJson = "[]";
        profile.Country = string.Empty;
        profile.Language = string.Empty;
        profile.PrimaryGuestId = null;
        profile.ConfirmedMarketingEmail = string.Empty;
        profile.MarketingConsentEmail = false;
        profile.MarketingConsentWhatsApp = false;
        profile.TotalStays = 0;
        profile.TotalSpend = 0m;
        profile.PropertyIdsJson = "[]";
        profile.FirstStayAt = now;
        profile.LastStayAt = now;
        profile.UpdatedAt = now;
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
        filesDeleted += await DeleteVerificationDocumentImagesAsync(guest, cancellationToken);
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
    /// Deletes the photos of identity documents that an identity verification of the bookings of <paramref name="guest"/> kept (E03/F04/US02/T04,
    /// art. 17 GDPR): the encrypted objects of the private bucket and their <see cref="DocumentImage"/> rows (<see cref="DocumentImageCleaner"/>),
    /// also those a verification that ended already deleted from the bucket and marked. The objects go before the caller saves. Returns the photos
    /// (rows) removed.
    /// </summary>
    public async Task<int> DeleteVerificationDocumentImagesAsync(Guest guest, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(guest);

        // IgnoreQueryFilters: whichever org the caller works in, the guest's own photos go; the org of the guest is named.
        var images = await db.DocumentImages
            .IgnoreQueryFilters()
            .Where(i => i.OrgId == guest.OrgId && i.GuestId == guest.Id)
            .ToListAsync(cancellationToken);
        if (images.Count == 0)
            return 0;

        await DocumentImageCleaner.DeleteAsync(db, fileStorage, logger, images, cancellationToken);
        logger.LogInformation("{Count} document photo(s) of guest {GuestId} deleted from the private storage", images.Count, guest.Id);
        return images.Count;
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

        await ClearTaxExemptionReasonsAsync(bookingIds, cancellationToken);
        return perBooker;
    }

    /// <summary>
    /// NF-46, CO-15 (art. 17 and 9 GDPR): the reason of a tourist tax exemption can reveal a health condition, so it is not kept with an erased
    /// guest. The lines of the confirmed tax of the stays keep their kind and amount (fiscal retention, art. 6.1.c) and lose the reason code;
    /// the calculation hash of such a record is zeroed with it (<see cref="StayTouristTax.ErasedCalculationHash"/>), so nothing left in the
    /// record can be tried against the list of reasons. Returns the lines changed (none when already clear).
    /// </summary>
    public async Task<int> ClearTaxExemptionReasonsAsync(IReadOnlyCollection<Guid> bookingIds, CancellationToken cancellationToken)
    {
        if (bookingIds.Count == 0)
            return 0;

        var ids = bookingIds.ToList();
        var records = await db.StayTouristTaxes
            .Include(t => t.Guests)
            .Where(t => ids.Contains(t.BookingId) && t.Guests.Any(g => g.ExemptionCode != null))
            .ToListAsync(cancellationToken);
        var cleared = 0;
        foreach (var record in records)
        {
            foreach (var line in record.Guests.Where(g => g.ExemptionCode is not null))
            {
                line.ExemptionCode = null;
                cleared++;
            }

            if (record.CalculationHash is not null)
                record.CalculationHash = StayTouristTax.ErasedCalculationHash;
        }

        return cleared;
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

        // NF-40, NF-46: the document dates, the residence, the statistical fields and the tourist tax exemption (whose reason can reveal
        // a health condition) go with the guest too.
        row.DocumentIssuedOn = null;
        row.DocumentExpiresOn = null;
        row.ResidenceInItaly = null;
        row.ResidenceComuneCode = null;
        row.ResidenceComuneName = null;
        row.ResidenceProvince = null;
        row.ResidenceCountryCode = null;
        row.ResidenceCountryName = null;
        row.TourismType = null;
        row.TravelWay = null;
        row.EducationLevel = null;
        row.TaxExemptionCode = null;

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

    /// <summary>
    /// The copy of the OTA conversations of the guest (E05/F01/US01/T03, ADR-032-R13): the threads of the guest's bookings lose the words and the
    /// names, see <see cref="AnonymizeOtaThreadsAsync"/>. The threads stay with the booking and the dates of their messages, so the host still sees
    /// that a conversation took place, and no more. A conversation with no booking (an inquiry) has no guest record to be erased with: the retention
    /// takes care of it.
    /// </summary>
    public async Task<OtaAnonymizationOutcome> AnonymizeOtaThreadsOfGuestAsync(Guid guestId, DateTime now, CancellationToken cancellationToken)
    {
        var ofGuest = db.OtaMessageThreads.Where(t => t.BookingId != null && db.Bookings.Any(b => b.Id == t.BookingId && b.GuestId == guestId));
        var threadIds = await HoldingPersonalData(ofGuest)
            .Select(t => t.Id)
            .ToListAsync(cancellationToken);

        return await AnonymizeOtaThreadsAsync(threadIds, now, cancellationToken);
    }

    /// <summary>
    /// The threads among <paramref name="threads"/> that still hold something of a person: the ones not marked yet, and the marked ones with a message
    /// that still has a sender's name or a link to an attachment. The anonymization sets that name on every message it touches and drops the link, so a
    /// marked thread that has either is one where a read of the channel manager, racing the erasure, stored a message after the erasure had read the
    /// thread (the erasure and the sync do not wait for each other). Found on the plain columns on purpose: the text is encrypted and never compared in SQL.
    /// </summary>
    internal IQueryable<OtaMessageThread> HoldingPersonalData(IQueryable<OtaMessageThread> threads)
    {
        return threads.Where(t => t.AnonymizedAt == null
            || db.OtaMessages.Any(m => m.ThreadId == t.Id
                && (m.SenderName != AnonymizedName || m.AttachmentUrl != null || m.AttachmentContentType != null)));
    }

    /// <summary>
    /// Takes the personal data out of the OTA threads <paramref name="threadIds"/>: the text of every message is emptied, its sender's name
    /// and the name of the guest of the thread become <see cref="AnonymizedName"/>, and the address of an attachment on the channel manager is
    /// forgotten (the file itself is not here: only the link to it was). The thread is marked (<see cref="OtaMessageThread.AnonymizedAt"/>), so the
    /// next read of the channel manager brings none of it back and a second run finds nothing to do. A thread that is marked already is touched only
    /// for the messages that still hold something (<see cref="HoldingPersonalData"/>: a read that raced the mark stored them). Stages the changes
    /// without saving. The ids, the dates, the direction, the state and the counts stay: they are the shape of the conversation, not a person.
    /// The outcome counts the threads that changed and the messages that lost something.
    /// </summary>
    public async Task<OtaAnonymizationOutcome> AnonymizeOtaThreadsAsync(
        IReadOnlyCollection<Guid> threadIds,
        DateTime now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(threadIds);
        if (threadIds.Count == 0)
            return new OtaAnonymizationOutcome(0, 0);

        var threads = await db.OtaMessageThreads
            .Where(t => threadIds.Contains(t.Id))
            .ToListAsync(cancellationToken);
        if (threads.Count == 0)
            return new OtaAnonymizationOutcome(0, 0);

        var ids = threads.Select(t => t.Id).ToList();
        var messages = (await db.OtaMessages.Where(m => ids.Contains(m.ThreadId)).ToListAsync(cancellationToken)).ToLookup(m => m.ThreadId);
        int changed = 0, cleared = 0;
        foreach (var thread in threads)
        {
            var marked = thread.AnonymizedAt is not null;
            var lost = AnonymizeOtaThread(thread, messages[thread.Id], now);
            if (!marked || lost > 0)
            {
                changed++;
                cleared += lost;
            }
        }

        return new OtaAnonymizationOutcome(changed, cleared);
    }

    /// <summary>
    /// Takes the personal data out of one OTA thread that is loaded with its messages (<see cref="AnonymizeOtaThreadsAsync"/> says what goes). The one
    /// place that knows which fields are personal: the erasure and the retention call it for the threads they find, and the sync of the channel manager
    /// calls it for a thread of an erased guest that was not there to be erased when the guest was. A message that holds nothing of a person is left as it is.
    /// Stages the changes without saving. Returns the messages that lost something.
    /// </summary>
    internal static int AnonymizeOtaThread(OtaMessageThread thread, IEnumerable<OtaMessage> messages, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(thread);
        ArgumentNullException.ThrowIfNull(messages);

        var lost = 0;
        foreach (var message in messages)
        {
            if (message.Text.Length == 0 && message.SenderName == AnonymizedName && message.AttachmentUrl is null && message.AttachmentContentType is null)
                continue;

            message.Text = string.Empty;
            message.SenderName = AnonymizedName;
            message.AttachmentUrl = null;
            message.AttachmentContentType = null;
            lost++;
        }

        if (thread.AnonymizedAt is null)
        {
            thread.GuestDisplayName = AnonymizedName;
            thread.AnonymizedAt = now;
            thread.UpdatedAt = now;
        }
        else if (lost > 0)
        {
            thread.UpdatedAt = now;
        }

        return lost;
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
        s.DocumentIssuePlaceCode, s.DocumentIssuePlaceName, s.DocumentIssuedOn?.ToString("O"), s.DocumentExpiresOn?.ToString("O"),
        s.ResidenceInItaly?.ToString(), s.ResidenceComuneCode, s.ResidenceComuneName, s.ResidenceProvince, s.ResidenceCountryCode,
        s.ResidenceCountryName, s.TourismType, s.TravelWay, s.EducationLevel, s.TaxExemptionCode);

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

/// <summary>What the anonymization of OTA conversations changed: the threads, and the messages that lost their words.</summary>
public sealed record OtaAnonymizationOutcome(int Threads, int Messages);
