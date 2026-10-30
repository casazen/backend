using Casazen.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Casazen.Infrastructure.Data.Configurations;

internal sealed class ConcordatoFacilePurchaseConfiguration : IEntityTypeConfiguration<ConcordatoFacilePurchase>
{
    public void Configure(EntityTypeBuilder<ConcordatoFacilePurchase> entity)
    {
        entity.HasOne(p => p.Org)
            .WithMany()
            .HasForeignKey(p => p.OrgId)
            .OnDelete(DeleteBehavior.Restrict);

        entity.HasOne(p => p.LeaseContract)
            .WithMany()
            .HasForeignKey(p => p.LeaseContractId)
            .OnDelete(DeleteBehavior.Restrict);

        entity.HasIndex(p => new { p.OrgId, p.LeaseContractId });
        entity.HasIndex(p => p.StripePaymentIntentId);
    }
}
