using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Suppliers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Casazen.Infrastructure.Data.Configurations;

internal sealed class SupplierSettingsConfiguration : IEntityTypeConfiguration<SupplierSettings>
{
    public void Configure(EntityTypeBuilder<SupplierSettings> entity)
    {
        // One row per supplier: the key is the supplier org, which is also the foreign key.
        entity.HasOne(s => s.SupplierProfile)
            .WithOne()
            .HasForeignKey<SupplierSettings>(s => s.OrgId)
            .OnDelete(DeleteBehavior.Cascade);

        // The bounds are the ones of SupplierAgendaLimits (a constant, so the rules and the database cannot drift).
        entity.ToTable(t =>
        {
            t.HasCheckConstraint(
                "CK_SupplierSettings_BufferMinutes",
                $"\"BufferMinutes\" BETWEEN {SupplierAgendaLimits.MinBufferMinutes} AND {SupplierAgendaLimits.MaxBufferMinutes}");
            t.HasCheckConstraint(
                "CK_SupplierSettings_MaxJobsPerDay",
                $"\"MaxJobsPerDay\" BETWEEN {SupplierAgendaLimits.MinJobsPerDay} AND {SupplierAgendaLimits.MaxJobsPerDayLimit}");
            t.HasCheckConstraint(
                "CK_SupplierSettings_MinNoticeHours",
                $"\"MinNoticeHours\" BETWEEN {SupplierAgendaLimits.MinNoticeHours} AND {SupplierAgendaLimits.MaxNoticeHours}");
            t.HasCheckConstraint(
                "CK_SupplierSettings_HorizonDays",
                $"\"HorizonDays\" BETWEEN {SupplierAgendaLimits.MinHorizonDays} AND {SupplierAgendaLimits.MaxHorizonDays}");
            t.HasCheckConstraint(
                "CK_SupplierSettings_SlotStepMinutes",
                $"\"SlotStepMinutes\" BETWEEN {SupplierAgendaLimits.MinSlotStepMinutes} AND {SupplierAgendaLimits.MaxSlotStepMinutes}");
            t.HasCheckConstraint(
                "CK_SupplierSettings_ParallelJobs",
                $"\"ParallelJobs\" BETWEEN {SupplierAgendaLimits.MinParallelJobs} AND {SupplierAgendaLimits.MaxParallelJobs}");
            t.HasCheckConstraint(
                "CK_SupplierSettings_RespondWithinMinutes",
                $"\"RespondWithinMinutes\" BETWEEN {SupplierAgendaLimits.MinRespondWithinMinutes} AND {SupplierAgendaLimits.MaxRespondWithinMinutes}");
        });
    }
}
