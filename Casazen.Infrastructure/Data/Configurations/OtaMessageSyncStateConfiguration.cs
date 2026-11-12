using Casazen.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Casazen.Infrastructure.Data.Configurations;

/// <summary>
/// The checkpoints of the copy of the conversations of a listing (E05/F01/US01/T01, ADR-032): one row per organization and accommodation
/// (unique index). The connection it is read through is never deleted while the row exists (RESTRICT, like the mappings); the property lets
/// go (SET NULL) when it is removed.
/// </summary>
internal sealed class OtaMessageSyncStateConfiguration : IEntityTypeConfiguration<OtaMessageSyncState>
{
    public void Configure(EntityTypeBuilder<OtaMessageSyncState> entity)
    {
        entity.HasOne<OctorateConnection>()
            .WithMany()
            .HasForeignKey(s => s.ConnectionId)
            .OnDelete(DeleteBehavior.Restrict);
        entity.HasOne<Property>()
            .WithMany()
            .HasForeignKey(s => s.PropertyId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.SetNull);
        entity.HasOne<Org>()
            .WithMany()
            .HasForeignKey(s => s.OrgId)
            .OnDelete(DeleteBehavior.Restrict);

        entity.HasIndex(s => new { s.OrgId, s.OctorateAccommodationId })
            .IsUnique()
            .HasDatabaseName(OtaMessageSyncState.OrgAccommodationIndexName);
    }
}
