using Casazen.Core.Entities;
using Casazen.Core.Exceptions;
using Casazen.Core.Models;
using Casazen.Core.Options;
using Casazen.Core.Repositories;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// Rights of the guests on their personal data (CO-15, docs/runbooks/gdpr.md): complete export, erasure and
/// anonymization (<see cref="GuestDataEraser"/>), marketing consent that only the guest can give. Every operation is
/// audited in <see cref="GuestPrivacyAuditEntry"/>, and the logs carry ids only, never personal data.
/// </summary>
public class GdprService(
    AppDbContext db,
    IGuestRepository guestRepository,
    GuestDataEraser eraser,
    IOptions<GdprOptions> gdprOptions,
    TimeProvider timeProvider,
    ILogger<GdprService> logger) : IGdprService
{
    public const string HostGrantForbiddenCode = "gdpr_marketing_consent_host_grant_forbidden";
    public const string WithdrawalNoteRequiredCode = "gdpr_marketing_withdrawal_note_required";
    public const int WithdrawalNoteMaxLength = 500;

    private DateTime UtcNow() => timeProvider.GetUtcNow().UtcDateTime;

    public async Task<GuestPrivacySummary> GetGuestPrivacySummaryAsync(Guid orgId, Guid guestId, CancellationToken cancellationToken = default)
    {
        var guest = await db.Guests.AsNoTracking().FirstOrDefaultAsync(g => g.Id == guestId && g.OrgId == orgId, cancellationToken)
            ?? throw GuestNotFound(guestId);
        var history = await ConsentHistoryAsync(guest.Id, cancellationToken);
        var retention = await RetentionScheduleAsync(guest, history, cancellationToken);
        var usage = await guestRepository.GetUsageAsync(guest.Id, timeProvider.TodayInRome(), cancellationToken);

        return new GuestPrivacySummary(
            guest.Id,
            MarketingState(guest, history),
            PrivacyNoticeState(guest, history),
            history.Select(r => new GuestConsentHistoryItem(r.Purpose, r.Action, r.Version, r.Source, r.RecordedAt, r.Note)).ToList(),
            retention,
            guest.DataAnonymizedDate,
            guest.AlloggiatiDataErasedAt,
            guest.IsDeleted,
            guest.DeletedAt,
            !string.IsNullOrWhiteSpace(guest.DocumentScanUrl),
            usage.HasOpenBookings);
    }

    public async Task<GuestDataExport> ExportGuestDataAsync(
        Guid orgId,
        Guid guestId,
        string? actorUserId,
        CancellationToken cancellationToken = default)
    {
        var guest = await db.Guests.AsNoTracking().FirstOrDefaultAsync(g => g.Id == guestId && g.OrgId == orgId, cancellationToken)
            ?? throw GuestNotFound(guestId);

        var bookings = await db.Bookings.AsNoTracking()
            .Where(b => b.GuestId == guest.Id && b.OrgId == orgId)
            .OrderBy(b => b.CheckInDate).ThenBy(b => b.Id)
            .Select(b => new
            {
                b.Id,
                b.BookingCode,
                PropertyName = b.Property.Name,
                b.CheckInDate,
                b.CheckOutDate,
                b.Status,
                b.Source,
                b.NumberOfGuests,
                b.NumberOfAdults,
                b.NumberOfChildren,
                b.TotalPrice,
                b.TouristTaxAmount,
                b.SpecialRequests,
                b.CreatedAt,
                Payments = b.Payments
                    .OrderBy(p => p.CreatedAt)
                    .Select(p => new GuestExportPayment(p.Amount, p.RefundedAmount, p.Status, p.Method, p.ProcessedAt, p.CreatedAt))
                    .ToList(),
            })
            .ToListAsync(cancellationToken);
        var bookingIds = bookings.Select(b => b.Id).ToList();

        var stayGuests = await db.StayGuests.AsNoTracking()
            .Where(s => s.GuestId == guest.Id || bookingIds.Contains(s.BookingId))
            .OrderBy(s => s.BookingId).ThenBy(s => s.Position)
            .ToListAsync(cancellationToken);
        var sessions = await db.GuestCheckInSessions.AsNoTracking()
            .Where(s => bookingIds.Contains(s.BookingId))
            .OrderBy(s => s.CreatedAt)
            .Select(s => new GuestExportCheckInSession(s.Id, s.BookingId, s.Status, s.CreatedAt, s.SentAt, s.CompletedAt, s.ExpiresAt))
            .ToListAsync(cancellationToken);
        var communications = await db.AlloggiatiWebReports.AsNoTracking()
            .Where(r => r.GuestId == guest.Id)
            .OrderBy(r => r.CreatedAt)
            .Select(r => new GuestExportAlloggiatiCommunication(r.BookingId, r.Status, r.ReportedAt))
            .ToListAsync(cancellationToken);
        var history = await ConsentHistoryAsync(guest.Id, cancellationToken);
        var retention = await RetentionScheduleAsync(guest, history, cancellationToken);

        var now = UtcNow();
        var export = new GuestDataExport
        {
            ExportedAt = now,
            Subject = new GuestExportSubject(
                guest.Id, guest.FirstName, guest.LastName, guest.Email, guest.PhoneNumber, guest.Address, guest.City,
                guest.PostalCode, guest.Country, guest.Gender, guest.Notes, guest.CreatedAt, guest.UpdatedAt),
            Birth = new GuestExportBirth(ToDate(guest.DateOfBirth), guest.PlaceOfBirth, guest.Nationality),
            Document = new GuestExportDocument(
                guest.DocumentType,
                guest.DocumentNumber,
                ToDate(guest.DocumentIssueDate),
                ToDate(guest.DocumentExpiryDate),
                guest.DocumentIssuingCountry,
                !string.IsNullOrWhiteSpace(guest.DocumentScanUrl)),
            Bookings = bookings.Select(b => new GuestExportBooking(
                b.Id, b.BookingCode, b.PropertyName, DateOnly.FromDateTime(b.CheckInDate), DateOnly.FromDateTime(b.CheckOutDate),
                b.Status, b.Source, b.NumberOfGuests, b.NumberOfAdults, b.NumberOfChildren, b.TotalPrice, b.TouristTaxAmount,
                b.SpecialRequests, b.CreatedAt, b.Payments)).ToList(),
            StayGuests = stayGuests.Select(s => new GuestExportStayGuest(
                s.BookingId, s.Position, s.GuestId == guest.Id, s.Type, s.FirstName, s.LastName, s.Gender, ToDate(s.DateOfBirth),
                s.BornInItaly, s.BirthComuneCode, s.BirthComuneName, s.BirthProvince, s.BirthCountryCode, s.BirthCountryName,
                s.CitizenshipCode, s.CitizenshipName, s.DocumentType, s.DocumentTypeCode, s.DocumentNumber,
                s.DocumentIssuePlaceCode, s.DocumentIssuePlaceName, s.DataSource, s.AnonymizedAt)).ToList(),
            CheckInSessions = sessions,
            AlloggiatiCommunications = communications,
            Consents = new GuestExportConsents(
                guest.ConsentVersion,
                guest.ConsentDate ?? guest.DataProcessingConsentDate,
                guest.ConsentIpAddress,
                guest.MarketingConsent,
                guest.MarketingConsentDate,
                history.Select(r => new GuestExportConsentEvent(r.Purpose, r.Action, r.Version, r.Source, r.RecordedAt, r.IpAddress, r.Note)).ToList()),
            Processing = new GuestExportProcessing(
                guest.DataProcessingPurpose,
                guest.AlloggiatiDataErasedAt,
                guest.DataAnonymizedDate,
                guest.IsDeleted,
                guest.DeletedAt,
                retention),
        };

        AddAudit(guest, GuestPrivacyAuditAction.Exported, actorUserId, now);
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("GDPR export of guest {GuestId} by user {ActorUserId}", guest.Id, actorUserId);
        return export;
    }

    public async Task EraseGuestDataAsync(
        Guid orgId,
        Guid guestId,
        string reason,
        string? actorUserId,
        CancellationToken cancellationToken = default)
    {
        var guest = await LoadForChangeAsync(orgId, guestId, cancellationToken);
        await EnsureNoOpenBookingsAsync(guest, cancellationToken);

        var now = UtcNow();
        var outcome = await eraser.AnonymizeAsync(guest, now, cancellationToken);
        var changed = outcome.Changed || !guest.IsDeleted;
        if (!guest.IsDeleted)
        {
            guest.IsDeleted = true;
            guest.DeletedAt = now;
            guest.DeletionReason = Truncate(reason, 500);
        }

        if (!guest.ErasureRequested)
        {
            guest.ErasureRequested = true;
            guest.ErasureRequestedDate = now;
        }

        if (!changed)
        {
            logger.LogInformation("GDPR erasure of guest {GuestId}: already erased, nothing to do", guest.Id);
            return;
        }

        AddAudit(guest, GuestPrivacyAuditAction.Erased, actorUserId, now, outcome);
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation(
            "GDPR erasure of guest {GuestId} by user {ActorUserId}: {StayGuests} stay guests anonymized, {Files} files deleted",
            guest.Id, actorUserId, outcome.StayGuestsAnonymized, outcome.FilesDeleted);
    }

    public async Task AnonymizeGuestDataAsync(Guid orgId, Guid guestId, string? actorUserId, CancellationToken cancellationToken = default)
    {
        var guest = await LoadForChangeAsync(orgId, guestId, cancellationToken);
        await EnsureNoOpenBookingsAsync(guest, cancellationToken);

        var now = UtcNow();
        var outcome = await eraser.AnonymizeAsync(guest, now, cancellationToken);
        if (!outcome.Changed)
        {
            logger.LogInformation("GDPR anonymization of guest {GuestId}: already anonymized, nothing to do", guest.Id);
            return;
        }

        AddAudit(guest, GuestPrivacyAuditAction.Anonymized, actorUserId, now, outcome);
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation(
            "GDPR anonymization of guest {GuestId} by user {ActorUserId}: {StayGuests} stay guests anonymized, {Files} files deleted",
            guest.Id, actorUserId, outcome.StayGuestsAnonymized, outcome.FilesDeleted);
    }

    public async Task EraseStoredFilesBeforeRemovalAsync(
        Guid orgId,
        Guid guestId,
        string? actorUserId,
        CancellationToken cancellationToken = default)
    {
        var guest = await LoadForChangeAsync(orgId, guestId, cancellationToken);
        var now = UtcNow();
        var filesDeleted = await eraser.DeleteDocumentScanAsync(guest, cancellationToken);
        AddAudit(guest, GuestPrivacyAuditAction.Erased, actorUserId, now, new GuestErasureOutcome(true, 0, filesDeleted));
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdateMarketingConsentAsync(
        Guid orgId,
        Guid guestId,
        bool marketingConsent,
        string? note,
        string? actorUserId,
        CancellationToken cancellationToken = default)
    {
        var guest = await LoadForChangeAsync(orgId, guestId, cancellationToken);

        // Art. 7 GDPR: a consent is given by the data subject. The host may only record the guest's withdrawal.
        if (marketingConsent)
        {
            logger.LogWarning("User {ActorUserId} tried to grant the marketing consent of guest {GuestId}: refused", actorUserId, guest.Id);
            throw new DomainRuleException("gdpr_marketing_consent_host_grant_forbidden", "GdprMarketingConsentHostGrantForbidden");
        }

        var documentedRequest = note?.Trim();
        if (string.IsNullOrEmpty(documentedRequest))
            throw new DomainRuleException("gdpr_marketing_withdrawal_note_required", "GdprMarketingWithdrawalNoteRequired");

        if (!guest.MarketingConsent)
        {
            logger.LogInformation("Marketing consent of guest {GuestId} is not in force: nothing to withdraw", guest.Id);
            return;
        }

        var now = UtcNow();
        db.GuestConsentRecords.Add(new GuestConsentRecord
        {
            OrgId = guest.OrgId,
            GuestId = guest.Id,
            Purpose = GuestConsentPurpose.Marketing,
            Action = GuestConsentAction.Withdrawn,
            Version = await LatestMarketingGrantVersionAsync(guest.Id, cancellationToken),
            Source = GuestConsentSource.HostOnGuestRequest,
            Note = Truncate(documentedRequest, WithdrawalNoteMaxLength),
            RecordedByUserId = actorUserId,
            RecordedAt = now,
        });
        guest.MarketingConsent = false;
        guest.MarketingConsentDate = now;
        guest.UpdatedAt = now;
        AddAudit(guest, GuestPrivacyAuditAction.MarketingConsentWithdrawn, actorUserId, now);
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Marketing consent of guest {GuestId} withdrawn by user {ActorUserId} on the guest's request", guest.Id, actorUserId);
    }

    private static NotFoundException GuestNotFound(Guid guestId) =>
        new($"Guest {guestId} not found") { Code = "guest_not_found", MessageKey = "GuestNotFound" };

    private async Task<Guest> LoadForChangeAsync(Guid orgId, Guid guestId, CancellationToken cancellationToken) =>
        await db.Guests.FirstOrDefaultAsync(g => g.Id == guestId && g.OrgId == orgId, cancellationToken)
        ?? throw GuestNotFound(guestId);

    /// <summary>
    /// Art. 17.3.b GDPR: while a stay is open its Alloggiati communication is a legal obligation still to fulfil, so its
    /// data cannot be erased (same rule as the deletion of a guest, TN-1).
    /// </summary>
    private async Task EnsureNoOpenBookingsAsync(Guest guest, CancellationToken cancellationToken)
    {
        var usage = await guestRepository.GetUsageAsync(guest.Id, timeProvider.TodayInRome(), cancellationToken);
        if (usage.HasOpenBookings)
        {
            logger.LogWarning("GDPR erasure of guest {GuestId} refused: it has open bookings", guest.Id);
            throw new DomainConflictException("guest_has_open_bookings", "GdprGuestHasOpenBookings");
        }
    }

    private void AddAudit(
        Guest guest,
        GuestPrivacyAuditAction action,
        string? actorUserId,
        DateTime now,
        GuestErasureOutcome? outcome = null) =>
        db.GuestPrivacyAuditEntries.Add(new GuestPrivacyAuditEntry
        {
            OrgId = guest.OrgId,
            GuestId = guest.Id,
            Action = action,
            ActorUserId = actorUserId,
            StayGuestsAnonymized = outcome?.StayGuestsAnonymized ?? 0,
            FilesDeleted = outcome?.FilesDeleted ?? 0,
            OccurredAt = now,
        });

    private Task<List<GuestConsentRecord>> ConsentHistoryAsync(Guid guestId, CancellationToken cancellationToken) =>
        db.GuestConsentRecords.AsNoTracking()
            .Where(r => r.GuestId == guestId)
            .OrderBy(r => r.RecordedAt).ThenBy(r => r.Id)
            .ToListAsync(cancellationToken);

    private async Task<string> LatestMarketingGrantVersionAsync(Guid guestId, CancellationToken cancellationToken) =>
        await db.GuestConsentRecords.AsNoTracking()
            .Where(r => r.GuestId == guestId && r.Purpose == GuestConsentPurpose.Marketing && r.Action == GuestConsentAction.Granted)
            .OrderByDescending(r => r.RecordedAt)
            .Select(r => r.Version)
            .FirstOrDefaultAsync(cancellationToken) ?? string.Empty;

    private static GuestMarketingConsentState MarketingState(Guest guest, IReadOnlyList<GuestConsentRecord> history)
    {
        var lastGrant = history.LastOrDefault(r => r.Purpose == GuestConsentPurpose.Marketing && r.Action == GuestConsentAction.Granted);
        return guest.MarketingConsent
            ? new GuestMarketingConsentState(true, lastGrant?.RecordedAt ?? guest.MarketingConsentDate, lastGrant?.Version ?? string.Empty)
            : new GuestMarketingConsentState(false, null, string.Empty);
    }

    private static GuestPrivacyNoticeState PrivacyNoticeState(Guest guest, IReadOnlyList<GuestConsentRecord> history)
    {
        var lastNotice = history.LastOrDefault(r => r.Purpose == GuestConsentPurpose.PrivacyNotice);
        return lastNotice is not null
            ? new GuestPrivacyNoticeState(lastNotice.Version, lastNotice.RecordedAt)
            : new GuestPrivacyNoticeState(guest.ConsentVersion, guest.ConsentDate ?? guest.DataProcessingConsentDate);
    }

    /// <summary>
    /// Retention of each category for this guest, computed like the job does (<see cref="GuestDataRetentionService"/>):
    /// reference date, due date when configured, and when the job last applied it (audit).
    /// </summary>
    private async Task<IReadOnlyList<GuestRetentionScheduleItem>> RetentionScheduleAsync(
        Guest guest,
        IReadOnlyList<GuestConsentRecord> history,
        CancellationToken cancellationToken)
    {
        var retention = gdprOptions.Value.Retention;
        var latestCheckout = await db.Bookings.AsNoTracking()
            .Where(b => b.GuestId == guest.Id)
            .MaxAsync(b => (DateTime?)b.CheckOutDate, cancellationToken);
        var applied = await db.GuestPrivacyAuditEntries.AsNoTracking()
            .Where(a => a.GuestId == guest.Id && a.Action == GuestPrivacyAuditAction.RetentionApplied && a.Category != null)
            .GroupBy(a => a.Category)
            .Select(g => new { Category = g.Key, At = g.Max(a => a.OccurredAt) })
            .ToListAsync(cancellationToken);

        var stayReference = GuestDataRetentionService.StayReferenceDate(latestCheckout, guest.CreatedAt);
        var lastGrant = history.LastOrDefault(r => r.Purpose == GuestConsentPurpose.Marketing && r.Action == GuestConsentAction.Granted);
        DateTime? marketingReference = guest.MarketingConsent ? (lastGrant?.RecordedAt ?? guest.MarketingConsentDate)?.Date : null;

        return Enum.GetValues<GuestDataCategory>().Select(category =>
        {
            var period = retention.For(category);
            var reference = category == GuestDataCategory.Marketing ? marketingReference : stayReference;
            DateTime? due = period.IsConfigured && reference is { } start ? period.AddTo(start).AddDays(1) : null;
            return new GuestRetentionScheduleItem(
                category,
                period.IsConfigured,
                period.Years,
                period.Months,
                period.Days,
                string.IsNullOrWhiteSpace(period.Source) ? null : period.Source.Trim(),
                reference,
                due,
                applied.FirstOrDefault(a => a.Category == category)?.At);
        }).ToList();
    }

    private static DateOnly? ToDate(DateTime? value) => value is { } date ? DateOnly.FromDateTime(date) : null;

    private static string Truncate(string value, int maxLength) => value.Length > maxLength ? value[..maxLength] : value;

    public async Task<Dictionary<string, object>> ExportOrgFiscalDataAsync(Guid orgId, CancellationToken cancellationToken = default)
    {
        var org = await db.Orgs.AsNoTracking().FirstOrDefaultAsync(o => o.Id == orgId, cancellationToken)
            ?? throw new KeyNotFoundException("Org not found");
        var years = await db.PropertyFiscalYears.AsNoTracking()
            .Where(y => y.OrgId == orgId)
            .Select(y => new { y.PropertyId, y.TaxYear, Regime = y.Regime.ToString(), y.IsPrimaryForCedolare })
            .ToListAsync(cancellationToken);
        // Taxpayers recorded per property (CO-18).
        var propertyTaxpayers = await db.Properties.AsNoTracking()
            .Where(p => p.OrgId == orgId && p.TaxpayerFiscalCode != null)
            .Select(p => new { PropertyId = p.Id, FiscalCode = p.TaxpayerFiscalCode })
            .ToListAsync(cancellationToken);
        return new Dictionary<string, object>
        {
            ["hasPartitaIva"] = org.HasPartitaIva,
            ["partitaIvaNumber"] = org.PartitaIvaNumber ?? "",
            ["fiscalCode"] = org.FiscalCode ?? "",
            ["fiscalDataRetentionUntil"] = org.FiscalDataRetentionUntil?.ToString("O") ?? "",
            ["propertyFiscalYears"] = years,
            ["propertyTaxpayers"] = propertyTaxpayers,
            ["exportedAt"] = DateTime.UtcNow.ToString("O"),
        };
    }

    public async Task AnonymizeOrgFiscalDataAsync(Guid orgId, CancellationToken cancellationToken = default)
    {
        var org = await db.Orgs.FirstOrDefaultAsync(o => o.Id == orgId, cancellationToken);
        if (org is null)
            return;
        var token = $"ANON-{orgId:N}";
        org.FiscalCode = token.Length > 16 ? token[..16] : token;
        org.PartitaIvaNumber = "00000000000";
        org.UpdatedAt = DateTime.UtcNow;
        // Codici fiscali of the taxpayers recorded on the org's properties (CO-18): removed, the org profile applies again.
        var properties = await db.Properties
            .Where(p => p.OrgId == orgId && p.TaxpayerFiscalCode != null)
            .ToListAsync(cancellationToken);
        foreach (var property in properties)
            property.TaxpayerFiscalCode = null;
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Org {OrgId} fiscal identifiers anonymized", orgId);
    }
}
