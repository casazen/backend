using Casazen.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Casazen.Infrastructure.Data.Configurations;

internal sealed class RentScheduleConfiguration : IEntityTypeConfiguration<RentSchedule>
{
    public void Configure(EntityTypeBuilder<RentSchedule> entity)
    {
        entity.HasOne(s => s.LeaseContract)
            .WithOne(l => l.RentSchedule)
            .HasForeignKey<RentSchedule>(s => s.LeaseContractId)
            .OnDelete(DeleteBehavior.Restrict);

        entity.HasOne(s => s.Org)
            .WithMany()
            .HasForeignKey(s => s.OrgId)
            .OnDelete(DeleteBehavior.Restrict);

        entity.HasIndex(s => s.LeaseContractId)
            .IsUnique();

        entity.Property(s => s.Amount)
            .HasPrecision(18, 2);

        entity.HasIndex(s => s.OrgId);
    }
}

internal sealed class RentLedgerEntryConfiguration : IEntityTypeConfiguration<RentLedgerEntry>
{
    public void Configure(EntityTypeBuilder<RentLedgerEntry> entity)
    {
        entity.HasOne(e => e.LeaseContract)
            .WithMany()
            .HasForeignKey(e => e.LeaseContractId)
            .OnDelete(DeleteBehavior.Restrict);

        entity.HasOne(e => e.RentSchedule)
            .WithMany(s => s.LedgerEntries)
            .HasForeignKey(e => e.RentScheduleId)
            .OnDelete(DeleteBehavior.Restrict);

        entity.HasOne(e => e.Org)
            .WithMany()
            .HasForeignKey(e => e.OrgId)
            .OnDelete(DeleteBehavior.Restrict);

        entity.HasIndex(e => new { e.LeaseContractId, e.PeriodStart })
            .IsUnique();

        entity.Property(e => e.AmountDue)
            .HasPrecision(18, 2);

        entity.Property(e => e.StampDutyAmount)
            .HasPrecision(18, 2);

        entity.HasIndex(e => e.OrgId);
    }

    internal static void ConfigureOrgIdIndex(EntityTypeBuilder<RentLedgerEntry> entity)
    {
        entity.HasIndex(e => new { e.OrgId, e.DueDate });
    }
}

internal sealed class BankLinkPaymentConfiguration : IEntityTypeConfiguration<BankLinkPayment>
{
    public void Configure(EntityTypeBuilder<BankLinkPayment> entity)
    {
        entity.HasIndex(p => p.StripePaymentIntentId)
            .IsUnique();

        entity.HasIndex(p => p.OrgId);

        entity.HasIndex(p => p.CondominiumInstallmentId);

        entity.HasOne<Org>()
            .WithMany()
            .HasForeignKey(p => p.OrgId)
            .OnDelete(DeleteBehavior.Restrict);

        entity.HasIndex(p => p.LeaseMaintenanceRequestId);

        // One open debit per maintenance request. Failed and canceled rows stay, so a retry can start the next one.
        entity.HasIndex(p => p.LeaseMaintenanceRequestId)
            .IsUnique()
            .HasFilter("\"LeaseMaintenanceRequestId\" IS NOT NULL AND \"Status\" IN (0, 1)")
            .HasDatabaseName("UIX_BankLinkPayments_OpenMaintenanceRequest");
    }

    internal static void ConfigureLeaseMaintenanceRequestIdForeignKey(EntityTypeBuilder<BankLinkPayment> entity)
    {
        entity.HasOne<LeaseMaintenanceRequest>()
            .WithMany()
            .HasForeignKey(p => p.LeaseMaintenanceRequestId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class RentLedgerComponentConfiguration : IEntityTypeConfiguration<RentLedgerComponent>
{
    public void Configure(EntityTypeBuilder<RentLedgerComponent> entity)
    {
        entity.HasOne(c => c.RentLedgerEntry)
            .WithMany(e => e.Components)
            .HasForeignKey(c => c.RentLedgerEntryId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasOne(c => c.Org)
            .WithMany()
            .HasForeignKey(c => c.OrgId)
            .OnDelete(DeleteBehavior.Restrict);

        entity.Property(c => c.Amount)
            .HasPrecision(18, 2);

        entity.Property(c => c.Kind)
            .HasConversion<int>();

        entity.HasIndex(c => c.RentLedgerEntryId);

        entity.HasIndex(c => c.OrgId);

        entity.HasOne<TenantCondominiumSettlement>()
            .WithMany()
            .HasForeignKey(c => c.TenantSettlementId)
            .OnDelete(DeleteBehavior.Restrict);

        entity.HasIndex(c => c.TenantSettlementId)
            .IsUnique()
            .HasFilter("\"TenantSettlementId\" IS NOT NULL");
    }
}

internal sealed class TenantPaymentDeclarationConfiguration : IEntityTypeConfiguration<TenantPaymentDeclaration>
{
    public void Configure(EntityTypeBuilder<TenantPaymentDeclaration> entity)
    {
        entity.HasOne(d => d.LeaseContract)
            .WithMany()
            .HasForeignKey(d => d.LeaseContractId)
            .OnDelete(DeleteBehavior.Restrict);

        entity.HasOne(d => d.RentLedgerEntry)
            .WithMany()
            .HasForeignKey(d => d.RentLedgerEntryId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasIndex(d => new { d.RentLedgerEntryId, d.SubmittedAt });

        entity.HasIndex(d => d.OrgId);
    }
}

internal sealed class RentBankMovementConfiguration : IEntityTypeConfiguration<RentBankMovement>
{
    public void Configure(EntityTypeBuilder<RentBankMovement> entity)
    {
        entity.HasOne(m => m.LeaseContract)
            .WithMany()
            .HasForeignKey(m => m.LeaseContractId)
            .OnDelete(DeleteBehavior.Restrict);

        entity.HasOne(m => m.RentLedgerEntry)
            .WithMany()
            .HasForeignKey(m => m.RentLedgerEntryId)
            .OnDelete(DeleteBehavior.Restrict);

        entity.HasOne(m => m.Org)
            .WithMany()
            .HasForeignKey(m => m.OrgId)
            .OnDelete(DeleteBehavior.Restrict);

        entity.Property(m => m.Amount)
            .HasPrecision(18, 2);

        entity.HasIndex(m => new { m.OrgId, m.Fingerprint })
            .IsUnique();

        entity.HasIndex(m => new { m.OrgId, m.PayerIbanHash });
        entity.HasIndex(m => new { m.OrgId, m.PayerNameHash });
        entity.HasIndex(m => m.LeaseContractId);
        entity.HasIndex(m => m.RentLedgerEntryId);
    }
}
