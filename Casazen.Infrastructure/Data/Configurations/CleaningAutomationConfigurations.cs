using Casazen.Core.Entities;
using Casazen.Core.Suppliers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Property = Casazen.Core.Entities.Property;

namespace Casazen.Infrastructure.Data.Configurations;

/// <summary>
/// The default cleaner of a property (E24/F01/US01/T01, NF-120): one row for each property, following its property, its org and its supplier with a cascade (a
/// setting never outlives what it belongs to, and never blocks their erasure). The kind of cleaner is stored as its number; the database keeps the supplier and the
/// staff member apart, a supplier priced and a price inside the bound of a service request, and a duration inside the bounds of the catalog, so a row written by
/// hand cannot be one the automation would refuse.
/// </summary>
internal sealed class PropertyCleaningSetupConfiguration : IEntityTypeConfiguration<PropertyCleaningSetup>
{
    public void Configure(EntityTypeBuilder<PropertyCleaningSetup> entity)
    {
        entity.Property(s => s.CleanerKind).HasConversion<int>();
        entity.HasOne<Property>().WithMany().HasForeignKey(s => s.PropertyId).OnDelete(DeleteBehavior.Cascade);
        entity.HasOne<Org>().WithMany().HasForeignKey(s => s.OrgId).OnDelete(DeleteBehavior.Cascade);
        entity.HasOne<Org>().WithMany().HasForeignKey(s => s.SupplierOrgId).IsRequired(false).OnDelete(DeleteBehavior.Cascade);
        entity.HasIndex(s => s.PropertyId).IsUnique().HasDatabaseName(PropertyCleaningSetup.PropertyIndexName);
        entity.HasIndex(s => s.OrgId);
        entity.HasIndex(s => s.SupplierOrgId);
        entity.ToTable(table =>
        {
            table.HasCheckConstraint(
                PropertyCleaningSetup.CleanerCheckName,
                "(\"CleanerKind\" = 1 AND \"SupplierOrgId\" IS NOT NULL AND \"StaffUserId\" IS NULL AND \"PriceCents\" IS NOT NULL "
                + $"AND \"PriceCents\" BETWEEN 1 AND {ServiceRequestLimits.MaxAmountCents}) "
                + "OR (\"CleanerKind\" = 2 AND \"StaffUserId\" IS NOT NULL AND \"SupplierOrgId\" IS NULL "
                + $"AND (\"PriceCents\" IS NULL OR \"PriceCents\" BETWEEN 1 AND {ServiceRequestLimits.MaxAmountCents}))");
            table.HasCheckConstraint(
                "CK_PropertyCleaningSetups_Duration",
                $"\"DurationMinutes\" IS NULL OR \"DurationMinutes\" BETWEEN {SupplierServiceCatalogLimits.MinDurationMinutes} AND {SupplierServiceCatalogLimits.MaxDurationMinutes}");
        });
    }
}

/// <summary>
/// The automatic cleaning of a stay (E24/F01/US01/T02, NF-120): one row for each stay, the key of the idempotency of the automation, following its booking, its
/// property and its org with a cascade. The request and the activity it points at are set to null if they are removed for good. The index on the property serves
/// the cascade of a property and the reads of the changes of a stay.
/// </summary>
internal sealed class StayCleaningConfiguration : IEntityTypeConfiguration<StayCleaning>
{
    public void Configure(EntityTypeBuilder<StayCleaning> entity)
    {
        entity.Property(c => c.CleanerKind).HasConversion<int>();
        entity.HasOne<Booking>().WithMany().HasForeignKey(c => c.BookingId).OnDelete(DeleteBehavior.Cascade);
        entity.HasOne<Property>().WithMany().HasForeignKey(c => c.PropertyId).OnDelete(DeleteBehavior.Cascade);
        entity.HasOne<Org>().WithMany().HasForeignKey(c => c.OrgId).OnDelete(DeleteBehavior.Cascade);
        entity.HasOne<ServiceRequest>().WithMany().HasForeignKey(c => c.ServiceRequestId).IsRequired(false).OnDelete(DeleteBehavior.SetNull);
        entity.HasOne<InternalActivity>().WithMany().HasForeignKey(c => c.InternalActivityId).IsRequired(false).OnDelete(DeleteBehavior.SetNull);
        entity.HasIndex(c => new { c.OrgId, c.BookingId }).IsUnique().HasDatabaseName(StayCleaning.BookingIndexName);
        entity.HasIndex(c => c.PropertyId);
        entity.HasIndex(c => c.ServiceRequestId);
        entity.HasIndex(c => c.InternalActivityId);
    }
}
