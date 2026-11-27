using Casazen.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Casazen.Infrastructure.Data.Configurations;

internal sealed class PopupAlertConfiguration : IEntityTypeConfiguration<PopupAlert>
{
    public void Configure(EntityTypeBuilder<PopupAlert> entity)
    {
        entity.HasOne<Org>()
            .WithMany()
            .HasForeignKey(a => a.OrgId)
            .OnDelete(DeleteBehavior.Cascade);
        entity.HasOne<User>()
            .WithMany()
            .HasForeignKey(a => a.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.Property(a => a.AlertType)
            .HasConversion<string>()
            .HasMaxLength(PopupAlert.AlertTypeMaxLength);

        entity.HasIndex(a => new { a.UserId, a.DismissedAt, a.RemindAt })
            .HasDatabaseName(PopupAlert.PendingIndexName);

        entity.HasIndex(a => a.RemindAt)
            .HasFilter("\"RemindAt\" IS NOT NULL AND \"DismissedAt\" IS NULL AND \"DeliveredAt\" IS NULL")
            .HasDatabaseName(PopupAlert.DueIndexName);
    }
}

internal sealed class UserNotificationPreferencesConfiguration : IEntityTypeConfiguration<UserNotificationPreferences>
{
    public void Configure(EntityTypeBuilder<UserNotificationPreferences> entity)
    {
        entity.HasOne<Org>()
            .WithMany()
            .HasForeignKey(p => p.OrgId)
            .OnDelete(DeleteBehavior.Cascade);
        entity.HasOne<User>()
            .WithMany()
            .HasForeignKey(p => p.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        // One row per user and org: the router that creates it for a first alert, run twice at the same moment, loses the race on this
        // index (23505) and reads the row the other one wrote. The defaults (22:00-08:00, legal alerts override) live in the entity, not in
        // the column: a column default would also replace a quiet end that the user set to midnight, the default of the type.
        entity.HasIndex(p => new { p.OrgId, p.UserId })
            .IsUnique()
            .HasDatabaseName(UserNotificationPreferences.OncePerUserIndexName);
    }
}
