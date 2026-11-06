using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Casazen.Infrastructure.Data.Configurations;

/// <summary>
/// The messages of the OTA conversations (E05/F01/US01/T01, ADR-032-R2, R3). The words are encrypted at rest through the Data Protection
/// converter of <c>EncryptedColumns</c> (purpose <c>Casazen.Message.Content</c>): the column holds the payload only, so it has no length and
/// its name says what it holds. A message of Octorate is one row of the organization (unique index on its Octorate id, where it has one); a
/// message CasaZen wrote is one row of its thread for a key of the sender's screen (unique index, where it has one).
/// </summary>
internal sealed class OtaMessageConfiguration : IEntityTypeConfiguration<OtaMessage>
{
    public void Configure(EntityTypeBuilder<OtaMessage> entity)
    {
        // The messages go with their thread.
        entity.HasOne<OtaMessageThread>()
            .WithMany()
            .HasForeignKey(m => m.ThreadId)
            .OnDelete(DeleteBehavior.Cascade);
        entity.HasOne<Org>()
            .WithMany()
            .HasForeignKey(m => m.OrgId)
            .OnDelete(DeleteBehavior.Restrict);

        // The column says what it holds: encrypted at rest. There is no plaintext column for the words of a message.
        entity.Property(m => m.Text).HasColumnName("TextEncrypted").IsRequired();

        // A message of Octorate, delivered twice (the webhook retries for two days, the reconciliation reads it again), is one row.
        entity.HasIndex(m => new { m.OrgId, m.OctorateMessageId })
            .IsUnique()
            .HasFilter("\"OctorateMessageId\" IS NOT NULL")
            .HasDatabaseName(OtaMessage.OrgOctorateMessageIndexName);

        // A retried send with the same key loses the race here (23505) and reads the first message.
        entity.HasIndex(m => new { m.ThreadId, m.IdempotencyKey })
            .IsUnique()
            .HasFilter("\"IdempotencyKey\" IS NOT NULL")
            .HasDatabaseName(OtaMessage.ThreadIdempotencyIndexName);

        // The thread in order.
        entity.HasIndex(m => new { m.ThreadId, m.SentAt })
            .HasDatabaseName("IX_OtaMessages_ThreadId_SentAt");

        entity.ToTable(t =>
        {
            t.HasCheckConstraint(
                "CK_OtaMessages_Direction",
                $"\"Direction\" IN ({(int)OtaMessageDirection.Inbound}, {(int)OtaMessageDirection.Outbound})");
            t.HasCheckConstraint(
                "CK_OtaMessages_DeliveryState",
                $"\"DeliveryState\" IN ({(int)OtaMessageDeliveryState.Created}, {(int)OtaMessageDeliveryState.WaitingAuthorization}, "
                + $"{(int)OtaMessageDeliveryState.Sent}, {(int)OtaMessageDeliveryState.Error}, {(int)OtaMessageDeliveryState.ManualVerify})");
            t.HasCheckConstraint(
                "CK_OtaMessages_Origin",
                $"\"Origin\" IN ({(int)MessageOrigin.Guest}, {(int)MessageOrigin.Person}, {(int)MessageOrigin.Assistant})");
        });
    }
}
