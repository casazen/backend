using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Suppliers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Casazen.Infrastructure.Data.Configurations;

internal sealed class SupplierBusyWindowConfiguration : IEntityTypeConfiguration<SupplierBusyWindow>
{
    public void Configure(EntityTypeBuilder<SupplierBusyWindow> entity)
    {
        entity.HasOne(w => w.SupplierProfile)
            .WithMany()
            .HasForeignKey(w => w.OrgId)
            .OnDelete(DeleteBehavior.Cascade);

        // The planner and the calendar read the windows of one supplier by time.
        entity.HasIndex(w => new { w.OrgId, w.StartUtc })
            .HasDatabaseName("IX_SupplierBusyWindows_OrgId_StartUtc");

        // SP-05: the iCal sync writes one row per occurrence of a feed event and finds it again by this key at every sync
        // (the UID, and the start because a series repeats the UID), so the same occurrence is never stored twice. The
        // windows the supplier sets by hand have no ExternalUid and are outside the index.
        entity.HasIndex(w => new { w.OrgId, w.ExternalUid, w.StartUtc })
            .IsUnique()
            .HasFilter("\"ExternalUid\" IS NOT NULL")
            .HasDatabaseName("UIX_SupplierBusyWindows_OrgId_ExternalUid_StartUtc");

        entity.ToTable(t => t.HasCheckConstraint("CK_SupplierBusyWindows_Interval", "\"StartUtc\" < \"EndUtc\""));
    }
}
