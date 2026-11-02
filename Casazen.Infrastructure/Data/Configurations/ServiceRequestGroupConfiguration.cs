using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Casazen.Infrastructure.Data.Configurations;

/// <summary>The table of the groups of backup suppliers (SP-BKP): not tenant-filtered, see <see cref="ServiceRequestGroup"/>.</summary>
internal sealed class ServiceRequestGroupConfiguration : IEntityTypeConfiguration<ServiceRequestGroup>
{
    public void Configure(EntityTypeBuilder<ServiceRequestGroup> entity)
    {
        entity.HasOne(g => g.Org)
            .WithMany()
            .HasForeignKey(g => g.OrgId)
            .OnDelete(DeleteBehavior.Restrict);

        // The pass of the job reads the groups that are not over by the end of their current window.
        entity.HasIndex(g => new { g.Phase, g.PrimaryDueAt })
            .HasFilter($"\"Phase\" <> {(int)ServiceRequestGroupPhase.Closed}")
            .HasDatabaseName("IX_ServiceRequestGroups_Open");

        // xmin, as for the requests: two calls that read the same group cannot both change it.
        entity.Property(g => g.Version).IsRowVersion();

        entity.ToTable(t =>
        {
            t.HasCheckConstraint(
                "CK_ServiceRequestGroups_Phase",
                $"\"Phase\" IN ({(int)ServiceRequestGroupPhase.PrimaryWindow}, {(int)ServiceRequestGroupPhase.BackupWindow}, {(int)ServiceRequestGroupPhase.Closed})");

            // A group in the second window has its end; one that is over has the instant it was closed; one that is not has neither.
            t.HasCheckConstraint(
                "CK_ServiceRequestGroups_Windows",
                $"(\"Phase\" = {(int)ServiceRequestGroupPhase.PrimaryWindow} AND \"BackupDueAt\" IS NULL AND \"ClosedAt\" IS NULL) OR "
                + $"(\"Phase\" = {(int)ServiceRequestGroupPhase.BackupWindow} AND \"BackupDueAt\" IS NOT NULL AND \"ClosedAt\" IS NULL) OR "
                + $"(\"Phase\" = {(int)ServiceRequestGroupPhase.Closed} AND \"ClosedAt\" IS NOT NULL)");
        });
    }
}

/// <summary>The columns of <c>ServiceRequests</c> that tie a request to its group (SP-BKP).</summary>
internal sealed class ServiceRequestGroupMembershipConfiguration : IEntityTypeConfiguration<ServiceRequest>
{
    public void Configure(EntityTypeBuilder<ServiceRequest> entity)
    {
        // A group is never deleted while a request belongs to it.
        entity.HasOne(sr => sr.Group)
            .WithMany()
            .HasForeignKey(sr => sr.GroupId)
            .OnDelete(DeleteBehavior.Restrict);

        // The members of a group are read together (the host's view, the pass of the job).
        entity.HasIndex(sr => sr.GroupId, "IX_ServiceRequests_GroupId")
            .HasFilter("\"GroupId\" IS NOT NULL");

        // One primary per group, and at most one backup that took the job. Two indexes and not one over both roles: the promotion changes
        // two rows (the primary becomes ReplacedPrimary, the backup PromotedBackup) and the order EF updates them in is not ours to choose.
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
