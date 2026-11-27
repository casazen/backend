using Casazen.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Property = Casazen.Core.Entities.Property;

namespace Casazen.Infrastructure.Data.Configurations;

internal sealed class OctoratePreparationConfiguration : IEntityTypeConfiguration<OctoratePreparation>
{
    public void Configure(EntityTypeBuilder<OctoratePreparation> row)
    {
        row.HasOne<Property>().WithMany().HasForeignKey(p => p.PropertyId).OnDelete(DeleteBehavior.Cascade);
        row.HasIndex(p => new { p.Environment, p.PropertyId }).IsUnique();
        row.HasIndex(p => new { p.Environment, p.AccommodationId }).IsUnique();
        row.HasIndex(p => new { p.Environment, p.BatchId });
    }
}

internal sealed class OctorateConnectionConfiguration : IEntityTypeConfiguration<OctorateConnection>
{
    public void Configure(EntityTypeBuilder<OctorateConnection> entity)
    {
        // ─── Octorate connector (ADR-023) ────────────────────────────────────────
        // One connection per org and environment: one consent covers every property the Octorate user sees (an agency's whole
        // network), and the client acts for the org of its scope. The account id is for display and audit, not part of the key. The
        // two token columns get the Data Protection converter from EncryptedColumns (no length: the payload is longer than the text).
        entity.HasIndex(c => new { c.OrgId, c.Environment })
            .IsUnique()
            .HasDatabaseName(OctorateConnection.OrgEnvironmentIndexName);
    }
}

internal sealed class OctorateListingMappingConfiguration : IEntityTypeConfiguration<OctorateListingMapping>
{
    public void Configure(EntityTypeBuilder<OctorateListingMapping> entity)
    {
        // A listing found through a connection, once per org, connection and accommodation; a property is confirmed to one listing
        // only (partial unique index on Status = 2, OctorateMappingStatus.Confirmed). A connection with mappings is never deleted
        // (RESTRICT); a deleted property leaves its mapping without property (SET NULL). The webhook finds the org of a notification
        // by the accommodation code across orgs (a system read): its own index.
        entity.HasOne<OctorateConnection>()
            .WithMany()
            .HasForeignKey(m => m.ConnectionId)
            .OnDelete(DeleteBehavior.Restrict);
        entity.HasOne<Property>()
            .WithMany()
            .HasForeignKey(m => m.PropertyId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.SetNull);
        entity.Property(m => m.SnapshotRoomsJson).HasColumnType("jsonb");
        entity.Property(m => m.CandidatePropertyIdsJson).HasColumnType("jsonb");
        entity.Property(m => m.ChannelIdsJson).HasColumnType("jsonb");
        entity.HasIndex(m => new { m.OrgId, m.ConnectionId, m.OctorateAccommodationId })
            .IsUnique()
            .HasDatabaseName(OctorateListingMapping.AccommodationIndexName);
        entity.HasIndex(m => new { m.OrgId, m.PropertyId })
            .IsUnique()
            .HasFilter("\"Status\" = 2")
            .HasDatabaseName(OctorateListingMapping.ConfirmedPropertyIndexName);
        entity.HasIndex(m => new { m.OrgId, m.Status });
        entity.HasIndex(m => m.OctorateAccommodationId);
    }
}

internal sealed class OctorateOAuthStateConfiguration : IEntityTypeConfiguration<OctorateOAuthState>
{
    public void Configure(EntityTypeBuilder<OctorateOAuthState> entity)
    {
        entity.Property(s => s.StateHash).IsFixedLength();
        entity.HasIndex(s => s.StateHash)
            .IsUnique()
            .HasDatabaseName(OctorateOAuthState.StateHashIndexName);
        entity.HasIndex(s => s.ExpiresAt);
    }
}

internal sealed class OctorateBookingSyncConfiguration : IEntityTypeConfiguration<OctorateBookingSync>
{
    public void Configure(EntityTypeBuilder<OctorateBookingSync> entity)
    {
        entity.HasOne<Booking>()
            .WithMany()
            .HasForeignKey(s => s.BookingId)
            .OnDelete(DeleteBehavior.Cascade);
        entity.Property(s => s.OctorateVersionJson).HasColumnType("jsonb");
        entity.HasIndex(s => s.BookingId)
            .IsUnique()
            .HasDatabaseName(OctorateBookingSync.BookingIndexName);
        entity.HasIndex(s => new { s.OrgId, s.State });
    }
}
