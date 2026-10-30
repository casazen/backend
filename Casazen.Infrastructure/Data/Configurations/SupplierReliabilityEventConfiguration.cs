using Casazen.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Casazen.Infrastructure.Data.Configurations;

internal sealed class SupplierReliabilityEventConfiguration : IEntityTypeConfiguration<SupplierReliabilityEvent>
{
    public void Configure(EntityTypeBuilder<SupplierReliabilityEvent> entity)
    {
        entity.HasOne(e => e.SupplierProfile)
            .WithMany()
            .HasForeignKey(e => e.SupplierOrgId)
            .OnDelete(DeleteBehavior.Cascade);

        // What the ranking reads: the events of a supplier since a date.
        entity.HasIndex(e => new { e.SupplierOrgId, e.OccurredAt })
            .HasDatabaseName("IX_SupplierReliabilityEvents_Supplier_OccurredAt");

        // One event of a kind for each supplier and request: the writers are jobs and webhooks that may run twice.
        entity.HasIndex(e => new { e.SupplierOrgId, e.RequestId, e.Kind })
            .IsUnique()
            .HasFilter("\"RequestId\" IS NOT NULL")
            .HasDatabaseName("UIX_SupplierReliabilityEvents_Request");

        entity.ToTable(t => t.HasCheckConstraint("CK_SupplierReliabilityEvents_Kind", "\"Kind\" IN (1, 2, 3, 4)"));
    }
}
