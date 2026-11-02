using Casazen.Core.Entities;
using Casazen.Core.SiteProfile;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Casazen.Infrastructure.Data.Configurations;

/// <summary>
/// The floor plan levels of a property (DB-11a): rows follow their property (cascade) and carry its org (TN-2, restrict like every other
/// <c>OrgId</c> foreign key). The ranges are also constraints, so no other writer can store a level or an area the host form refuses.
/// </summary>
internal sealed class PropertyFloorPlanConfiguration : IEntityTypeConfiguration<PropertyFloorPlan>
{
    public void Configure(EntityTypeBuilder<PropertyFloorPlan> entity)
    {
        entity.HasOne(f => f.Property)
            .WithMany()
            .HasForeignKey(f => f.PropertyId)
            .OnDelete(DeleteBehavior.Cascade);
        entity.HasOne<Org>().WithMany().HasForeignKey(f => f.OrgId).OnDelete(DeleteBehavior.Restrict);
        entity.HasIndex(f => new { f.PropertyId, f.Position });
        entity.HasIndex(f => f.OrgId);
        entity.ToTable(t =>
        {
            t.HasCheckConstraint(
                "CK_PropertyFloorPlans_Position",
                "\"Position\" >= 0");
            t.HasCheckConstraint(
                "CK_PropertyFloorPlans_Level",
                $"\"Level\" BETWEEN {SitePropertyProfileLimits.MinFloorLevel} AND {SitePropertyProfileLimits.MaxFloorLevel}");
            t.HasCheckConstraint(
                "CK_PropertyFloorPlans_AreaSqm",
                $"\"AreaSqm\" IS NULL OR \"AreaSqm\" BETWEEN {PropertyFloorPlan.MinAreaSqm} AND {PropertyFloorPlan.MaxAreaSqm}");
        });
    }
}

/// <summary>
/// The points of interest of a property (DB-11a): same ownership as the floor plan levels. The ranges (minutes, rounded distance) are also
/// constraints; the direction is one of eight points, never a bearing in degrees.
/// </summary>
internal sealed class PropertyPoiConfiguration : IEntityTypeConfiguration<PropertyPoi>
{
    public void Configure(EntityTypeBuilder<PropertyPoi> entity)
    {
        entity.HasOne(p => p.Property)
            .WithMany()
            .HasForeignKey(p => p.PropertyId)
            .OnDelete(DeleteBehavior.Cascade);
        entity.HasOne<Org>().WithMany().HasForeignKey(p => p.OrgId).OnDelete(DeleteBehavior.Restrict);
        entity.HasIndex(p => new { p.PropertyId, p.Position });
        entity.HasIndex(p => p.OrgId);
        entity.ToTable(t =>
        {
            t.HasCheckConstraint(
                "CK_PropertyPois_Minutes",
                $"\"Minutes\" BETWEEN {PropertyPoi.MinMinutes} AND {PropertyPoi.MaxMinutes}");
            t.HasCheckConstraint(
                "CK_PropertyPois_DistanceMeters",
                $"\"DistanceMeters\" BETWEEN {PropertyPoi.MinDistanceMeters} AND {PropertyPoi.MaxDistanceMeters}");
            t.HasCheckConstraint(
                "CK_PropertyPois_Direction",
                "\"Direction\" BETWEEN 0 AND 7");
        });
    }
}
