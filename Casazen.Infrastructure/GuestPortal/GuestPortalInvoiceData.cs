using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.GuestPortal;

public sealed class GuestPortalInvoiceData(AppDbContext db, IInvoiceDataService invoices)
{
    public sealed record State(bool Issued, bool InvoiceRequested, bool HasInvoiceData, bool HostManaged);

    private Task<bool> HasIssuedInvoiceAsync(Guid orgId, Guid bookingId, CancellationToken ct) =>
        db.FiscalDocuments.AnyAsync(d => d.BookingId == bookingId && d.OrgId == orgId &&
            d.Source == FiscalDocumentSource.CasaZen && d.Purpose == FiscalDocumentPurpose.GuestStay &&
            d.Status == FiscalDocumentStatus.Issued && d.Kind == FiscalDocumentKind.Invoice, ct);

    public async Task<State> GetAsync(Guid orgId, Guid bookingId, CancellationToken ct)
    {
        var issued = await HasIssuedInvoiceAsync(orgId, bookingId, ct);
        var data = await invoices.GetAsync(bookingId, ct);
        return new State(issued, data?.InvoiceRequested == true, data?.HasRecipientData == true, data is { Source: InvoiceDataSource.Host, HasRecipientData: true });
    }

    public async Task<bool> SaveAsync(Guid orgId, Guid bookingId, bool requested, SaveInvoiceDataCommand data, CancellationToken ct)
    {
        await using var transaction = await PostgresAdvisoryLocks.BeginLockedTransactionAsync(db, ct,
            (PostgresAdvisoryLocks.Scope.FiscalDocumentBooking, bookingId.ToString("N")));
        var state = await GetAsync(orgId, bookingId, ct);
        if (state.Issued || state.HostManaged)
            return false;
        if (requested && data.HasContent)
            await invoices.SaveAsync(bookingId, data, InvoiceDataSource.CheckIn, save: false, ct: ct);
        await invoices.MarkRequestedAsync(bookingId, requested, save: false, ct: ct);
        await db.SaveChangesAsync(ct);
        if (transaction is not null)
            await transaction.CommitAsync(ct);
        return true;
    }
}
