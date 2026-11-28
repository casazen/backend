using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Casazen.Infrastructure.Data.Configurations;

internal sealed class PropertyModeChangeConfiguration : IEntityTypeConfiguration<PropertyModeChange>
{
    public void Configure(EntityTypeBuilder<PropertyModeChange> entity)
    {
        entity.HasOne(c => c.Property)
            .WithMany()
            .HasForeignKey(c => c.PropertyId)
            .OnDelete(DeleteBehavior.Cascade);
        entity.HasOne<Org>()
            .WithMany()
            .HasForeignKey(c => c.OrgId)
            .OnDelete(DeleteBehavior.Restrict);

        entity.HasIndex(c => c.PropertyId)
            .IsUnique()
            .HasFilter($"\"Status\" = {(int)PropertyModeChangeStatus.Scheduled}")
            .HasDatabaseName(PropertyModeChange.OneScheduledIndexName);

        entity.HasIndex(c => new { c.Status, c.EffectiveDate });

        entity.HasIndex(c => new { c.PropertyId, c.CreatedAt });
    }
}
