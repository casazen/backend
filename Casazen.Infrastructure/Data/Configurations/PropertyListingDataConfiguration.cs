using Casazen.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Property = Casazen.Core.Entities.Property;

namespace Casazen.Infrastructure.Data.Configurations;

/// <summary>
/// The rooms of a unit (NF-34): rows follow their property (cascade) and carry its org (TN-2, restrict like every other <c>OrgId</c> foreign
/// key). The type and the position are also constraints, so no other writer can store a room the host form refuses.
/// </summary>
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

/// <summary>
/// The length-of-stay discounts of a property (NF-35, ADR-028-R2): same ownership as the rooms. One tier for each minimum of nights, and the
/// ranges are also constraints, so no other writer can store a tier the host form refuses (the limit of five tiers is the rules').
/// </summary>
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
