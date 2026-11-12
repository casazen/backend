using Casazen.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Casazen.Infrastructure.Data.Configurations;

/// <summary>
/// The rules of the owner stays (E07/F07/US01/T01): one row for the property and at most one for each of its owners (two filtered unique indexes), the
/// values bounded by check constraints, the blocked dates as jsonb. The rows go with their property (cascade); the org restricts.
/// </summary>
internal sealed class OwnerStayRuleConfiguration : IEntityTypeConfiguration<OwnerStayRule>
{
    public void Configure(EntityTypeBuilder<OwnerStayRule> entity)
    {
        entity.HasOne<Org>().WithMany().HasForeignKey(r => r.OrgId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne<Property>().WithMany().HasForeignKey(r => r.PropertyId).OnDelete(DeleteBehavior.Cascade);
        entity.HasOne<PropertyOwner>().WithMany().HasForeignKey(r => r.PropertyOwnerId).OnDelete(DeleteBehavior.Cascade);
        entity.Property(r => r.BlackoutsJson).HasColumnType("jsonb");
        entity.HasIndex(r => r.PropertyId).IsUnique().HasFilter("\"PropertyOwnerId\" IS NULL").HasDatabaseName("UIX_OwnerStayRules_Property");
        entity.HasIndex(r => new { r.PropertyId, r.PropertyOwnerId }).IsUnique().HasFilter("\"PropertyOwnerId\" IS NOT NULL")
            .HasDatabaseName("UIX_OwnerStayRules_Property_Owner");
        entity.HasIndex(r => r.OrgId);
        entity.ToTable(table =>
        {
            table.HasCheckConstraint("CK_OwnerStayRules_Allowance", "\"YearlyNightAllowance\" IS NULL OR \"YearlyNightAllowance\" BETWEEN 0 AND 366");
            table.HasCheckConstraint("CK_OwnerStayRules_Advance", "\"MinAdvanceDays\" >= 0 AND (\"MaxAdvanceDays\" IS NULL OR \"MaxAdvanceDays\" >= \"MinAdvanceDays\")");
            table.HasCheckConstraint("CK_OwnerStayRules_Nights", "\"MinNights\" >= 1 AND (\"MaxNights\" IS NULL OR \"MaxNights\" >= \"MinNights\")");
            table.HasCheckConstraint("CK_OwnerStayRules_Period", "\"ActiveFrom\" IS NULL OR \"ActiveUntil\" IS NULL OR \"ActiveUntil\" >= \"ActiveFrom\"");
        });
    }
}

/// <summary>
/// The owner stays (E07/F07/US01/T01): the property and the owner profile restrict (a stay is history and audit), the departure after the arrival.
/// The reconciliation reads the stays by state; the calendar and the conflicts read them by property and dates.
/// </summary>
internal sealed class OwnerStayRequestConfiguration : IEntityTypeConfiguration<OwnerStayRequest>
{
    public void Configure(EntityTypeBuilder<OwnerStayRequest> entity)
    {
        entity.HasOne<Org>().WithMany().HasForeignKey(s => s.OrgId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne<Property>().WithMany().HasForeignKey(s => s.PropertyId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne<PropertyOwner>().WithMany().HasForeignKey(s => s.PropertyOwnerId).OnDelete(DeleteBehavior.Restrict);
        entity.HasIndex(s => new { s.PropertyId, s.Status, s.StartDate });
        entity.HasIndex(s => new { s.OrgId, s.PropertyOwnerId, s.StartDate });
        entity.HasIndex(s => new { s.Status, s.EndDate });
        entity.HasIndex(s => s.CalendarBlockId).IsUnique().HasFilter("\"CalendarBlockId\" IS NOT NULL");
        entity.ToTable(table => table.HasCheckConstraint("CK_OwnerStayRequests_Dates", "\"EndDate\" > \"StartDate\" AND \"Nights\" >= 1"));
    }
}
