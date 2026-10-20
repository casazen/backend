using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Suppliers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Casazen.Infrastructure.Data.Configurations;

internal sealed class SupplierWorkingHoursConfiguration : IEntityTypeConfiguration<SupplierWorkingHours>
{
    public void Configure(EntityTypeBuilder<SupplierWorkingHours> entity)
    {
        entity.HasOne(h => h.SupplierProfile)
            .WithMany()
            .HasForeignKey(h => h.OrgId)
            .OnDelete(DeleteBehavior.Cascade);

        // One band per weekday and start: the same band twice is never meant. The overlap of two bands and the limit of
        // three a day need more than a unique index: the service decides both under the agenda lock.
        entity.HasIndex(h => new { h.OrgId, h.Weekday, h.StartMinute })
            .IsUnique()
            .HasDatabaseName("UIX_SupplierWorkingHours_OrgId_Weekday_StartMinute");

        entity.ToTable(t =>
        {
            t.HasCheckConstraint("CK_SupplierWorkingHours_Weekday", "\"Weekday\" BETWEEN 0 AND 6");
            t.HasCheckConstraint(
                "CK_SupplierWorkingHours_Minutes",
                $"\"StartMinute\" >= 0 AND \"StartMinute\" < \"EndMinute\" AND \"EndMinute\" <= {SupplierAgendaLimits.MinutesPerDay}");
        });
    }
}
