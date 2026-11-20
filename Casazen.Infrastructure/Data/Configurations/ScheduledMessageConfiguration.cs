using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Casazen.Infrastructure.Data.Configurations;

/// <summary>
/// The messages of the routines planned for a booking (E05/F07/US01/T01, NF-71). One row for each booking, routine and action (<see cref="ScheduledMessage.PlanIndexName"/>); the rows go with their
/// booking and their property, and survive a routine that is deleted (the key becomes <c>null</c>, the name stays). The text of the host's edit is encrypted at rest through the Data Protection
/// converter of <c>EncryptedColumns</c> (purpose <c>Casazen.Message.Content</c>): the column holds the payload only, so it has no length and its name says what it holds. <c>xmin</c> is the
/// concurrency token, so the routine that fires, the sweep and «Send now» cannot all win the same row. The state is an integer with a check constraint of the values it may take.
/// </summary>
internal sealed class ScheduledMessageConfiguration : IEntityTypeConfiguration<ScheduledMessage>
{
    public void Configure(EntityTypeBuilder<ScheduledMessage> entity)
    {
        entity.HasOne(m => m.Booking)
            .WithMany()
            .HasForeignKey(m => m.BookingId)
            .OnDelete(DeleteBehavior.Cascade);
        entity.HasOne(m => m.Property)
            .WithMany()
            .HasForeignKey(m => m.PropertyId)
            .OnDelete(DeleteBehavior.Cascade);
        entity.HasOne<Routine>()
            .WithMany()
            .HasForeignKey(m => m.RoutineId)
            .OnDelete(DeleteBehavior.SetNull);
        entity.HasOne<Org>()
            .WithMany()
            .HasForeignKey(m => m.OrgId)
            .OnDelete(DeleteBehavior.Restrict);

        // The column says what it holds: the encrypted edit of the host. There is no plaintext column for the words of a message.
        entity.Property(m => m.TextOverride).HasColumnName("TextOverrideEncrypted");

        // xmin, as for the requests: two actors that read the same row cannot both change it.
        entity.Property(m => m.Version).IsRowVersion();

        // Planning twice never writes two rows. A row of a deleted routine has no key: those are all distinct, as they should be (the history).
        entity.HasIndex(m => new { m.BookingId, m.RoutineId, m.ActionIndex })
            .IsUnique()
            .HasDatabaseName(ScheduledMessage.PlanIndexName);

        // The sweep: the rows that are still to be decided (planned, blocked, failed), by the time they go out.
        entity.HasIndex(m => new { m.Status, m.PlannedAtUtc })
            .HasFilter($"\"Status\" IN ({(int)ScheduledMessageStatus.Scheduled}, {(int)ScheduledMessageStatus.Blocked}, {(int)ScheduledMessageStatus.Failed})")
            .HasDatabaseName("IX_ScheduledMessages_Open");

        // The screens: the messages of a property by time, and the timeline of a booking.
        entity.HasIndex(m => new { m.OrgId, m.PropertyId, m.PlannedAtUtc });
        entity.HasIndex(m => new { m.OrgId, m.BookingId });

        entity.ToTable(t =>
        {
            t.HasCheckConstraint(
                "CK_ScheduledMessages_Status",
                $"\"Status\" IN ({(int)ScheduledMessageStatus.Scheduled}, {(int)ScheduledMessageStatus.Sent}, {(int)ScheduledMessageStatus.Skipped}, "
                + $"{(int)ScheduledMessageStatus.Blocked}, {(int)ScheduledMessageStatus.Failed}, {(int)ScheduledMessageStatus.Cancelled}, {(int)ScheduledMessageStatus.Expired})");
            t.HasCheckConstraint("CK_ScheduledMessages_ActionIndex", "\"ActionIndex\" >= 0");
            t.HasCheckConstraint("CK_ScheduledMessages_Attempts", "\"Attempts\" >= 0");
        });
    }
}
