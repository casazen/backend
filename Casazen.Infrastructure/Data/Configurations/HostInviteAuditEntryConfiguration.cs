using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Options;
using Casazen.Core.Suppliers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Casazen.Infrastructure.Data.Configurations;

internal sealed class HostInviteAuditEntryConfiguration : IEntityTypeConfiguration<HostInviteAuditEntry>
{
    public void Configure(EntityTypeBuilder<HostInviteAuditEntry> entity)
    {
        entity.HasIndex(e => new { e.ActorOrgId, e.OccurredAt })
            .HasDatabaseName("IX_HostInviteAuditEntries_ActorOrgId_OccurredAt");
        entity.HasIndex(e => e.InviteId)
            .HasDatabaseName("IX_HostInviteAuditEntries_InviteId");
    }
}
