using Casazen.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Casazen.Infrastructure.Data.Configurations;

internal sealed class WireBatchPayoutConfiguration : IEntityTypeConfiguration<WireBatchPayout>
{
    public void Configure(EntityTypeBuilder<WireBatchPayout> entity)
    {
        entity.HasOne<WireBatch>().WithMany().HasForeignKey(w => w.WireBatchId).OnDelete(DeleteBehavior.Cascade);
        entity.HasOne<OwnerPayout>().WithMany().HasForeignKey(w => w.OwnerPayoutId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne<PropertyOwner>().WithMany().HasForeignKey(w => w.PropertyOwnerId).OnDelete(DeleteBehavior.Restrict);
        entity.HasIndex(w => w.OwnerPayoutId).IsUnique().HasDatabaseName(WireBatchPayout.PayoutIndexName);
        entity.HasIndex(w => new { w.WireBatchId, w.EndToEndId }).IsUnique();
        entity.HasIndex(w => new { w.OrgId, w.WireBatchId });
        entity.ToTable(table => table.HasCheckConstraint("CK_WireBatchPayouts_Amount", "\"Amount\" > 0"));
    }
}
