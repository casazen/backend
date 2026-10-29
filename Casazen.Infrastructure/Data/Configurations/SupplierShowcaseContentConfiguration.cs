using Casazen.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Casazen.Infrastructure.Data.Configurations;

internal sealed class SupplierShowcaseContentConfiguration : IEntityTypeConfiguration<SupplierShowcaseContent>
{
    public void Configure(EntityTypeBuilder<SupplierShowcaseContent> entity)
    {
        // One row per supplier: the key is the supplier org, which is also the foreign key. A child of the supplier profile in cascade,
        // like the settings and the catalog: the repair moves the row to the keeper before it deletes a duplicate profile.
        entity.HasOne(c => c.SupplierProfile)
            .WithOne()
            .HasForeignKey<SupplierShowcaseContent>(c => c.OrgId)
            .OnDelete(DeleteBehavior.Cascade);

        // xmin: every update of the row changes it, and a save must carry the one the client read.
        entity.Property(c => c.Version).IsRowVersion();
    }
}
