using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Options;
using Casazen.Core.Suppliers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Casazen.Infrastructure.Data.Configurations;

internal sealed class HostInviteRecordConfiguration : IEntityTypeConfiguration<HostInviteRecord>
{
    public void Configure(EntityTypeBuilder<HostInviteRecord> entity)
    {
        // The link finds its invite by the hash of the token; sending it again swaps the hash, so it is unique.
        entity.HasIndex(i => i.TokenHash)
            .IsUnique()
            .HasDatabaseName("UIX_HostInviteRecords_TokenHash");

        // The duplicate check and the lists of an inviter, newest first.
        entity.HasIndex(i => new { i.InvitedByUserId, i.Email })
            .HasDatabaseName("IX_HostInviteRecords_InvitedByUserId_Email");

        // The pending cap of a host org and the admin list by creation date.
        entity.HasIndex(i => new { i.InvitedByOrgId, i.CreatedAt })
            .HasDatabaseName("IX_HostInviteRecords_InvitedByOrgId_CreatedAt");
    }
}
