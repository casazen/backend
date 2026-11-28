using Casazen.Core.Entities;
using Casazen.Core.Suppliers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Casazen.Infrastructure.Data.Configurations;

internal sealed class SupplierShowcaseMediaConfiguration : IEntityTypeConfiguration<SupplierShowcaseMedia>
{
    public void Configure(EntityTypeBuilder<SupplierShowcaseMedia> entity)
    {
        entity.HasOne(m => m.SupplierProfile)
            .WithMany()
            .HasForeignKey(m => m.OrgId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasIndex(m => new { m.OrgId, m.CreatedAt })
            .HasDatabaseName("IX_SupplierShowcaseMedia_OrgId_CreatedAt");

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
