using Casazen.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Casazen.Infrastructure.Data.Configurations;

internal sealed class SuppressionListEntryConfiguration : IEntityTypeConfiguration<SuppressionListEntry>
{
    public void Configure(EntityTypeBuilder<SuppressionListEntry> entity)
    {
        entity.HasOne<Org>().WithMany().HasForeignKey(r => r.OrgId)
            .OnDelete(DeleteBehavior.Restrict);

        // Index for fast suppression checks: OrgId + EmailHash
        entity.HasIndex(r => new { r.OrgId, r.EmailHash })
            .IsUnique();

        entity.HasIndex(r => r.OrgId);
    }
}
