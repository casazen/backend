using Casazen.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Casazen.Infrastructure.Data.Configurations;

internal sealed class SupplierShowcaseContentConfiguration : IEntityTypeConfiguration<SupplierShowcaseContent>
{
    public void Configure(EntityTypeBuilder<SupplierShowcaseContent> entity)
    {
        entity.HasOne(c => c.SupplierProfile)
            .WithOne()
            .HasForeignKey<SupplierShowcaseContent>(c => c.OrgId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.Property(c => c.Version).IsRowVersion();
    }
}
