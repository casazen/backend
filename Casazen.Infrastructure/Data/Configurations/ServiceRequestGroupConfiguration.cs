using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Casazen.Infrastructure.Data.Configurations;

internal sealed class ServiceRequestGroupConfiguration : IEntityTypeConfiguration<ServiceRequestGroup>
{
    public void Configure(EntityTypeBuilder<ServiceRequestGroup> entity)
    {
        entity.HasOne(g => g.Org)
            .WithMany()
            .HasForeignKey(g => g.OrgId)
            .OnDelete(DeleteBehavior.Restrict);

        entity.HasIndex(g => new { g.Phase, g.PrimaryDueAt })
            .HasFilter($"\"Phase\" <> {(int)ServiceRequestGroupPhase.Closed}")
            .HasDatabaseName("IX_ServiceRequestGroups_Open");

        entity.Property(g => g.Version).IsRowVersion();

        entity.ToTable(t =>
        {
            t.HasCheckConstraint(
                "CK_ServiceRequestGroups_Phase",
                $"\"Phase\" IN ({(int)ServiceRequestGroupPhase.PrimaryWindow}, {(int)ServiceRequestGroupPhase.BackupWindow}, {(int)ServiceRequestGroupPhase.Closed})");

            t.HasCheckConstraint(
                "CK_ServiceRequestGroups_Windows",
                $"(\"Phase\" = {(int)ServiceRequestGroupPhase.PrimaryWindow} AND \"BackupDueAt\" IS NULL AND \"ClosedAt\" IS NULL) OR "
                + $"(\"Phase\" = {(int)ServiceRequestGroupPhase.BackupWindow} AND \"BackupDueAt\" IS NOT NULL AND \"ClosedAt\" IS NULL) OR "
                + $"(\"Phase\" = {(int)ServiceRequestGroupPhase.Closed} AND \"ClosedAt\" IS NOT NULL)");
        });
    }
}

internal sealed class ServiceRequestGroupMembershipConfiguration : IEntityTypeConfiguration<ServiceRequest>
{
    public void Configure(EntityTypeBuilder<ServiceRequest> entity)
    {
        entity.HasOne(sr => sr.Group)
            .WithMany()
            .HasForeignKey(sr => sr.GroupId)
            .OnDelete(DeleteBehavior.Restrict);

        entity.HasIndex(sr => sr.GroupId, "IX_ServiceRequests_GroupId")
            .HasFilter("\"GroupId\" IS NOT NULL");

        entity.HasIndex(sr => sr.GroupId, "UIX_ServiceRequests_GroupId_Primary")
            .IsUnique()
            .HasFilter($"\"GroupRole\" = {(int)ServiceRequestGroupRole.Primary}");
        entity.HasIndex(sr => sr.GroupId, "UIX_ServiceRequests_GroupId_Promoted")
            .IsUnique()
            .HasFilter($"\"GroupRole\" = {(int)ServiceRequestGroupRole.PromotedBackup}");

        entity.ToTable(t => t.HasCheckConstraint(
            "CK_ServiceRequests_GroupRole",
            $"(\"GroupId\" IS NULL AND \"GroupRole\" = {(int)ServiceRequestGroupRole.None} "
            + "AND \"BackupAcceptedAt\" IS NULL AND \"BackupAcceptedByUserId\" IS NULL) OR "
            + $"(\"GroupId\" IS NOT NULL AND \"GroupRole\" BETWEEN {(int)ServiceRequestGroupRole.Primary} AND {(int)ServiceRequestGroupRole.PromotedBackup})"));
    }
}
