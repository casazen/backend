using Casazen.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Property = Casazen.Core.Entities.Property;

namespace Casazen.Infrastructure.Data.Configurations;

internal sealed class PropertyRoomConfiguration : IEntityTypeConfiguration<PropertyRoom>
{
    public void Configure(EntityTypeBuilder<PropertyRoom> entity)
    {
        entity.HasOne<Property>()
            .WithMany()
            .HasForeignKey(r => r.PropertyId)
            .OnDelete(DeleteBehavior.Cascade);
        entity.HasOne<Org>().WithMany().HasForeignKey(r => r.OrgId).OnDelete(DeleteBehavior.Restrict);
        entity.HasIndex(r => new { r.PropertyId, r.Position });
        entity.HasIndex(r => r.OrgId);
        entity.ToTable(t =>
        {
            t.HasCheckConstraint("CK_PropertyRooms_Position", "\"Position\" >= 0");
            t.HasCheckConstraint("CK_PropertyRooms_Type", "\"Type\" BETWEEN 0 AND 3");
        });
    }
}

internal sealed class PropertyStayDiscountConfiguration : IEntityTypeConfiguration<PropertyStayDiscount>
{
    public void Configure(EntityTypeBuilder<PropertyStayDiscount> entity)
    {
        entity.HasOne<Property>()
            .WithMany()
            .HasForeignKey(d => d.PropertyId)
            .OnDelete(DeleteBehavior.Cascade);
        entity.HasOne<Org>().WithMany().HasForeignKey(d => d.OrgId).OnDelete(DeleteBehavior.Restrict);
        entity.HasIndex(d => new { d.PropertyId, d.MinNights })
            .IsUnique()
            .HasDatabaseName("UIX_PropertyStayDiscounts_PropertyId_MinNights");
        entity.HasIndex(d => d.OrgId);
        entity.ToTable(t =>
        {
            t.HasCheckConstraint(
                "CK_PropertyStayDiscounts_MinNights",
                $"\"MinNights\" BETWEEN {PropertyStayDiscount.MinNightsLowest} AND {PropertyStayDiscount.MinNightsHighest}");
            t.HasCheckConstraint(
                "CK_PropertyStayDiscounts_PercentOff",
                "\"PercentOff\" > 0 AND \"PercentOff\" <= 100");
        });
    }
}
