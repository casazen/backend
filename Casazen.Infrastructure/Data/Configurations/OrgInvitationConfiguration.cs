using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Casazen.Infrastructure.Data.Configurations;

internal sealed class OrgInvitationConfiguration : IEntityTypeConfiguration<OrgInvitation>
{
    public void Configure(EntityTypeBuilder<OrgInvitation> entity)
    {
        entity.HasOne<Org>()
            .WithMany()
            .HasForeignKey(i => i.OrgId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasIndex(i => i.TokenHash)
            .IsUnique()
            .HasDatabaseName("UIX_OrgInvitations_TokenHash");

        // At most one pending invitation per org and email: two parallel invitations of the same person race on this
        // (the loser gets 23505, answered as 409). Filter on Status = Pending (1), which is explicit and never reused.
        entity.HasIndex(i => new { i.OrgId, i.Email })
            .IsUnique()
            .HasFilter($"\"Status\" = {(int)OrgInvitationStatus.Pending}")
            .HasDatabaseName("UIX_OrgInvitations_OrgId_Email_Pending");

        entity.HasIndex(i => new { i.OrgId, i.Status })
            .HasDatabaseName("IX_OrgInvitations_OrgId_Status");

        entity.HasIndex(i => new { i.Status, i.ExpiresAt })
            .HasDatabaseName("IX_OrgInvitations_Status_ExpiresAt");

        entity.Property(i => i.SendLog).HasDefaultValueSql("'{}'");
    }
}
