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

/// <summary>
/// The profile of the issuer of the documents of an organization (FIS-02, NF-82, E06/F02/US02/T01): one row for each organization, so a second
/// profile of the same org is refused by the database, whoever writes it. The fiscal identity (VAT number, fiscal code) is on <c>Org</c>.
/// </summary>
internal sealed class OrgFiscalIssuerProfileConfiguration : IEntityTypeConfiguration<OrgFiscalIssuerProfile>
{
    public void Configure(EntityTypeBuilder<OrgFiscalIssuerProfile> entity)
    {
        entity.HasIndex(p => p.OrgId).IsUnique().HasDatabaseName(OrgFiscalIssuerProfile.OrgIndexName);
    }
}

/// <summary>
/// The single issuer of the documents of a booking (FIS-02, NF-82, E06/F02/US02/T01): one row for each booking, so a second issuer of the same
/// booking is refused by the database itself, and the row goes with its booking (cascade). The document that locked it is a plain column: the
/// table of the documents arrives later (E06/F01/US02/T01).
/// </summary>
internal sealed class BookingDocumentIssuerConfiguration : IEntityTypeConfiguration<BookingDocumentIssuer>
{
    public void Configure(EntityTypeBuilder<BookingDocumentIssuer> entity)
    {
        entity.HasOne<Booking>().WithMany().HasForeignKey(i => i.BookingId).OnDelete(DeleteBehavior.Cascade);
        entity.HasIndex(i => i.BookingId).IsUnique().HasDatabaseName(BookingDocumentIssuer.BookingIndexName);
    }
}
