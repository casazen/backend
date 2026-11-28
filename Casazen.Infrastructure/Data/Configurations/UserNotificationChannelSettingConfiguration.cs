using Casazen.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Casazen.Infrastructure.Data.Configurations;

internal sealed class UserNotificationChannelSettingConfiguration : IEntityTypeConfiguration<UserNotificationChannelSetting>
{
    public void Configure(EntityTypeBuilder<UserNotificationChannelSetting> entity)
    {
        entity.HasOne<Org>()
            .WithMany()
            .HasForeignKey(s => s.OrgId)
            .OnDelete(DeleteBehavior.Cascade);
        entity.HasOne<User>()
            .WithMany()
            .HasForeignKey(s => s.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.Property(s => s.EventType)
            .HasConversion<string>()
            .HasMaxLength(UserNotificationChannelSetting.EventTypeMaxLength);

        // One choice per user and kind of event: a save writes it or changes it, and a second save at the same moment loses the race on
        // this index (23505) and changes the row the other one wrote.
        entity.HasIndex(s => new { s.OrgId, s.UserId, s.EventType })
            .IsUnique()
            .HasDatabaseName(UserNotificationChannelSetting.OncePerUserAndEventIndexName);

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
