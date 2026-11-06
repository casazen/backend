using Casazen.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Casazen.Infrastructure.Data.Configurations;

internal sealed class UserNotificationChannelSettingConfiguration : IEntityTypeConfiguration<UserNotificationChannelSetting>
{
    public void Configure(EntityTypeBuilder<UserNotificationChannelSetting> entity)
    {
        // Like the quiet hours: the choices of a person go with the account and with the org (erasure; the merge of two duplicate orgs).
        entity.HasOne<Org>()
            .WithMany()
            .HasForeignKey(s => s.OrgId)
            .OnDelete(DeleteBehavior.Cascade);
        entity.HasOne<User>()
            .WithMany()
            .HasForeignKey(s => s.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        // By name, so a member appended later never changes the meaning of the rows already written. The channels stay the integer of
        // their bits (NotificationChannels).
        entity.Property(s => s.EventType)
            .HasConversion<string>()
            .HasMaxLength(UserNotificationChannelSetting.EventTypeMaxLength);

        // One choice per user and kind of event: a save writes it or changes it, and a second save at the same moment loses the race on
        // this index (23505) and changes the row the other one wrote.
        entity.HasIndex(s => new { s.OrgId, s.UserId, s.EventType })
            .IsUnique()
            .HasDatabaseName(UserNotificationChannelSetting.OncePerUserAndEventIndexName);

        // The choices of the people to tell about an event: all the rows of some users, one org.
        entity.HasIndex(s => new { s.OrgId, s.UserId });
    }
}

internal sealed class NotificationEmailSlotConfiguration : IEntityTypeConfiguration<NotificationEmailSlot>
{
    public void Configure(EntityTypeBuilder<NotificationEmailSlot> entity)
    {
        entity.HasOne<Org>()
            .WithMany()
            .HasForeignKey(s => s.OrgId)
            .OnDelete(DeleteBehavior.Cascade);
        entity.HasOne<User>()
            .WithMany()
            .HasForeignKey(s => s.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        // One slot per person and conversation: the claim of the second message at the same moment loses the race on this index (23505).
        entity.HasIndex(s => new { s.OrgId, s.UserId, s.ThreadKey })
            .IsUnique()
            .HasDatabaseName(NotificationEmailSlot.OncePerUserAndThreadIndexName);
    }
}
