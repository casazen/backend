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
        entity.HasIndex(i => i.TokenHash)
            .IsUnique()
            .HasDatabaseName("UIX_HostInviteRecords_TokenHash");

        entity.HasIndex(i => new { i.InvitedByUserId, i.Email })
            .HasDatabaseName("IX_HostInviteRecords_InvitedByUserId_Email");

        entity.HasIndex(i => i.UsedByUserId);

        entity.HasIndex(i => new { i.InvitedByOrgId, i.CreatedAt })
            .HasDatabaseName("IX_HostInviteRecords_InvitedByOrgId_CreatedAt");
    }
}
