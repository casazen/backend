using Casazen.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Casazen.Infrastructure.Data.Configurations;

/// <summary>
/// The payouts of a wire batch (E16/F01/US01/T01): the batch holds its rows (cascade), the payout and the owner restrict (a payout is evidence), a payout is in
/// <b>one batch only</b> (the unique index on the payout: the database refuses a second row for it whatever two requests do at the same moment), the end-to-end id
/// is unique in its batch and the amount of a transfer is positive.
/// </summary>
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
