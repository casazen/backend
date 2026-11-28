using Casazen.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Casazen.Infrastructure.Data.Configurations;

internal sealed class ManualComplianceProofConfiguration : IEntityTypeConfiguration<ManualComplianceProof>
{
    public void Configure(EntityTypeBuilder<ManualComplianceProof> entity)
    {
        entity.HasOne(p => p.Property)
            .WithMany()
            .HasForeignKey(p => p.PropertyId)
            .OnDelete(DeleteBehavior.Cascade);
        entity.HasOne<Org>().WithMany().HasForeignKey(p => p.OrgId).OnDelete(DeleteBehavior.Restrict);
        entity.Ignore(p => p.RetainUntil);
    }
}
