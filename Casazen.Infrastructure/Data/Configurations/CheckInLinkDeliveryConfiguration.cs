using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Casazen.Infrastructure.Data.Configurations;

/// <summary>
/// Where the check-in link of a booking stands on its way to the guest (E23/F01/US01/T01, T02, NF-122). One row per booking (<see cref="CheckInLinkDelivery.BookingIndexName"/>);
/// the rows go with their booking. The raw token of the link is encrypted at rest through the Data Protection converter of <c>EncryptedColumns</c> (purpose
/// <c>Casazen.CheckInLink.Token</c>): the column holds the payload only, so it has no length and its name says what it holds. The state and the channel are integers
/// with a check constraint of the values they may take.
/// </summary>
internal sealed class CheckInLinkDeliveryConfiguration : IEntityTypeConfiguration<CheckInLinkDelivery>
{
    public void Configure(EntityTypeBuilder<CheckInLinkDelivery> entity)
    {
        entity.HasOne<Booking>()
            .WithMany()
            .HasForeignKey(d => d.BookingId)
            .OnDelete(DeleteBehavior.Cascade);
        entity.HasOne<GuestCheckInSession>()
            .WithMany()
            .HasForeignKey(d => d.SessionId)
            .OnDelete(DeleteBehavior.SetNull);
        entity.HasOne<OtaMessage>()
            .WithMany()
            .HasForeignKey(d => d.OtaMessageId)
            .OnDelete(DeleteBehavior.SetNull);
        entity.HasOne<Org>()
            .WithMany()
            .HasForeignKey(d => d.OrgId)
            .OnDelete(DeleteBehavior.Restrict);

        entity.Property(d => d.LinkToken).HasColumnName("LinkTokenEncrypted");

        entity.HasIndex(d => d.BookingId)
            .IsUnique()
            .HasDatabaseName(CheckInLinkDelivery.BookingIndexName);

        entity.HasIndex(d => new { d.OrgId, d.State })
            .HasFilter("\"EndedAt\" IS NULL")
            .HasDatabaseName("IX_CheckInLinkDeliveries_OrgId_State_Open");

        entity.ToTable(t =>
        {
            t.HasCheckConstraint(
                "CK_CheckInLinkDeliveries_Channel",
                $"\"Channel\" IN ({(int)CheckInLinkChannel.Email}, {(int)CheckInLinkChannel.PortalChat}, {(int)CheckInLinkChannel.Manual})");
            t.HasCheckConstraint(
                "CK_CheckInLinkDeliveries_State",
                $"\"State\" IN ({(int)CheckInLinkDeliveryState.Sent}, {(int)CheckInLinkDeliveryState.WaitingChatPermission}, "
                + $"{(int)CheckInLinkDeliveryState.WaitingThread}, {(int)CheckInLinkDeliveryState.WaitingContact}, "
                + $"{(int)CheckInLinkDeliveryState.WaitingStaticIps}, {(int)CheckInLinkDeliveryState.Failed}, {(int)CheckInLinkDeliveryState.Sending})");
        });
    }
}
