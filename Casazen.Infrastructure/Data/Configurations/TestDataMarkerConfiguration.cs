using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Options;
using Casazen.Core.Suppliers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Casazen.Infrastructure.Data.Configurations;

internal sealed class TestDataMarkerConfiguration : IEntityTypeConfiguration<TestDataMarker>
{
    public void Configure(EntityTypeBuilder<TestDataMarker> entity)
    {
        entity.Property(m => m.EntityType).HasMaxLength(TestDataMarker.EntityTypeMaxLength);
        entity.Property(m => m.Origin).HasMaxLength(TestDataMarker.OriginMaxLength);
        entity.Property(m => m.TemplateKey).HasMaxLength(TestDataMarker.TemplateKeyMaxLength);
        entity.HasIndex(m => new { m.EntityType, m.EntityId }).IsUnique();
        entity.HasIndex(m => new { m.OrgId, m.Origin });
        entity.HasOne<Org>().WithMany().HasForeignKey(m => m.OrgId).OnDelete(DeleteBehavior.Cascade);
    }
}
