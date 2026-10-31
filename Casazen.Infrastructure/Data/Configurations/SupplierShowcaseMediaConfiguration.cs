using Casazen.Core.Entities;
using Casazen.Core.Suppliers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Casazen.Infrastructure.Data.Configurations;

internal sealed class SupplierShowcaseMediaConfiguration : IEntityTypeConfiguration<SupplierShowcaseMedia>
{
    public void Configure(EntityTypeBuilder<SupplierShowcaseMedia> entity)
    {
        // A child of the supplier profile in cascade, like the content and the catalog. The objects in the bucket do not go with the rows:
        // a supplier profile is deleted only by the repair of duplicates, which moves what must stay first.
        entity.HasOne(m => m.SupplierProfile)
            .WithMany()
            .HasForeignKey(m => m.OrgId)
            .OnDelete(DeleteBehavior.Cascade);

        // The rows of a supplier, oldest first: the count against the limit and the pass of the cleanup both read them this way.
        entity.HasIndex(m => new { m.OrgId, m.CreatedAt })
            .HasDatabaseName("IX_SupplierShowcaseMedia_OrgId_CreatedAt");

        // One row per object: the key has a random name, and the content service looks a file up by it.
        entity.HasIndex(m => m.StorageKey)
            .IsUnique()
            .HasDatabaseName("UIX_SupplierShowcaseMedia_StorageKey");

        entity.ToTable(t =>
        {
            t.HasCheckConstraint(
                "CK_SupplierShowcaseMedia_Kind",
                $"\"Kind\" IN ('{ShowcaseMediaKinds.Logo}', '{ShowcaseMediaKinds.Cover}', '{ShowcaseMediaKinds.Work}', '{ShowcaseMediaKinds.Before}')");
            t.HasCheckConstraint("CK_SupplierShowcaseMedia_Bytes", "\"Bytes\" > 0");
        });
    }
}
