using Casazen.Core.Entities;
using Casazen.Core.Options;
using Casazen.Core.Suppliers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Casazen.Infrastructure.Data.Configurations;

internal sealed class SupplierProfileConfiguration : IEntityTypeConfiguration<SupplierProfile>
{
    public void Configure(EntityTypeBuilder<SupplierProfile> entity)
    {
        // ─── Supplier console (US-022 / #292) ────────────────────────────────────
        entity.HasOne(sp => sp.Org)
            .WithOne()
            .HasForeignKey<SupplierProfile>(sp => sp.OrgId)
            .OnDelete(DeleteBehavior.Restrict);

        entity.HasIndex(sp => sp.Status);

        // SU-04: comuni chosen from the official list. A column added to a table with rows: the profiles that exist start
        // with none (nothing is inferred from the free-text ComuniJson).
        entity.Property(sp => sp.ComuneIstatCodesJson)
            .HasDefaultValueSql("'[]'::jsonb");

        entity.HasIndex(sp => sp.ClaimTokenHash)
            .IsUnique()
            .HasDatabaseName("UIX_SupplierProfiles_ClaimTokenHash");

        // SU-13: the slug of the public showcase is unique (profiles without one are not constrained).
        entity.HasIndex(sp => sp.ShowcaseSlug)
            .IsUnique()
            .HasFilter("\"ShowcaseSlug\" IS NOT NULL")
            .HasDatabaseName("UIX_SupplierProfiles_ShowcaseSlug");
    }

    internal static void ConfigureTable(EntityTypeBuilder<SupplierProfile> entity)
    {
        // ─── Payment of the service requests inside CasaZen (SP-15a) ─────────────
        // The commission a supplier is charged instead of the platform's (an admin sets it, SP-15b): a percentage, 0 to 50.
        entity.ToTable(t =>
            t.HasCheckConstraint(
                "CK_SupplierProfiles_CommissionPercentOverride",
                $"\"CommissionPercentOverride\" IS NULL OR \"CommissionPercentOverride\" BETWEEN 0 AND {(int)SupplierPaymentsOptions.MaxCommissionPercent}"));
    }

    internal static void ConfigureTable2(EntityTypeBuilder<SupplierProfile> entity)
    {
        // ─── Refunds of the service payments and the commission period (SP-15b) ─────────────
        // The end of a commission period only makes sense with the percentage it ends.
        entity.ToTable(t =>
            t.HasCheckConstraint(
                "CK_SupplierProfiles_CommissionOverrideUntil",
                "\"CommissionOverrideUntil\" IS NULL OR \"CommissionPercentOverride\" IS NOT NULL"));
    }
}

internal sealed class SupplierAvailabilityConfiguration : IEntityTypeConfiguration<SupplierAvailability>
{
    public void Configure(EntityTypeBuilder<SupplierAvailability> entity)
    {
        entity.HasOne(sa => sa.SupplierProfile)
            .WithMany()
            .HasForeignKey(sa => sa.OrgId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasIndex(sa => new { sa.OrgId, sa.Date })
            .IsUnique();
    }
}

internal sealed class SupplierInviteRecordConfiguration : IEntityTypeConfiguration<SupplierInviteRecord>
{
    public void Configure(EntityTypeBuilder<SupplierInviteRecord> entity)
    {
        entity.HasIndex(i => i.Email);

        entity.HasIndex(i => new { i.Email, i.IsUsed });

        entity.HasIndex(i => i.TokenHash)
            .IsUnique()
            .HasDatabaseName("UIX_SupplierInviteRecords_TokenHash");
    }
}

internal sealed class SupplierAdminAuditEntryConfiguration : IEntityTypeConfiguration<SupplierAdminAuditEntry>
{
    public void Configure(EntityTypeBuilder<SupplierAdminAuditEntry> entity)
    {
        // Audit trail of the admin actions on suppliers and invites (SU-12): read per supplier, newest first. No foreign
        // key: the trail outlives a supplier org deleted by the fix-orphaned repair.
        entity.HasIndex(e => new { e.SupplierOrgId, e.OccurredAt });
    }
}

internal sealed class ServiceRequestConfiguration : IEntityTypeConfiguration<ServiceRequest>
{
    public void Configure(EntityTypeBuilder<ServiceRequest> entity)
    {
        // ─── Micro-marketplace v0 (US-021 / #293) ────────────────────────────────
        entity.HasOne(sr => sr.Org)
            .WithMany()
            .HasForeignKey(sr => sr.OrgId)
            .OnDelete(DeleteBehavior.Restrict);

        entity.HasOne(sr => sr.Property)
            .WithMany()
            .HasForeignKey(sr => sr.PropertyId)
            .OnDelete(DeleteBehavior.Restrict);

        entity.HasOne(sr => sr.SupplierOrg)
            .WithMany()
            .HasForeignKey(sr => sr.SupplierOrgId)
            .OnDelete(DeleteBehavior.Restrict);

        entity.HasOne(sr => sr.Booking)
            .WithMany()
            .HasForeignKey(sr => sr.BookingId)
            .OnDelete(DeleteBehavior.SetNull);

        entity.HasIndex(sr => new { sr.OrgId, sr.Status });

        entity.HasIndex(sr => new { sr.SupplierOrgId, sr.Status });

        // A4-19 (SU-10): Npgsql maps a uint row version to the xmin system column, so every state transition is saved
        // only if the row was not changed since it was read.
        entity.Property(sr => sr.Version)
            .IsRowVersion();

        // SP-04: a request has an optional catalog service (set to null if the service is ever removed for good; the catalog
        // deletes softly and the request keeps its own copy of the name), and the planner and the console read a supplier's
        // requests by the time of the work.
        entity.HasOne<SupplierServiceListing>()
            .WithMany()
            .HasForeignKey(sr => sr.ServiceListingId)
            .OnDelete(DeleteBehavior.SetNull);

        entity.HasIndex(sr => new { sr.SupplierOrgId, sr.ScheduledStartUtc });
    }

    internal static void ConfigureChecksMirrorServiceRequestLimits(EntityTypeBuilder<ServiceRequest> entity)
    {
        // The checks mirror ServiceRequestLimits and the rules of the service: a bad row is refused by the database too. A
        // time has both ends or none, a proposal has its start, end and instant or none, an amount is from 1 cent to the bound.
        entity.ToTable(t =>
        {
            t.HasCheckConstraint(
                "CK_ServiceRequests_ScheduledInterval",
                "(\"ScheduledStartUtc\" IS NULL AND \"ScheduledEndUtc\" IS NULL) OR "
                + "(\"ScheduledStartUtc\" IS NOT NULL AND \"ScheduledEndUtc\" IS NOT NULL AND \"ScheduledEndUtc\" > \"ScheduledStartUtc\")");
            t.HasCheckConstraint(
                "CK_ServiceRequests_ProposedInterval",
                "(\"ProposedStartUtc\" IS NULL AND \"ProposedEndUtc\" IS NULL AND \"ProposedAt\" IS NULL) OR "
                + "(\"ProposedStartUtc\" IS NOT NULL AND \"ProposedEndUtc\" IS NOT NULL AND \"ProposedAt\" IS NOT NULL "
                + "AND \"ProposedEndUtc\" > \"ProposedStartUtc\")");
            t.HasCheckConstraint(
                "CK_ServiceRequests_Amounts",
                $"(\"EstimatedAmountCents\" IS NULL OR \"EstimatedAmountCents\" BETWEEN 1 AND {ServiceRequestLimits.MaxAmountCents}) AND "
                + $"(\"QuotedAmountCents\" IS NULL OR \"QuotedAmountCents\" BETWEEN 1 AND {ServiceRequestLimits.MaxAmountCents}) AND "
                + $"(\"FinalAmountCents\" IS NULL OR \"FinalAmountCents\" BETWEEN 1 AND {ServiceRequestLimits.MaxAmountCents})");
        });
    }
}
