using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Options;
using Casazen.Core.Suppliers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Casazen.Infrastructure.Data.Configurations;

internal sealed class GuestPrivacyAuditEntryConfiguration : IEntityTypeConfiguration<GuestPrivacyAuditEntry>
{
    public void Configure(EntityTypeBuilder<GuestPrivacyAuditEntry> entity)
    {
        entity.HasOne<Org>().WithMany().HasForeignKey(a => a.OrgId)
            .OnDelete(DeleteBehavior.Restrict);
        entity.HasIndex(a => a.OrgId);
        entity.HasIndex(a => new { a.GuestId, a.OccurredAt });
    }
}
