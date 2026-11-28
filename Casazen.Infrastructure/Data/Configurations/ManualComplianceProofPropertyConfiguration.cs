using Casazen.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Casazen.Infrastructure.Data.Configurations;

internal sealed class ManualComplianceProofPropertyConfiguration : IEntityTypeConfiguration<ManualComplianceProofProperty>
{
    public void Configure(EntityTypeBuilder<ManualComplianceProofProperty> entity)
    {
        entity.HasOne(link => link.Proof)
            .WithMany(proof => proof.CoveredProperties)
            .HasForeignKey(link => link.ProofId)
            .OnDelete(DeleteBehavior.Cascade);
        entity.HasOne(link => link.Property)
            .WithMany()
            .HasForeignKey(link => link.PropertyId)
            .OnDelete(DeleteBehavior.Cascade);
        entity.HasOne<Org>().WithMany().HasForeignKey(link => link.OrgId).OnDelete(DeleteBehavior.Restrict);
    }
}
