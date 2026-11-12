using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Casazen.Infrastructure.Data.Configurations;

/// <summary>
/// The copy of the OTA conversations (E05/F01/US01/T01, ADR-032-R1). One thread per Octorate thread and organization (unique index): two
/// deliveries of the same thread are one row. The property and the booking are optional and let go (SET NULL) when they are removed: the
/// thread stays with the organization, unassigned, because the copy of a conversation is not lost with a listing.
/// </summary>
internal sealed class OtaMessageThreadConfiguration : IEntityTypeConfiguration<OtaMessageThread>
{
    public void Configure(EntityTypeBuilder<OtaMessageThread> entity)
    {
        entity.HasOne(t => t.Property)
            .WithMany()
            .HasForeignKey(t => t.PropertyId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.SetNull);
        entity.HasOne(t => t.Booking)
            .WithMany()
            .HasForeignKey(t => t.BookingId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.SetNull);
        entity.HasOne<Org>()
            .WithMany()
            .HasForeignKey(t => t.OrgId)
            .OnDelete(DeleteBehavior.Restrict);

        // A thread of Octorate is one row of the organization, whoever delivers it and however many times.
        entity.HasIndex(t => new { t.OrgId, t.OctorateThreadId })
            .IsUnique()
            .HasDatabaseName(OtaMessageThread.OrgThreadIndexName);

        // The list of the threads of a property (and the unassigned ones, PropertyId null) by last message; the threads of a booking.
        entity.HasIndex(t => new { t.OrgId, t.PropertyId, t.LastMessageAt })
            .HasDatabaseName("IX_OtaMessageThreads_OrgId_PropertyId_LastMessageAt");
        entity.HasIndex(t => new { t.OrgId, t.BookingId })
            .HasDatabaseName("IX_OtaMessageThreads_OrgId_BookingId");

        // The threads whose booking CasaZen did not hold yet: linked by the reservation Octorate gave, without a call to Octorate.
        entity.HasIndex(t => new { t.OrgId, t.OctorateReservationId })
            .HasFilter("\"BookingId\" IS NULL AND \"OctorateReservationId\" IS NOT NULL")
            .HasDatabaseName("IX_OtaMessageThreads_OrgId_OctorateReservationId_Unlinked");

        entity.ToTable(t =>
        {
            t.HasCheckConstraint(
                "CK_OtaMessageThreads_ChannelSource",
                $"\"ChannelSource\" IN ({(int)OtaChannel.Airbnb}, {(int)OtaChannel.Booking}, {(int)OtaChannel.Vrbo}, {(int)OtaChannel.Expedia})");
            t.HasCheckConstraint(
                "CK_OtaMessageThreads_Kind",
                $"\"Kind\" IN ({(int)OtaThreadKind.Reservation}, {(int)OtaThreadKind.Request})");
            t.HasCheckConstraint(
                "CK_OtaMessageThreads_LastMessageDirection",
                $"\"LastMessageDirection\" IS NULL OR \"LastMessageDirection\" IN ({(int)OtaMessageDirection.Inbound}, {(int)OtaMessageDirection.Outbound})");
        });
    }
}
