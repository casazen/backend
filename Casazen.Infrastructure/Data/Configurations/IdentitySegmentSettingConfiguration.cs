using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Property = Casazen.Core.Entities.Property;

namespace Casazen.Infrastructure.Data.Configurations;

/// <summary>
/// The segments of the self-hosted identity verification (E70/F01/US01/T02, NF-194): the row goes with its property (cascade) and carries its org (TN-2, restrict
/// like every other <c>OrgId</c> foreign key). One row per key: (org, format, channel) for the row of every property and (org, property, format, channel) for the
/// row of one property, two filtered unique indexes, so the two can coexist and neither can be written twice. A new row is in observation and requires the
/// consistency assurance, the database defaults included.
/// </summary>
internal sealed class IdentitySegmentSettingConfiguration : IEntityTypeConfiguration<IdentitySegmentSetting>
{
    public void Configure(EntityTypeBuilder<IdentitySegmentSetting> entity)
    {
        entity.HasOne<Org>().WithMany().HasForeignKey(s => s.OrgId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne<Property>().WithMany().HasForeignKey(s => s.PropertyId).OnDelete(DeleteBehavior.Cascade);

        entity.Property(s => s.Mode).HasDefaultValue(IdentitySegmentMode.Observation);
        entity.Property(s => s.RequiredAssurance).HasDefaultValue(IdentityAssuranceLevels.Consistency);

        entity.HasIndex(s => new { s.OrgId, s.FormatId, s.Channel })
            .IsUnique()
            .HasFilter("\"PropertyId\" IS NULL")
            .HasDatabaseName(IdentitySegmentSetting.OrgWideIndexName);
        entity.HasIndex(s => new { s.OrgId, s.PropertyId, s.FormatId, s.Channel })
            .IsUnique()
            .HasFilter("\"PropertyId\" IS NOT NULL")
            .HasDatabaseName(IdentitySegmentSetting.PropertyIndexName);
    }
}
