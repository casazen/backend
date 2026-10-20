using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Suppliers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Casazen.Infrastructure.Data.Configurations;

internal sealed class OrgInvitationConfiguration : IEntityTypeConfiguration<OrgInvitation>
{
    public void Configure(EntityTypeBuilder<OrgInvitation> entity)
    {
        // The invitations of an org go with it.
        entity.HasOne<Org>()
            .WithMany()
            .HasForeignKey(i => i.OrgId)
            .OnDelete(DeleteBehavior.Cascade);

        // The link finds its invitation by the hash of the token; rotating the token swaps the hash, so it is unique.
        entity.HasIndex(i => i.TokenHash)
            .IsUnique()
            .HasDatabaseName("UIX_OrgInvitations_TokenHash");

        // At most one pending invitation per org and email: two parallel invitations of the same person race on this
        // (the loser gets 23505, answered as 409). Filter on Status = Pending (1), which is explicit and never reused.
        entity.HasIndex(i => new { i.OrgId, i.Email })
            .IsUnique()
            .HasFilter($"\"Status\" = {(int)OrgInvitationStatus.Pending}")
            .HasDatabaseName("UIX_OrgInvitations_OrgId_Email_Pending");

        // The tenant filter reads by OrgId; the team page lists an org's open invitations and the seat count counts them.
        entity.HasIndex(i => new { i.OrgId, i.Status })
            .HasDatabaseName("IX_OrgInvitations_OrgId_Status");

        // The maintenance job scans by state and date: the invitations to remind, expire or delete.
        entity.HasIndex(i => new { i.Status, i.ExpiresAt })
            .HasDatabaseName("IX_OrgInvitations_Status_ExpiresAt");

        // The sends of the last 24 hours (AM-02c): an empty list for every invitation written before they were tracked, so the
        // column can be NOT NULL on a table that already has rows. No send is invented for them.
        entity.Property(i => i.SendLog).HasDefaultValueSql("'{}'");
    }
}
