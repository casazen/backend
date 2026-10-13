using Casazen.Core.Entities;
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
/// without IP and note (proof of consent, art. 7.1 GDPR); the Alloggiati communication status and receipt reference.
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
    /// the IP and note of its consent events, the special requests of its bookings; open check-in links of its bookings
    /// expire. <see cref="GuestErasureOutcome.Changed"/> is false when there was nothing left to remove (idempotent).
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
        var specialRequests = await ClearSpecialRequestsAsync(guest.Id, now, cancellationToken);
        await ExpireOpenCheckInLinksAsync(guest.Id, now, cancellationToken);

        var changed = Fingerprint(guest) != before
            || filesDeleted > 0 || stayGuests > 0 || consentEvidence > 0 || specialRequests > 0
            || guest.DataAnonymizedDate is null;
        guest.AlloggiatiDataErasedAt ??= now;
        guest.DataAnonymizedDate ??= now;
        if (changed)
            guest.UpdatedAt = now;

        return new GuestErasureOutcome(changed, stayGuests, filesDeleted);
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

    /// <summary>Special requests are written by the guest (preferences, possibly health data): not needed for the accounts.</summary>
    private async Task<int> ClearSpecialRequestsAsync(Guid guestId, DateTime now, CancellationToken cancellationToken)
    {
        var bookings = await db.Bookings
            .Where(b => b.GuestId == guestId && b.SpecialRequests != "")
            .ToListAsync(cancellationToken);
        foreach (var booking in bookings)
        {
            booking.SpecialRequests = string.Empty;
            booking.UpdatedAt = now;
        }

        return bookings.Count;
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
}

/// <summary>What an erasure changed: false <see cref="Changed"/> means it had already been done.</summary>
public sealed record GuestErasureOutcome(bool Changed, int StayGuestsAnonymized, int FilesDeleted);
