using Casazen.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Casazen.Infrastructure.Data.Configurations;

/// <summary>
/// The flats a receipt covers (E20/F02/US01/T01, NF-43, NF-47, decision O3): going with the proof and with the flat (cascade) and carrying the org
/// (TN-2, restrict like every other <c>OrgId</c> foreign key). The pair (proof, flat) is unique; the row holds no guest data.
/// </summary>
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
