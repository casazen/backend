using Casazen.Core.Entities.Enums;
using Casazen.Core.GuestPortal;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.GuestPortal;

public static class GuestExtraFiscalPayment
{
    /// <summary>Serialize the payment with guest document numbering. An already issued document cannot acquire more lines.</summary>
    public static async Task SavePaidAsync(AppDbContext db, GuestHostExtraOrder order, CancellationToken ct)
    {
        await using var transaction = await PostgresAdvisoryLocks.BeginLockedTransactionAsync(db, ct,
            (PostgresAdvisoryLocks.Scope.FiscalDocumentBooking, order.BookingId.ToString("N")));
        if (order.Status is not (GuestHostExtraStatus.Paid or GuestHostExtraStatus.Fulfilled))
        {
            order.SupplementToDocumentId = await db.FiscalDocuments.AsNoTracking()
                .Where(d => d.OrgId == order.OrgId && d.BookingId == order.BookingId &&
                    d.Purpose == FiscalDocumentPurpose.GuestStay && d.Source == FiscalDocumentSource.CasaZen &&
                    d.Status == FiscalDocumentStatus.Issued && !d.CreditedInFull &&
                    (d.Kind == FiscalDocumentKind.Invoice || d.Kind == FiscalDocumentKind.Receipt) &&
                    db.OrgFiscalIssuerProfiles.Any(p => p.Id == d.IssuerProfileId && p.OrgId == order.OrgId && p.PropertyOwnerId == null))
                .OrderByDescending(d => d.ForAgencyServices).ThenByDescending(d => d.CreatedAt)
                .Select(d => (Guid?)d.Id).FirstOrDefaultAsync(ct);
            order.Status = GuestHostExtraStatus.Paid;
            await db.SaveChangesAsync(ct);
        }
        if (transaction is not null) await transaction.CommitAsync(ct);
    }
}
