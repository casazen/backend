using Casazen.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Casazen.Infrastructure.Data.Configurations;

/// <summary>
/// The proofs a host uploads (E03/F02/US01/T01, NF-47, NF-52): going with the property (cascade) and carrying its org (TN-2, restrict like
/// every other <c>OrgId</c> foreign key). The file is in the private bucket; the row holds its key and no guest data.
/// </summary>
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
