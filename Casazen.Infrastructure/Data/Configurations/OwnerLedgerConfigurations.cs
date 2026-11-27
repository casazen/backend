using Casazen.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Casazen.Infrastructure.Data.Configurations;

/// <summary>
/// The append-only owner ledger (E06/F05/US02/T02, AC-NF110-1): one key per org (the same facts are posted once), a line reversed at most once, the
/// owner, the property and the reversed line restrict (a line is evidence).
/// </summary>
internal sealed class OwnerLedgerEntryConfiguration : IEntityTypeConfiguration<OwnerLedgerEntry>
{
    public void Configure(EntityTypeBuilder<OwnerLedgerEntry> entity)
    {
        entity.HasOne<PropertyOwner>().WithMany().HasForeignKey(e => e.PropertyOwnerId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne<Property>().WithMany().HasForeignKey(e => e.PropertyId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne<OwnerLedgerEntry>().WithMany().HasForeignKey(e => e.ReversesEntryId).OnDelete(DeleteBehavior.Restrict);
        entity.HasIndex(e => new { e.OrgId, e.IdempotencyKey }).IsUnique().HasDatabaseName(OwnerLedgerEntry.IdempotencyIndexName);
        entity.HasIndex(e => e.ReversesEntryId).IsUnique().HasFilter("\"ReversesEntryId\" IS NOT NULL").HasDatabaseName(OwnerLedgerEntry.ReversalIndexName);
        entity.HasIndex(e => new { e.OrgId, e.PropertyOwnerId, e.PeriodYear, e.PeriodMonth });
        entity.HasIndex(e => new { e.OrgId, e.SourceType, e.SourceId });
        entity.ToTable(table =>
        {
            table.HasCheckConstraint("CK_OwnerLedgerEntries_PeriodMonth", "\"PeriodMonth\" BETWEEN 1 AND 12");
            table.HasCheckConstraint("CK_OwnerLedgerEntries_Share", "\"SharePercent\" >= 0 AND \"SharePercent\" <= 100");
            table.HasCheckConstraint("CK_OwnerLedgerEntries_FxRate", "\"FxRate\" > 0");
        });
    }
}

/// <summary>The versions of the owner statements (E06/F05/US02/T02, AC-NF110-2): one number per owner and month, guarded by the row version.</summary>
internal sealed class OwnerStatementVersionConfiguration : IEntityTypeConfiguration<OwnerStatementVersion>
{
    public void Configure(EntityTypeBuilder<OwnerStatementVersion> entity)
    {
        entity.HasOne<PropertyOwner>().WithMany().HasForeignKey(v => v.PropertyOwnerId).OnDelete(DeleteBehavior.Restrict);
        entity.HasIndex(v => new { v.OrgId, v.PropertyOwnerId, v.PeriodYear, v.PeriodMonth, v.VersionNumber }).IsUnique()
            .HasDatabaseName(OwnerStatementVersion.VersionIndexName);
        entity.Property(v => v.RowVersion).IsRowVersion();
        entity.ToTable(table =>
        {
            table.HasCheckConstraint("CK_OwnerStatementVersions_PeriodMonth", "\"PeriodMonth\" BETWEEN 1 AND 12");
            table.HasCheckConstraint("CK_OwnerStatementVersions_VersionNumber", "\"VersionNumber\" >= 1");
        });
    }
}

/// <summary>The months of the owners (E06/F05/US02/T02, AC-NF110-3): one row per owner and month, guarded by the row version.</summary>
internal sealed class OwnerClosePeriodConfiguration : IEntityTypeConfiguration<OwnerClosePeriod>
{
    public void Configure(EntityTypeBuilder<OwnerClosePeriod> entity)
    {
        entity.HasOne<PropertyOwner>().WithMany().HasForeignKey(p => p.PropertyOwnerId).OnDelete(DeleteBehavior.Restrict);
        entity.HasIndex(p => new { p.OrgId, p.PropertyOwnerId, p.PeriodYear, p.PeriodMonth }).IsUnique().HasDatabaseName(OwnerClosePeriod.PeriodIndexName);
        entity.Property(p => p.RowVersion).IsRowVersion();
        entity.ToTable(table => table.HasCheckConstraint("CK_OwnerClosePeriods_PeriodMonth", "\"PeriodMonth\" BETWEEN 1 AND 12"));
    }
}

/// <summary>The charges and expenses of the owners (E06/F05/US02/T02): the property and the owner restrict, a positive amount, a valid range.</summary>
internal sealed class OwnerChargeConfiguration : IEntityTypeConfiguration<OwnerCharge>
{
    public void Configure(EntityTypeBuilder<OwnerCharge> entity)
    {
        entity.HasOne<Property>().WithMany().HasForeignKey(c => c.PropertyId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne<PropertyOwner>().WithMany().HasForeignKey(c => c.PropertyOwnerId).OnDelete(DeleteBehavior.Restrict);
        entity.HasIndex(c => new { c.OrgId, c.PropertyId, c.Status });
        entity.ToTable(table =>
        {
            table.HasCheckConstraint("CK_OwnerCharges_Amount", "\"Amount\" > 0");
            table.HasCheckConstraint("CK_OwnerCharges_Validity", "\"EffectiveTo\" IS NULL OR \"EffectiveTo\" >= \"EffectiveFrom\"");
        });
    }
}

/// <summary>
/// The links to the invoices of the external fiscal systems (E06/F01/US02/T05): the version and the owner restrict; one live link per version, provider
/// and external id.
/// </summary>
internal sealed class OwnerExternalDocumentReferenceConfiguration : IEntityTypeConfiguration<OwnerExternalDocumentReference>
{
    public void Configure(EntityTypeBuilder<OwnerExternalDocumentReference> entity)
    {
        entity.HasOne<OwnerStatementVersion>().WithMany().HasForeignKey(r => r.OwnerStatementVersionId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne<PropertyOwner>().WithMany().HasForeignKey(r => r.PropertyOwnerId).OnDelete(DeleteBehavior.Restrict);
        entity.HasIndex(r => new { r.OrgId, r.OwnerStatementVersionId, r.Provider, r.ExternalId }).IsUnique()
            .HasFilter("\"RemovedAt\" IS NULL")
            .HasDatabaseName("UIX_OwnerExternalDocumentReferences_Live");
        entity.HasIndex(r => new { r.OrgId, r.PropertyOwnerId });
    }
}

/// <summary>The payouts to the owners (E06/F05/US02/T03): one per org and key, the owner and the version restrict.</summary>
internal sealed class OwnerPayoutConfiguration : IEntityTypeConfiguration<OwnerPayout>
{
    public void Configure(EntityTypeBuilder<OwnerPayout> entity)
    {
        entity.HasOne<PropertyOwner>().WithMany().HasForeignKey(p => p.PropertyOwnerId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne<OwnerStatementVersion>().WithMany().HasForeignKey(p => p.OwnerStatementVersionId).OnDelete(DeleteBehavior.Restrict);
        entity.HasIndex(p => new { p.OrgId, p.IdempotencyKey }).IsUnique().HasDatabaseName(OwnerPayout.IdempotencyIndexName);
        entity.HasIndex(p => new { p.OrgId, p.PropertyOwnerId, p.OwnerStatementVersionId });
        entity.Property(p => p.RowVersion).IsRowVersion();
        entity.ToTable(table => table.HasCheckConstraint(
            "CK_OwnerPayouts_Amounts", "\"GrossAmount\" > 0 AND \"WithholdingAmount\" >= 0 AND \"NetAmount\" = \"GrossAmount\" - \"WithholdingAmount\""));
    }
}

/// <summary>The withholding evidence (E06/F05/US02/T03): append-only, one decision per payout, a decision reversed at most once.</summary>
internal sealed class TaxObligationEvidenceConfiguration : IEntityTypeConfiguration<TaxObligationEvidence>
{
    public void Configure(EntityTypeBuilder<TaxObligationEvidence> entity)
    {
        entity.HasOne<OwnerPayout>().WithMany().HasForeignKey(e => e.OwnerPayoutId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne<PropertyOwner>().WithMany().HasForeignKey(e => e.PropertyOwnerId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne<OwnerStatementVersion>().WithMany().HasForeignKey(e => e.OwnerStatementVersionId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne<TaxObligationEvidence>().WithMany().HasForeignKey(e => e.ReversesEvidenceId).OnDelete(DeleteBehavior.Restrict);
        entity.HasIndex(e => new { e.OrgId, e.IdempotencyKey }).IsUnique().HasDatabaseName(TaxObligationEvidence.IdempotencyIndexName);
        entity.HasIndex(e => e.ReversesEvidenceId).IsUnique().HasFilter("\"ReversesEvidenceId\" IS NOT NULL")
            .HasDatabaseName(TaxObligationEvidence.ReversalIndexName);
        entity.HasIndex(e => new { e.OrgId, e.PeriodYear, e.PeriodMonth });
        entity.ToTable(table => table.HasCheckConstraint(
            "CK_TaxObligationEvidence_Decision", "(\"Decision\" = 1 AND \"Amount\" IS NOT NULL) OR (\"Decision\" = 2 AND \"Amount\" IS NULL)"));
    }
}

/// <summary>The annual tax datasets (E06/F05/US02/T03): one per org, kind and year.</summary>
internal sealed class TaxReportingDatasetConfiguration : IEntityTypeConfiguration<TaxReportingDataset>
{
    public void Configure(EntityTypeBuilder<TaxReportingDataset> entity)
    {
        entity.HasIndex(d => new { d.OrgId, d.Kind, d.Year }).IsUnique().HasDatabaseName(TaxReportingDataset.KindYearIndexName);
        entity.Property(d => d.RowVersion).IsRowVersion();
    }
}

/// <summary>The exchange rates of the owner accounting (E06/F05/US02/T02): one per org, pair and day, positive.</summary>
internal sealed class OwnerFxRateConfiguration : IEntityTypeConfiguration<OwnerFxRate>
{
    public void Configure(EntityTypeBuilder<OwnerFxRate> entity)
    {
        entity.HasIndex(r => new { r.OrgId, r.BaseCurrency, r.QuoteCurrency, r.RateDate }).IsUnique().HasDatabaseName(OwnerFxRate.RateIndexName);
        entity.ToTable(table => table.HasCheckConstraint("CK_OwnerFxRates_Rate", "\"Rate\" > 0"));
    }
}
