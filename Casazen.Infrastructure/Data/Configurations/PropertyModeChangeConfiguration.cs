using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Options;
using Casazen.Core.Suppliers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Casazen.Infrastructure.Data.Configurations;

internal sealed class PropertyModeChangeConfiguration : IEntityTypeConfiguration<PropertyModeChange>
{
    public void Configure(EntityTypeBuilder<PropertyModeChange> entity)
    {
        // The history goes with the property (a property is soft-deleted, so in practice it stays for good).
        entity.HasOne(c => c.Property)
            .WithMany()
            .HasForeignKey(c => c.PropertyId)
            .OnDelete(DeleteBehavior.Cascade);
        entity.HasOne<Org>()
            .WithMany()
            .HasForeignKey(c => c.OrgId)
            .OnDelete(DeleteBehavior.Restrict);

        // One change waiting for its day per property. The creation takes the property dates lock first, so this is the
        // net under it: a writer that skipped the lock gets 23505 on this index and answers property_mode_change_exists.
        entity.HasIndex(c => c.PropertyId)
            .IsUnique()
            .HasFilter($"\"Status\" = {(int)PropertyModeChangeStatus.Scheduled}")
            .HasDatabaseName(PropertyModeChange.OneScheduledIndexName);

        // The hourly job: the scheduled changes whose day has come.
        entity.HasIndex(c => new { c.Status, c.EffectiveDate });

        // The history of a property, newest first.
        entity.HasIndex(c => new { c.PropertyId, c.CreatedAt });
    }
}
