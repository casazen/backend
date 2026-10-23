using Casazen.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Casazen.Infrastructure.Data.Configurations;

internal sealed class ConcordatoFacileGiftConfiguration : IEntityTypeConfiguration<ConcordatoFacileGift>
{
    public void Configure(EntityTypeBuilder<ConcordatoFacileGift> entity)
    {
        entity.HasOne(g => g.Org)
            .WithMany()
            .HasForeignKey(g => g.OrgId)
            .OnDelete(DeleteBehavior.Restrict);

        entity.HasOne(g => g.ConsumedForLease)
            .WithMany()
            .HasForeignKey(g => g.ConsumedForLeaseId)
            .OnDelete(DeleteBehavior.Restrict);

        entity.HasIndex(g => g.OrgId).IsUnique();
    }
}
