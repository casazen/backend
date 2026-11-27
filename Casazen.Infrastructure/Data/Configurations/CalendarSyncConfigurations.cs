using Casazen.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Casazen.Infrastructure.Data.Configurations;

internal sealed class CalendarBlockConfiguration : IEntityTypeConfiguration<CalendarBlock>
{
    public void Configure(EntityTypeBuilder<CalendarBlock> entity)
    {
        // ─── Property iCal OTA sync (US-018 / #294) ─────────────────────────────
        entity.HasOne(b => b.Property)
            .WithMany()
            .HasForeignKey(b => b.PropertyId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasOne(b => b.Org)
            .WithMany()
            .HasForeignKey(b => b.OrgId)
            .OnDelete(DeleteBehavior.Restrict);

        // Blocks belong to their import feed (PC-11, A2-11): a UID is unique within its feed, and removing the feed
        // removes its blocks. Airbnb and Booking.com feeds of the same property never touch each other's blocks.
        entity.HasOne(b => b.Feed)
            .WithMany()
            .HasForeignKey(b => b.FeedId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasIndex(b => new { b.FeedId, b.ExternalUid })
            .IsUnique();

        // The index of the foreign key to the property, declared here so that it stays: EF drops the index of a foreign key when another index
        // starts with the same column, filtered or not, and the filtered index below holds the Octorate blocks only. Every read of the blocks
        // of a property (availability, calendar, export) goes through this one.
        entity.HasIndex(b => b.PropertyId);

        // The blocks read from the Octorate calendar have no feed (E02/F02/US01/T03): the unique index on (FeedId, ExternalUid) does not cover
        // them (a null feed is never equal to another), so their key is the property and the UID, for that source only
        // (CalendarBlockSource.Octorate = 2).
        entity.HasIndex(b => new { b.PropertyId, b.ExternalUid })
            .IsUnique()
            .HasFilter("\"Source\" = 2")
            .HasDatabaseName(CalendarBlock.OctorateUidIndexName);

        // OTA stay created from an imported block (CO-21, D7): one stay per block. Deleting the booking keeps the block,
        // which takes its nights again on its own.
        entity.HasOne(b => b.Booking)
            .WithMany()
            .HasForeignKey(b => b.BookingId)
            .OnDelete(DeleteBehavior.SetNull);

        entity.HasIndex(b => b.BookingId)
            .IsUnique();
    }
}

internal sealed class PropertyICalFeedConfiguration : IEntityTypeConfiguration<PropertyICalFeed>
{
    public void Configure(EntityTypeBuilder<PropertyICalFeed> entity)
    {
        entity.HasOne(f => f.Property)
            .WithMany()
            .HasForeignKey(f => f.PropertyId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasOne(f => f.Org)
            .WithMany()
            .HasForeignKey(f => f.OrgId)
            .OnDelete(DeleteBehavior.Restrict);

        entity.HasIndex(f => f.PropertyId);
    }
}

internal sealed class PropertyICalExportConfiguration : IEntityTypeConfiguration<PropertyICalExport>
{
    public void Configure(EntityTypeBuilder<PropertyICalExport> entity)
    {
        entity.HasOne(e => e.Property)
            .WithMany()
            .HasForeignKey(e => e.PropertyId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasOne(e => e.Org)
            .WithMany()
            .HasForeignKey(e => e.OrgId)
            .OnDelete(DeleteBehavior.Restrict);

        entity.HasIndex(e => e.PropertyId)
            .IsUnique();
    }

    internal static void ConfigureExportTokenIndex(EntityTypeBuilder<PropertyICalExport> entity)
    {
        entity.HasIndex(e => e.ExportToken)
            .IsUnique();
    }
}
