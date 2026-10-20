using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Suppliers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Casazen.Infrastructure.Data.Configurations;

internal sealed class SupplierServiceListingConfiguration : IEntityTypeConfiguration<SupplierServiceListing>
{
    public void Configure(EntityTypeBuilder<SupplierServiceListing> entity)
    {
        entity.HasOne(l => l.SupplierProfile)
            .WithMany()
            .HasForeignKey(l => l.OrgId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.Property(l => l.Version).IsRowVersion();

        entity.HasIndex(l => new { l.OrgId, l.Slug })
            .IsUnique()
            .HasFilter("\"DeletedAt\" IS NULL")
            .HasDatabaseName("UIX_SupplierServiceListings_OrgId_Slug");

        entity.ToTable(t =>
        {
            t.HasCheckConstraint(
                "CK_SupplierServiceListings_PriceFromCents", "\"PriceFromCents\" IS NULL OR \"PriceFromCents\" > 0");
            t.HasCheckConstraint(
                "CK_SupplierServiceListings_DurationMinutes", "\"DurationMinutes\" IS NULL OR \"DurationMinutes\" > 0");
            t.HasCheckConstraint(
                "CK_SupplierServiceListings_MinNoticeHours", "\"MinNoticeHours\" IS NULL OR \"MinNoticeHours\" >= 0");
            t.HasCheckConstraint(
                "CK_SupplierServiceListings_WeekdaysMask", "\"WeekdaysMask\" BETWEEN 0 AND 127");
        });
    }
}
