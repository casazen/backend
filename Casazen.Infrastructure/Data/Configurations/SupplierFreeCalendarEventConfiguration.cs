using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Suppliers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Casazen.Infrastructure.Data.Configurations;

internal sealed class SupplierFreeCalendarEventConfiguration : IEntityTypeConfiguration<SupplierFreeCalendarEvent>
{
    public void Configure(EntityTypeBuilder<SupplierFreeCalendarEvent> entity)
    {
        entity.HasOne(e => e.SupplierProfile)
            .WithMany()
            .HasForeignKey(e => e.OrgId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasIndex(e => new { e.OrgId, e.DecisionKey })
            .IsUnique()
            .HasDatabaseName("UIX_SupplierFreeCalendarEvents_OrgId_DecisionKey");

        entity.HasIndex(e => new { e.OrgId, e.Decision, e.NextStartUtc })
            .HasDatabaseName("IX_SupplierFreeCalendarEvents_OrgId_Decision_NextStartUtc");
    }
}
