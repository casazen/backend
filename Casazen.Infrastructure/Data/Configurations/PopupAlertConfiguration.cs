using Casazen.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Casazen.Infrastructure.Data.Configurations;

internal sealed class PopupAlertConfiguration : IEntityTypeConfiguration<PopupAlert>
{
    public void Configure(EntityTypeBuilder<PopupAlert> entity)
    {
        // The alerts of an org go with it (the one place an org is deleted, the merge of two duplicate supplier orgs, must not be
        // stopped by them), and the ones of a person go with the account (erasure): the row is a user id and a reference.
        entity.HasOne<Org>()
            .WithMany()
            .HasForeignKey(a => a.OrgId)
            .OnDelete(DeleteBehavior.Cascade);
        entity.HasOne<User>()
            .WithMany()
            .HasForeignKey(a => a.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        // By name, so a member appended later never changes the meaning of the rows already written. The urgency stays a number: the
        // list is sorted by it in SQL, highest first (AlertUrgency.High = 3).
        entity.Property(a => a.AlertType)
            .HasConversion<string>()
            .HasMaxLength(PopupAlert.AlertTypeMaxLength);

        // The pending alerts of a user: not dismissed, snoozed until an instant that has passed (GET /api/me/alerts/pending).
        entity.HasIndex(a => new { a.UserId, a.DismissedAt, a.RemindAt })
            .HasDatabaseName(PopupAlert.PendingIndexName);

        // The job that pushes the deferred and snoozed alerts when they fall due reads across every org, every minute: only the rows
        // still waiting for their push.
        entity.HasIndex(a => a.RemindAt)
            .HasFilter("\"RemindAt\" IS NOT NULL AND \"DismissedAt\" IS NULL AND \"DeliveredAt\" IS NULL")
            .HasDatabaseName(PopupAlert.DueIndexName);
    }
}

internal sealed class UserNotificationPreferencesConfiguration : IEntityTypeConfiguration<UserNotificationPreferences>
{
    public void Configure(EntityTypeBuilder<UserNotificationPreferences> entity)
    {
        // Same as the alerts: the preferences of a person go with the account and with the org.
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
