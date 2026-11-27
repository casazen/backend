using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.GuestPortal;
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
/// The profiles of the issuers (FIS-02, NF-82, E06/F02/US02/T01, E11/F01/US01/T01, E17/F01/US01/T01): one row for each organization (no owner) and one for each
/// owner who issues, so a second profile of the same org or of the same owner is refused by the database, whoever writes it (two partial unique indexes). The
/// Italian fiscal identity of the organization (VAT number, fiscal code) is on <c>Org</c>; an owner's is on its profile. The personal data and the bank
/// accounts get their converter from <c>EncryptedColumns</c> (no length), the channels whose portal collects the tourist tax are jsonb. The profile of an owner
/// goes with the owner (cascade), unless a document was issued on it (the foreign key of the documents is restrict).
/// </summary>
internal sealed class OrgFiscalIssuerProfileConfiguration : IEntityTypeConfiguration<OrgFiscalIssuerProfile>
{
    public void Configure(EntityTypeBuilder<OrgFiscalIssuerProfile> entity)
    {
        entity.HasIndex(p => p.OrgId)
            .IsUnique()
            .HasFilter("\"PropertyOwnerId\" IS NULL")
            .HasDatabaseName(OrgFiscalIssuerProfile.OrgIndexName);
        entity.HasIndex(p => p.PropertyOwnerId)
            .IsUnique()
            .HasFilter("\"PropertyOwnerId\" IS NOT NULL")
            .HasDatabaseName(OrgFiscalIssuerProfile.OwnerIndexName);
        entity.HasOne<PropertyOwner>().WithMany().HasForeignKey(p => p.PropertyOwnerId).OnDelete(DeleteBehavior.Cascade);
        entity.Ignore(p => p.PlatformCollectedTouristTaxChannels);
        entity.Ignore(p => p.IsOwnerProfile);

        // Who holds the accounts of the portals and whether their commissions are recharged (E14/F01/US01/T01, ⚖ I10, I11): the migration gives the profiles that
        // exist the defaults (the owner, the recharge on). EF always writes the value of the entity: left to generate it, EF would take an explicit false for
        // «not set» and the insert would write the default (true) in its place.
        entity.Property(p => p.PortalAccountHolder).HasDefaultValue(PortalAccountHolder.Owner).ValueGeneratedNever();
        entity.Property(p => p.RechargePortalCommissions).HasDefaultValue(true).ValueGeneratedNever();
    }
}

/// <summary>
/// Who holds the account of a listing on a portal (E14/F01/US01/T01, ⚖ I10): one row for each property and portal (<see cref="PropertyChannelAccountHolder.PropertyChannelIndexName"/>),
/// gone with its property (cascade), and only a known holder (check).
/// </summary>
internal sealed class PropertyChannelAccountHolderConfiguration : IEntityTypeConfiguration<PropertyChannelAccountHolder>
{
    public void Configure(EntityTypeBuilder<PropertyChannelAccountHolder> entity)
    {
        entity.HasOne<Property>().WithMany().HasForeignKey(h => h.PropertyId).OnDelete(DeleteBehavior.Cascade);
        entity.HasIndex(h => new { h.PropertyId, h.Channel }).IsUnique().HasDatabaseName(PropertyChannelAccountHolder.PropertyChannelIndexName);
        entity.HasIndex(h => h.OrgId);
        entity.ToTable(table => table.HasCheckConstraint(
            "CK_PropertyChannelAccountHolders_Holder",
            $"\"Holder\" IN ({(int)PortalAccountHolder.Owner}, {(int)PortalAccountHolder.Agency})"));
    }
}

/// <summary>
/// Who issued the documents of a booking (FIS-02, NF-82, E06/F02/US02/T01, E17/F01/US01/T02): one row for each document issued (a booking with the receipt of
/// its owner and the invoice of the agency has two), so the database refuses a second row of the same document (<see cref="BookingDocumentIssuer.DocumentIndexName"/>);
/// the booking is indexed, no longer unique. The row goes with its booking (cascade). The document is a plain column (the legacy rows of a lock made before
/// any document have none).
/// </summary>
internal sealed class BookingDocumentIssuerConfiguration : IEntityTypeConfiguration<BookingDocumentIssuer>
{
    public void Configure(EntityTypeBuilder<BookingDocumentIssuer> entity)
    {
        entity.HasOne<Booking>().WithMany().HasForeignKey(i => i.BookingId).OnDelete(DeleteBehavior.Cascade);
        entity.HasIndex(i => i.BookingId);
        entity.HasIndex(i => i.LockedByDocumentId)
            .IsUnique()
            .HasFilter("\"LockedByDocumentId\" IS NOT NULL")
            .HasDatabaseName(BookingDocumentIssuer.DocumentIndexName);
    }
}

/// <summary>
/// The fiscal documents (NF-83, NF-84, E06/F01/US02/T01, E11/F01/US01/T01, E17/F01/US01/T01). Kept ten years (ADR-033-R7): the booking, the property, the
/// owner, the statement version, the issuer profile, the credited and the replaced document are <b>restrict</b> foreign keys, so neither the removal of a
/// booking nor anything else can delete a document by cascade. The database refuses two documents with the same number in one org, issuer and series, two
/// imported rows of one Octorate document and two live owner invoices of one owner and period, accepts only the known SDI states, and keeps the number of a replacement
/// closed as «non più dovuta» (FIX/BE-TD17-JOB-EXIT: <c>CK_FiscalDocuments_ClosedNotDue</c>). The recipient columns, the issuer snapshot, the XML and the note of that closing get
/// their converter from <c>EncryptedColumns</c> (no length); <c>xmin</c> is the concurrency token.
/// </summary>
internal sealed class FiscalDocumentConfiguration : IEntityTypeConfiguration<FiscalDocument>
{
    public void Configure(EntityTypeBuilder<FiscalDocument> entity)
    {
        entity.HasOne<Booking>().WithMany().HasForeignKey(d => d.BookingId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne<OrgFiscalIssuerProfile>().WithMany().HasForeignKey(d => d.IssuerProfileId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne<Property>().WithMany().HasForeignKey(d => d.PropertyId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne<PropertyOwner>().WithMany().HasForeignKey(d => d.RecipientOwnerId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne<OwnerStatementVersion>().WithMany().HasForeignKey(d => d.OwnerStatementVersionId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne<FiscalDocument>().WithMany().HasForeignKey(d => d.CreditedDocumentId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne<FiscalDocument>().WithMany().HasForeignKey(d => d.ReplacesDocumentId).OnDelete(DeleteBehavior.Restrict);
        entity.HasMany(d => d.Lines).WithOne().HasForeignKey(l => l.DocumentId).OnDelete(DeleteBehavior.Cascade);

        // One live owner invoice per owner and period (E14): neither a discarded draft, nor a credit note, nor an invoice credited in full holds it.
        entity.HasIndex(d => new { d.OrgId, d.RecipientOwnerId, d.PeriodYear, d.PeriodMonth })
            .IsUnique()
            .HasFilter(
                $"\"Purpose\" = {(int)FiscalDocumentPurpose.OwnerInvoice} AND \"Status\" <> {(int)FiscalDocumentStatus.Discarded} "
                + $"AND \"Kind\" <> {(int)FiscalDocumentKind.CreditNote} AND NOT \"CreditedInFull\" AND \"RecipientOwnerId\" IS NOT NULL")
            .HasDatabaseName(FiscalDocument.OwnerPeriodIndexName);

        entity.HasOne<GuestHostExtraOrder>().WithMany().HasForeignKey(d => d.SupplementForExtraOrderId).OnDelete(DeleteBehavior.Restrict);
        entity.HasIndex(d => d.SupplementForExtraOrderId).IsUnique()
            .HasFilter("\"SupplementForExtraOrderId\" IS NOT NULL AND \"Status\" <> 3");
        entity.HasIndex(d => new { d.OrgId, d.IssuerProfileId, d.Series, d.SequenceNumber })
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

            // FIX/BE-TD17-JOB-EXIT: a replacement closed as «non più dovuta» is all or nothing: a document that was not closed has none of the four columns of the closing, and a closed one has its reason and its
            // day, is discarded and KEEPS its sequence number (the number is reserved and never freed: with the unique index of the series, no other document can take it).
            table.HasCheckConstraint(
                "CK_FiscalDocuments_ClosedNotDue",
                "(\"ClosedNotDueAt\" IS NULL AND \"ClosedNotDueReason\" IS NULL AND \"ClosedNotDueByUserId\" IS NULL AND \"ClosedNotDueNote\" IS NULL) "
                + "OR (\"ClosedNotDueAt\" IS NOT NULL AND \"ClosedNotDueReason\" IS NOT NULL AND \"SequenceNumber\" IS NOT NULL "
                + $"AND \"Status\" = {(int)FiscalDocumentStatus.Discarded})");
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

/// <summary>The counter of the fiscal numbers: one row per org, issuer and series (<see cref="FiscalNumberSequence.IssuerSeriesIndexName"/>).</summary>
internal sealed class FiscalNumberSequenceConfiguration : IEntityTypeConfiguration<FiscalNumberSequence>
{
    public void Configure(EntityTypeBuilder<FiscalNumberSequence> entity)
    {
        entity.HasIndex(s => new { s.OrgId, s.IssuerProfileId, s.Series }).IsUnique().HasDatabaseName(FiscalNumberSequence.IssuerSeriesIndexName);
        entity.ToTable(table => table.HasCheckConstraint("CK_FiscalNumberSequences_LastNumber", "\"LastNumber\" >= 0"));
    }
}
