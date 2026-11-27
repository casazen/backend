using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Property = Casazen.Core.Entities.Property;

namespace Casazen.Infrastructure.Data.Configurations;

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
