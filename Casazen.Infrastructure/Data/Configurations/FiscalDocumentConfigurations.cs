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

/// <summary>
/// The fiscal documents (NF-83, NF-84, E06/F01/US02/T01). Kept ten years (ADR-033-R7): the booking, the credited and the replaced document are
/// <b>restrict</b> foreign keys, so neither the removal of a booking nor anything else can delete a document by cascade. The database refuses two
/// documents with the same number in one org and series and two imported rows of one Octorate document, and accepts only the known SDI states.
/// The recipient columns and the XML get their converter from <c>EncryptedColumns</c> (no length); <c>xmin</c> is the concurrency token.
/// </summary>
internal sealed class FiscalDocumentConfiguration : IEntityTypeConfiguration<FiscalDocument>
{
    public void Configure(EntityTypeBuilder<FiscalDocument> entity)
    {
        entity.HasOne<Booking>().WithMany().HasForeignKey(d => d.BookingId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne<FiscalDocument>().WithMany().HasForeignKey(d => d.CreditedDocumentId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne<FiscalDocument>().WithMany().HasForeignKey(d => d.ReplacesDocumentId).OnDelete(DeleteBehavior.Restrict);
        entity.HasMany(d => d.Lines).WithOne().HasForeignKey(l => l.DocumentId).OnDelete(DeleteBehavior.Cascade);

        entity.HasIndex(d => new { d.OrgId, d.Series, d.SequenceNumber })
            .IsUnique()
            .HasFilter("\"SequenceNumber\" IS NOT NULL")
            .HasDatabaseName(FiscalDocument.SeriesNumberIndexName);
        entity.HasIndex(d => new { d.OrgId, d.BookingId });
        entity.HasIndex(d => new { d.OrgId, d.OctorateDocumentId })
            .IsUnique()
            .HasFilter("\"OctorateDocumentId\" IS NOT NULL")
            .HasDatabaseName(FiscalDocument.OctorateDocumentIndexName);
        entity.HasIndex(d => new { d.OrgId, d.IssueDate });
        entity.HasIndex(d => d.CreditedDocumentId);
        entity.HasIndex(d => d.ReplacesDocumentId);

        entity.Property(d => d.RowVersion).IsRowVersion();
        entity.ToTable(table =>
        {
            table.HasCheckConstraint(
                "CK_FiscalDocuments_SdiStatus",
                "\"SdiStatus\" IN (" + string.Join(", ", FiscalDocumentSdiStatuses.All.Select(s => $"'{s}'")) + ")");
            table.HasCheckConstraint("CK_FiscalDocuments_SequenceNumber", "\"SequenceNumber\" IS NULL OR \"SequenceNumber\" > 0");
        });
    }
}

/// <summary>The lines of a fiscal document: one number per line within the document (<see cref="FiscalDocumentLine.DocumentLineIndexName"/>).</summary>
internal sealed class FiscalDocumentLineConfiguration : IEntityTypeConfiguration<FiscalDocumentLine>
{
    public void Configure(EntityTypeBuilder<FiscalDocumentLine> entity)
    {
        entity.HasIndex(l => new { l.DocumentId, l.LineNumber }).IsUnique().HasDatabaseName(FiscalDocumentLine.DocumentLineIndexName);
    }
}

/// <summary>The counter of the fiscal numbers: one row per org and series (<see cref="FiscalNumberSequence.OrgSeriesIndexName"/>).</summary>
internal sealed class FiscalNumberSequenceConfiguration : IEntityTypeConfiguration<FiscalNumberSequence>
{
    public void Configure(EntityTypeBuilder<FiscalNumberSequence> entity)
    {
        entity.HasIndex(s => new { s.OrgId, s.Series }).IsUnique().HasDatabaseName(FiscalNumberSequence.OrgSeriesIndexName);
        entity.ToTable(table => table.HasCheckConstraint("CK_FiscalNumberSequences_LastNumber", "\"LastNumber\" >= 0"));
    }
}
