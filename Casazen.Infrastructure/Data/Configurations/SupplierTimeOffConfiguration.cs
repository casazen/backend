using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Suppliers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Casazen.Infrastructure.Data.Configurations;

internal sealed class SupplierTimeOffConfiguration : IEntityTypeConfiguration<SupplierTimeOff>
{
    public void Configure(EntityTypeBuilder<SupplierTimeOff> entity)
    {
        entity.HasOne(t => t.SupplierProfile)
            .WithMany()
            .HasForeignKey(t => t.OrgId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasIndex(t => new { t.OrgId, t.FromDate })
            .HasDatabaseName("IX_SupplierTimeOff_OrgId_FromDate");

        entity.ToTable(t => t.HasCheckConstraint("CK_SupplierTimeOff_Dates", "\"FromDate\" <= \"ToDate\""));
    }
}
