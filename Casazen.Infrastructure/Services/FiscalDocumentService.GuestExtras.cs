using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.GuestPortal;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Services;

public sealed partial class FiscalDocumentService
{
    private IQueryable<GuestHostExtraOrder> PendingSupplements() =>
        db.GuestHostExtraOrders.Where(o => o.SupplementToDocumentId != null &&
            (o.Status == GuestHostExtraStatus.Paid || o.Status == GuestHostExtraStatus.Fulfilled) &&
            !db.FiscalDocuments.Any(d => d.SupplementForExtraOrderId == o.Id && d.Status != FiscalDocumentStatus.Discarded));

    private async Task<List<FiscalDocument>> SupplementalDraftsAsync(Guid bookingId, Guid orgId, string? userId, CancellationToken ct)
    {
        await using var transaction = await PostgresAdvisoryLocks.BeginLockedTransactionAsync(db, ct,
            (PostgresAdvisoryLocks.Scope.FiscalDocumentBooking, bookingId.ToString("N")));
        var orders = await PendingSupplements().Where(o => o.OrgId == orgId && o.BookingId == bookingId)
            .OrderBy(o => o.CreatedAt).Select(o => o.Id).ToListAsync(ct);
        var made = await db.FiscalDocuments.Include(d => d.Lines).Where(d => d.OrgId == orgId && d.BookingId == bookingId &&
            d.SupplementForExtraOrderId != null && d.Status == FiscalDocumentStatus.Draft)
            .OrderBy(d => d.CreatedAt).ToListAsync(ct);
        if (orders.Count > 0)
        {
            var plan = await RequirePlanAsync(await BookingOfAsync(bookingId, ct), ct);
            var issuer = plan.Documents.Single(p => p.Bills(FiscalLineType.ExtraService));
            foreach (var orderId in orders)
            {
                var draft = new FiscalDocument
                {
                    BookingId = bookingId,
                    SupplementForExtraOrderId = orderId,
                    Source = FiscalDocumentSource.CasaZen,
                    Status = FiscalDocumentStatus.Draft,
                    CreatedAt = UtcNow,
                    CreatedByUserId = userId
                };
                made.Add(await RebuildAsync(draft, ct, isNew: true, planned: issuer));
            }
        }
        if (transaction is not null) await transaction.CommitAsync(ct);
        return made;
    }

    private async Task<GuestHostExtraOrder> SupplementOrderAsync(Guid orgId, Guid bookingId, Guid orderId, Guid issuerId, CancellationToken ct)
    {
        return await db.GuestHostExtraOrders.AsNoTracking().SingleOrDefaultAsync(o =>
            o.Id == orderId && o.OrgId == orgId && o.BookingId == bookingId &&
            (o.Status == GuestHostExtraStatus.Paid || o.Status == GuestHostExtraStatus.Fulfilled) &&
            db.FiscalDocuments.Any(d => d.Id == o.SupplementToDocumentId && d.OrgId == orgId &&
                d.BookingId == bookingId && d.Currency == o.Currency && d.Status == FiscalDocumentStatus.Issued && d.IssuerProfileId == issuerId), ct)
            ?? throw new DomainConflictException(FiscalDocumentErrors.DraftOutdated, "FiscalDocumentDraftOutdated");
    }
}
