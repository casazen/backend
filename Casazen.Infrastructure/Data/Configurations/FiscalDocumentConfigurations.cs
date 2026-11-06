using Casazen.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Casazen.Infrastructure.Data.Configurations;

/// <summary>
/// The invoice data of a booking (FIS-01, NF-81, E06/F02/US01/T01): one row for each booking, gone with the booking (cascade), so the reset of the
/// demo and the erasure of a booking leave nothing of who an invoice was for. The personal columns get their converter from
/// <c>EncryptedColumns</c> (no length: the payload is longer than the text); <c>xmin</c> is the concurrency token, so a host's edit and an import
/// that read the same row cannot both write it.
/// </summary>
internal sealed class BookingInvoiceDataConfiguration : IEntityTypeConfiguration<BookingInvoiceData>
{
    public void Configure(EntityTypeBuilder<BookingInvoiceData> entity)
    {
        entity.HasOne<Booking>().WithMany().HasForeignKey(d => d.BookingId).OnDelete(DeleteBehavior.Cascade);
        entity.HasIndex(d => d.BookingId).IsUnique().HasDatabaseName(BookingInvoiceData.BookingIndexName);
        entity.Property(d => d.RowVersion).IsRowVersion();
    }
}
