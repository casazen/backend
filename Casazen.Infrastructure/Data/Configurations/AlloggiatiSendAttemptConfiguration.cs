using Casazen.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Casazen.Infrastructure.Data.Configurations;

/// <summary>
/// The attempts of the Alloggiati transmission (AW-R04, ADR-015-R6, E03/F01/US05/T01). At most one open attempt per booking: the filtered
/// unique index on the booking covers the open statuses (reserved, sending, to verify), so two concurrent callers can never both hold one
/// and an outcome nobody knows blocks the next send. A booking with attempts is never deleted (restrict); a receipt removed at the end of its
/// retention leaves the attempt without it (SET NULL). The org restricts like every other <c>OrgId</c> foreign key (TN-2).
/// </summary>
internal sealed class AlloggiatiSendAttemptConfiguration : IEntityTypeConfiguration<AlloggiatiSendAttempt>
{
    private static readonly string OpenStatuses =
        $"{(int)AlloggiatiAttemptStatus.Reserved}, {(int)AlloggiatiAttemptStatus.Sending}, {(int)AlloggiatiAttemptStatus.ToVerify}";

    public void Configure(EntityTypeBuilder<AlloggiatiSendAttempt> entity)
    {
        entity.HasOne<Booking>()
            .WithMany()
            .HasForeignKey(a => a.BookingId)
            .OnDelete(DeleteBehavior.Restrict);
        entity.HasOne<AlloggiatiReceipt>()
            .WithMany()
            .HasForeignKey(a => a.ReceiptId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.SetNull);
        entity.HasOne<Org>().WithMany().HasForeignKey(a => a.OrgId).OnDelete(DeleteBehavior.Restrict);
        entity.Property(a => a.SnapshotHash).IsFixedLength();
        entity.Property(a => a.GuestsJson).HasColumnType("jsonb");
        entity.HasIndex(a => a.BookingId)
            .IsUnique()
            .HasFilter($"\"Status\" IN ({OpenStatuses})")
            .HasDatabaseName(AlloggiatiSendAttempt.OpenPerBookingIndexName);
        entity.HasIndex(a => new { a.OrgId, a.BookingId });
    }
}
