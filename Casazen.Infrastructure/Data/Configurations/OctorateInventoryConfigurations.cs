using Casazen.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Property = Casazen.Core.Entities.Property;

namespace Casazen.Infrastructure.Data.Configurations;

/// <summary>
/// What a person declared about where the listings of a property are (E02/F01/US03): one current intent for each property, held by a partial
/// unique index on the rows that have a property (the database guard that a property never has two), and one for each group with no property
/// yet, held by a second partial index on the rows that have none. The row goes with its property (cascade, when it has one) and carries the
/// org (TN-2, restrict like every other <c>OrgId</c> foreign key).
/// </summary>
internal sealed class OctorateInventoryIntentConfiguration : IEntityTypeConfiguration<OctorateInventoryIntent>
{
    public void Configure(EntityTypeBuilder<OctorateInventoryIntent> entity)
    {
        entity.HasOne<Property>()
            .WithMany()
            .HasForeignKey(i => i.PropertyId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.Cascade);
        entity.HasOne<Org>().WithMany().HasForeignKey(i => i.OrgId).OnDelete(DeleteBehavior.Restrict);
        entity.HasIndex(i => new { i.OrgId, i.PropertyId })
            .IsUnique()
            .HasFilter("\"PropertyId\" IS NOT NULL")
            .HasDatabaseName(OctorateInventoryIntent.PropertyIndexName);
        entity.HasIndex(i => new { i.OrgId, i.WizardBatch })
            .IsUnique()
            .HasFilter("\"PropertyId\" IS NULL")
            .HasDatabaseName(OctorateInventoryIntent.OrgLevelGroupIndexName);
        entity.HasIndex(i => i.PropertyId);
    }
}

/// <summary>
/// An import of existing OTA inventory (E02/F01/US03): carries the org (TN-2, restrict); the list of properties it is about is JSON (jsonb).
/// Its channels go with it (cascade).
/// </summary>
internal sealed class OctorateInventoryImportBatchConfiguration : IEntityTypeConfiguration<OctorateInventoryImportBatch>
{
    public void Configure(EntityTypeBuilder<OctorateInventoryImportBatch> entity)
    {
        entity.HasOne<Org>().WithMany().HasForeignKey(b => b.OrgId).OnDelete(DeleteBehavior.Restrict);
        entity.Property(b => b.ScopePropertyIdsJson).HasColumnType("jsonb");
        entity.HasMany(b => b.Channels)
            .WithOne()
            .HasForeignKey(c => c.BatchId)
            .OnDelete(DeleteBehavior.Cascade);
        entity.HasIndex(b => new { b.OrgId, b.CreatedAt });
    }
}

/// <summary>
/// A channel of an import batch (E02/F01/US03): one row for each batch and channel code (unique index), carrying the org (TN-2, restrict);
/// its items go with it (cascade). The reads of the import and of the onboarding plan go by org and channel, so that has an index too.
/// </summary>
internal sealed class OctorateInventoryImportChannelConfiguration : IEntityTypeConfiguration<OctorateInventoryImportChannel>
{
    public void Configure(EntityTypeBuilder<OctorateInventoryImportChannel> entity)
    {
        entity.HasOne<Org>().WithMany().HasForeignKey(c => c.OrgId).OnDelete(DeleteBehavior.Restrict);
        entity.HasMany(c => c.Items)
            .WithOne()
            .HasForeignKey(i => i.ChannelId)
            .OnDelete(DeleteBehavior.Cascade);
        entity.HasIndex(c => new { c.BatchId, c.ChannelCode })
            .IsUnique()
            .HasDatabaseName(OctorateInventoryImportChannel.BatchChannelIndexName);
        entity.HasIndex(c => new { c.OrgId, c.ChannelCode });
    }
}

/// <summary>
/// An external item of an import (E02/F01/US03): one row for each channel row and Octorate connection (unique index, so a connection is never
/// held twice by a channel), carrying the org (TN-2, restrict). A second index finds, by org and connection, what any batch already did for a
/// connection.
/// </summary>
internal sealed class OctorateInventoryImportItemConfiguration : IEntityTypeConfiguration<OctorateInventoryImportItem>
{
    public void Configure(EntityTypeBuilder<OctorateInventoryImportItem> entity)
    {
        entity.HasOne<Org>().WithMany().HasForeignKey(i => i.OrgId).OnDelete(DeleteBehavior.Restrict);
        entity.HasIndex(i => new { i.ChannelId, i.OctorateConnectionId })
            .IsUnique()
            .HasDatabaseName(OctorateInventoryImportItem.ChannelConnectionIndexName);
        entity.HasIndex(i => new { i.OrgId, i.OctorateConnectionId })
            .HasDatabaseName(OctorateInventoryImportItem.OrgConnectionIndexName);
    }
}
