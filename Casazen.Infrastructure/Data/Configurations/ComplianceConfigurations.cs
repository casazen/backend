using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Casazen.Infrastructure.Data.Configurations;

internal sealed class AlloggiatiWebReportConfiguration : IEntityTypeConfiguration<AlloggiatiWebReport>
{
    public void Configure(EntityTypeBuilder<AlloggiatiWebReport> entity)
    {
        entity.HasOne(r => r.Booking)
            .WithMany(b => b.AlloggiatiWebReports)
            .HasForeignKey(r => r.BookingId)
            .OnDelete(DeleteBehavior.Restrict);

        entity.HasOne(r => r.Guest)
            .WithMany(g => g.AlloggiatiWebReports)
            .HasForeignKey(r => r.GuestId)
            .OnDelete(DeleteBehavior.Restrict);

        entity.ToTable(t => t.HasCheckConstraint(
                "CK_AlloggiatiWebReports_SentRequiresReceipt",
                $"\"Status\" <> {(int)AlloggiatiWebStatus.Inviato} OR btrim(coalesce(\"ConfirmationNumber\", '')) <> ''"));

        entity.HasOne<AlloggiatiReceipt>()
            .WithMany()
            .HasForeignKey(r => r.ReceiptId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.SetNull);
    }

    internal static void ConfigureOrgIdIndex(EntityTypeBuilder<AlloggiatiWebReport> entity)
    {
        entity.HasIndex(r => r.OrgId);
    }

    internal static void ConfigureOrgIdForeignKey(EntityTypeBuilder<AlloggiatiWebReport> entity)
    {
        entity.HasOne<Org>().WithMany().HasForeignKey(r => r.OrgId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class StayAlertStateConfiguration : IEntityTypeConfiguration<StayAlertState>
{
    public void Configure(EntityTypeBuilder<StayAlertState> entity)
    {
        entity.HasOne(s => s.Booking)
            .WithMany()
            .HasForeignKey(s => s.BookingId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class CinAlertStateConfiguration : IEntityTypeConfiguration<CinAlertState>
{
    public void Configure(EntityTypeBuilder<CinAlertState> entity)
    {
        entity.HasOne(s => s.Property)
            .WithMany()
            .HasForeignKey(s => s.PropertyId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class AlloggiatiCodeEntryConfiguration : IEntityTypeConfiguration<AlloggiatiCodeEntry>
{
    public void Configure(EntityTypeBuilder<AlloggiatiCodeEntry> entity)
    {
        entity.HasOne(e => e.Import)
            .WithMany()
            .HasForeignKey(e => e.ImportId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class SwissLocalityConfiguration : IEntityTypeConfiguration<SwissLocality>
{
    public void Configure(EntityTypeBuilder<SwissLocality> locality)
    {
        locality.HasKey(l => new { l.PostalCode, l.AddOn, l.MunicipalityCode, l.Name });
        locality.HasIndex(l => l.NormalizedName);
        locality.HasIndex(l => l.MunicipalityCode);
    }
}

internal sealed class TouristTaxRateConfiguration : IEntityTypeConfiguration<TouristTaxRate>
{
    public void Configure(EntityTypeBuilder<TouristTaxRate> entity)
    {
        entity.HasIndex(t => t.City);
        entity.HasIndex(t => new { t.City, t.IsActive, t.EffectiveFrom });
        entity.Property(t => t.VerificationLevel)
            .HasConversion<string>()
            .HasMaxLength(20);
        entity.HasIndex(t => t.IstatCode);
        entity.Property(t => t.CalculationMethod)
            .HasConversion<string>()
            .HasMaxLength(30)
            .HasDefaultValue(TouristTaxCalculationMethod.PerPersonPerNight)
            .HasSentinel((TouristTaxCalculationMethod)(-1));
    }
}

internal sealed class BiometricVerificationLogConfiguration : IEntityTypeConfiguration<BiometricVerificationLog>
{
    public void Configure(EntityTypeBuilder<BiometricVerificationLog> entity)
    {
        entity.Property(l => l.Method)
            .HasDefaultValue(VerificationMethod.Biometric)
            .HasSentinel((VerificationMethod)(-1));
        entity.Property(l => l.ReasonsJson)
            .HasDefaultValueSql("'[]'::jsonb");

        // E70/F01/US01/T02 (NF-194, ADR-013-R18, R19): how the self-hosted policy decided. The rows written before the columns read as the legacy path (DecisionMode
        // Legacy, 0). The key of an attempt is unique per booking, the last line of defence of the idempotent closure (two processes, one key, one row); the rows of
        // the legacy path have no key and the filter leaves them out. The index on the booking alone stays declared: the readers of the last row of a booking need it.
        entity.Property(l => l.DecisionMode)
            .HasDefaultValue(IdentityDecisionMode.Legacy);
        entity.HasIndex(l => l.BookingId);
        entity.HasIndex(l => new { l.BookingId, l.IdempotencyKey })
            .IsUnique()
            .HasFilter("\"IdempotencyKey\" IS NOT NULL")
            .HasDatabaseName(BiometricVerificationLog.IdempotencyIndexName);
    }
}

internal sealed class HighTensionAreaComuneConfiguration : IEntityTypeConfiguration<HighTensionAreaComune>
{
    public void Configure(EntityTypeBuilder<HighTensionAreaComune> entity)
    {
        entity.HasIndex(c => c.Comune);
    }
}

internal sealed class ComuneImuChannelConfiguration : IEntityTypeConfiguration<ComuneImuChannel>
{
    public void Configure(EntityTypeBuilder<ComuneImuChannel> entity)
    {
        entity.HasIndex(c => c.Comune);
    }
}

internal sealed class RegulatoryDataAuditEntryConfiguration : IEntityTypeConfiguration<RegulatoryDataAuditEntry>
{
    public void Configure(EntityTypeBuilder<RegulatoryDataAuditEntry> entity)
    {
        entity.HasIndex(e => new { e.EntityId, e.OccurredAt });
    }
}

internal sealed class TouristTaxRemittanceConfiguration : IEntityTypeConfiguration<TouristTaxRemittance>
{
    public void Configure(EntityTypeBuilder<TouristTaxRemittance> entity)
    {
        // ─── Tourist tax paid to the comuni (NF-100, REP-05) ───────────────────────
        // The «paid» half of the summary per comune. A comune can be paid in instalments, so the payments of the same comune and period are
        // not unique: the index only serves the summary, which reads the payments of an org by comune and by the period they lie in. A
        // fiscal proof must not disappear with a row of the org (RESTRICT), and the amount and the period are checked by the database too:
        // a payment is above zero and starts no later than it ends.
        entity.HasOne<Org>()
            .WithMany()
            .HasForeignKey(r => r.OrgId)
            .OnDelete(DeleteBehavior.Restrict);
        entity.HasIndex(r => new { r.OrgId, r.ComuneKey, r.PeriodFrom, r.PeriodTo });
        entity.ToTable(t =>
        {
            t.HasCheckConstraint("CK_TouristTaxRemittances_PaidAmountPositive", "\"PaidAmount\" > 0");
            t.HasCheckConstraint("CK_TouristTaxRemittances_PeriodOrder", "\"PeriodFrom\" <= \"PeriodTo\"");
        });
    }
}
