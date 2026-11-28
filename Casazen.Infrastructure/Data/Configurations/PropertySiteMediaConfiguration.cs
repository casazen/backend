using Casazen.Core.Entities;
using Casazen.Core.SiteProfile;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Casazen.Infrastructure.Data.Configurations;

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
