using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using DocumentKind = Casazen.Core.Entities.Enums.FiscalDocumentKind;

namespace Casazen.Infrastructure.Octorate;

/// <inheritdoc cref="IOctorateDocumentImportService"/>
/// <remarks>
/// <list type="bullet">
/// <item><b>Which accommodations.</b> The confirmed mappings of the org (<see cref="OctorateBookingRuns.ListMappedPropertiesAsync"/>, the same list the
/// bookings are read from), each accommodation once, under the Octorate scope of the org.</item>
/// <item><b>Upsert.</b> By (org, Octorate id); the unique index refuses a second row, and a lost race reads the row that won. The document is always
/// <see cref="FiscalDocumentStatus.Issued"/> and read-only in CasaZen: only this import changes it, from what Octorate says.</item>
/// <item><b>SDI state.</b> <c>fiscalInvoiceGroup</c> mapped to <see cref="FiscalDocumentSdiStatuses"/> (<c>unknown</c> when Octorate gives none) and written
/// on the row: the service of the SDI outcomes (E06/F03/US01) does not exist yet.</item>
/// <item><b>Failures.</b> A refused or failing call is logged with ids and the error type only, counted, and read again by the next run; a refusal of
/// the whole org (credentials, environment, rate limit) stops the run of the org.</item>
/// </list>
/// </remarks>
public sealed class OctorateDocumentImportService(
    AppDbContext db,
    IOctorateApiClient client,
    IOctorateOrgScope orgScope,
    IDocumentIssuerService issuers,
    IInternalActivityService activities,
    IFeatureFlags featureFlags,
    IHostEnvironment hostEnvironment,
    TimeProvider timeProvider,
    ILogger<OctorateDocumentImportService> logger) : IOctorateDocumentImportService
{
    public async Task<IReadOnlyList<Guid>> OrgsToImportAsync(CancellationToken ct = default)
    {
        if (!Enabled)
            return [];

        var environment = OctorateEnvironments.Of(hostEnvironment);
        var connected = db.OctorateConnections
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .Where(c => c.Environment == environment && c.Status == OctorateConnectionStatus.Connected)
            .Select(c => c.OrgId);
        return await db.OrgFiscalIssuerProfiles
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .AsNoTracking()
            .Where(p => p.IssuerMode == DocumentIssuerMode.Octorate && connected.Contains(p.OrgId))
            .Select(p => p.OrgId)
            .Distinct()
            .ToListAsync(ct);
    }

    public async Task<DateTime?> LastImportedAtAsync(Guid orgId, CancellationToken ct = default) =>
        await db.FiscalDocuments
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .AsNoTracking()
            .Where(d => d.OrgId == orgId && d.Source == FiscalDocumentSource.Octorate)
            .MaxAsync(d => (DateTime?)d.UpdatedAt, ct);

    public async Task<OctorateDocumentImportResult> ImportAsync(Guid orgId, DateTime? since, CancellationToken ct = default)
    {
        if (!Enabled)
        {
            logger.LogInformation("Octorate documents of org {OrgId} not imported: the import is switched off", orgId);
            return new OctorateDocumentImportResult(0, 0, 0, 0, 0);
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var from = (since ?? now.AddDays(-OctorateDocumentImport.FirstReadDays)) - OctorateDocumentImport.Overlap;
        var mapped = (await OctorateBookingRuns.ListMappedPropertiesAsync(db, OctorateEnvironments.Of(hostEnvironment), ct))
            .Where(m => m.OrgId == orgId)
            .ToList();
        var currencies = await db.Properties
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(p => p.OrgId == orgId)
            .Select(p => new { p.Id, p.Currency })
            .ToDictionaryAsync(p => p.Id, p => StayCurrency.Normalize(p.Currency), ct);

        int created = 0, updated = 0, skipped = 0, failed = 0, twoIssuers = 0;
        using (orgScope.Use(orgId))
        {
            foreach (var accommodation in mapped.GroupBy(m => m.AccommodationId))
            {
                OctorateFiscalDocument[] documents;
                try
                {
                    documents = await client.GetDocumentsAsync(accommodation.Key, from, ct);
                }
                catch (OctorateException ex)
                {
                    failed++;
                    logger.LogWarning(
                        "Octorate documents of org {OrgId}, accommodation {AccommodationId} not read ({Error}): read again at the next run",
                        orgId, accommodation.Key, ex.GetType().Name);
                    if (ex is OctorateAuthException or OctorateRateLimitException)
                        break;
                    continue;
                }

                var currency = accommodation.Select(m => currencies.GetValueOrDefault(m.PropertyId)).FirstOrDefault(c => c is not null);
                foreach (var document in documents)
                {
                    var outcome = await UpsertAsync(orgId, document, currency, now, ct);
                    switch (outcome)
                    {
                        case Outcome.Created: created++; break;
                        case Outcome.Updated: updated++; break;
                        case Outcome.CreatedTwoIssuers: created++; twoIssuers++; break;
                        case Outcome.UpdatedTwoIssuers: updated++; twoIssuers++; break;
                        default: skipped++; break;
                    }
                }
            }
        }

        logger.LogInformation(
            "Octorate documents of org {OrgId}: {Created} created, {Updated} updated, {Skipped} skipped, {Failed} reads failed, {TwoIssuers} with two issuers",
            orgId, created, updated, skipped, failed, twoIssuers);
        return new OctorateDocumentImportResult(created, updated, skipped, failed, twoIssuers);
    }

    private bool Enabled =>
        featureFlags.IsEnabled(FeatureFlags.OctorateConnector) && featureFlags.IsEnabled(FeatureFlags.OctorateDocumentImport);

    private async Task<Outcome> UpsertAsync(Guid orgId, OctorateFiscalDocument document, string? propertyCurrency, DateTime now, CancellationToken ct)
    {
        if (KindOf(document.Type) is not { } kind || document.DocumentId.Length > FiscalDocument.OctorateDocumentIdMaxLength)
            return Outcome.Skipped;

        var reservation = document.ReservationId is { Length: > 0 and <= FiscalDocument.OctorateReservationIdMaxLength } r ? r : null;
        var booking = reservation is null
            ? null
            : await db.Bookings
                .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
                .AsNoTracking()
                .Where(b => b.OrgId == orgId && b.OctorateReservationId == reservation)
                .Select(b => new { b.Id, b.PropertyId })
                .FirstOrDefaultAsync(ct);

        var row = await db.FiscalDocuments
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .FirstOrDefaultAsync(d => d.OrgId == orgId && d.OctorateDocumentId == document.DocumentId, ct);
        var isNew = row is null;
        row ??= new FiscalDocument { OrgId = orgId, Source = FiscalDocumentSource.Octorate, OctorateDocumentId = document.DocumentId, CreatedAt = now };
        if (row.Source != FiscalDocumentSource.Octorate)
            return Outcome.Skipped;

        row.Kind = kind;
        row.Status = FiscalDocumentStatus.Issued;
        row.Number = Truncate(document.Number, FiscalDocument.NumberMaxLength);
        row.IssueDate = document.Date;
        row.Currency = Currency(document.Currency) ?? propertyCurrency ?? row.Currency;
        row.TaxableBase = document.TaxableBase ?? 0m;
        row.VatAmount = document.Vat ?? 0m;
        row.Total = document.Total ?? 0m;
        row.PaymentState = document.Paid switch
        {
            true => FiscalDocumentPaymentState.Paid,
            false => FiscalDocumentPaymentState.ToCollect,
            null => FiscalDocumentPaymentState.Unknown,
        };
        var sdi = SdiStatusOf(document.SdiGroup);
        if (row.SdiStatus != sdi)
            row.SdiStatusUpdatedAt = now;
        row.SdiStatus = sdi;
        row.OctorateReservationId = reservation;
        row.OctoratePdfAvailable = document.PdfAvailable;
        row.BookingId = booking?.Id;
        row.RecipientName = Truncate(document.RecipientName, FiscalDocument.RecipientNameMaxLength);
        row.UpdatedAt = now;
        if (isNew)
            db.FiscalDocuments.Add(row);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (isNew && OrgTeamAccess.IsUniqueViolation(ex, FiscalDocument.OctorateDocumentIndexName))
        {
            // Another run imported the same document at the same moment: its row is the one; this run reads it next time.
            db.Entry(row).State = EntityState.Detached;
            return Outcome.Skipped;
        }

        var twoIssuers = booking is not null && await LockOrFlagTwoIssuersAsync(orgId, booking.Id, booking.PropertyId, row.Id, ct);
        await SyncTodosAsync(orgId, row, booking?.PropertyId, ct);

        return (isNew, twoIssuers) switch
        {
            (true, true) => Outcome.CreatedTwoIssuers,
            (true, false) => Outcome.Created,
            (false, true) => Outcome.UpdatedTwoIssuers,
            _ => Outcome.Updated,
        };
    }

    /// <summary>
    /// Locks the booking to Octorate as its issuer. When CasaZen issued for it (the lock answers a conflict, or a CasaZen document is there), the
    /// imported document is kept and an urgent "To do" is raised: CasaZen never hides a double issuance.
    /// </summary>
    private async Task<bool> LockOrFlagTwoIssuersAsync(Guid orgId, Guid bookingId, Guid propertyId, Guid documentId, CancellationToken ct)
    {
        var conflict = false;
        try
        {
            await issuers.LockIssuerAsync(bookingId, BookingDocumentIssuerKind.Octorate, documentId, save: true, ct);
        }
        catch (DomainConflictException)
        {
            conflict = true;
        }

        conflict |= await db.FiscalDocuments
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .AnyAsync(d => d.OrgId == orgId && d.BookingId == bookingId && d.Source == FiscalDocumentSource.CasaZen && d.Status == FiscalDocumentStatus.Issued, ct);
        if (!conflict)
            return false;

        logger.LogWarning("Booking {BookingId} of org {OrgId} has documents of two issuers: a To do is raised", bookingId, orgId);
        await activities.CreateAsync(
            new CreateTodoRequest
            {
                Type = ActivityType.FiscalDocument,
                Action = ActivityAction.ReviewFiscalDocument,
                EntityType = OctorateDocumentImport.IssuersEntityType,
                EntityId = bookingId,
                Urgency = ActivityUrgency.High,
                PropertyId = propertyId,
                OrgId = orgId,
                Reason = OctorateDocumentImport.TwoIssuersReason,
            },
            ct);
        return true;
    }

    /// <summary>The "To do" of a rejected SDI state and of a document to collect, raised while the state holds and closed when it changes.</summary>
    private async Task SyncTodosAsync(Guid orgId, FiscalDocument row, Guid? propertyId, CancellationToken ct)
    {
        await SyncAsync(row.SdiStatus == FiscalDocumentSdiStatuses.Rejected, OctorateDocumentImport.SdiEntityType, OctorateDocumentImport.SdiRejectedReason, ActivityUrgency.High);
        await SyncAsync(row.PaymentState == FiscalDocumentPaymentState.ToCollect, OctorateDocumentImport.PaymentEntityType, OctorateDocumentImport.ToCollectReason, ActivityUrgency.Medium);

        async Task SyncAsync(bool raised, string entityType, string reason, ActivityUrgency urgency)
        {
            if (raised)
            {
                await activities.CreateAsync(
                    new CreateTodoRequest
                    {
                        Type = ActivityType.FiscalDocument,
                        Action = ActivityAction.ReviewFiscalDocument,
                        EntityType = entityType,
                        EntityId = row.Id,
                        Urgency = urgency,
                        PropertyId = propertyId,
                        OrgId = orgId,
                        Reason = reason,
                    },
                    ct);
                return;
            }

            if (await activities.FindOpenAsync(orgId, nameof(ActivityAction.ReviewFiscalDocument), entityType, row.Id, ct) is { } open)
                await activities.CloseAsync(open.Id, InternalActivity.ClosedBySystem, ct);
        }
    }

    /// <summary>The kind of an imported document; <c>null</c> for one CasaZen does not store (a summary, an unknown type).</summary>
    internal static DocumentKind? KindOf(OctorateFiscalDocumentType type) => type switch
    {
        OctorateFiscalDocumentType.Invoice or OctorateFiscalDocumentType.CorrectingInvoice or OctorateFiscalDocumentType.SimplifiedInvoice => DocumentKind.Invoice,
        OctorateFiscalDocumentType.FiscalReceipt or OctorateFiscalDocumentType.GenericReceipt => DocumentKind.Receipt,
        OctorateFiscalDocumentType.CreditNote => DocumentKind.CreditNote,
        OctorateFiscalDocumentType.Proforma => DocumentKind.Proforma,
        _ => null,
    };

    /// <summary>The SDI state of a <c>fiscalInvoiceGroup</c>; <c>unknown</c> when Octorate gives none or one CasaZen does not know.</summary>
    internal static string SdiStatusOf(string? group) => group switch
    {
        "BEFORE_SDI" => FiscalDocumentSdiStatuses.ToSend,
        "SDI_PROCESSING" => FiscalDocumentSdiStatuses.Submitted,
        "SDI_REJECTED" or "REJECTED_BEFORE" => FiscalDocumentSdiStatuses.Rejected,
        "DONE" => FiscalDocumentSdiStatuses.Delivered,
        _ => FiscalDocumentSdiStatuses.Unknown,
    };

    private static string? Currency(string? code) =>
        code is { Length: FiscalDocument.CurrencyMaxLength } && code.All(char.IsAsciiLetter) ? code.ToUpperInvariant() : null;

    private static string? Truncate(string? text, int max) =>
        string.IsNullOrWhiteSpace(text) ? null : text.Trim().Length <= max ? text.Trim() : text.Trim()[..max];

    private enum Outcome
    {
        Skipped,
        Created,
        Updated,
        CreatedTwoIssuers,
        UpdatedTwoIssuers,
    }
}
