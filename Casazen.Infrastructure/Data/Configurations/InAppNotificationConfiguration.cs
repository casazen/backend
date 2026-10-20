using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Options;
using Casazen.Core.Suppliers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Casazen.Infrastructure.Data.Configurations;

internal sealed class InAppNotificationConfiguration : IEntityTypeConfiguration<InAppNotification>
{
    public void Configure(EntityTypeBuilder<InAppNotification> entity)
    {
        // The notifications of an org go with it (the one place an org is deleted, the merge of two duplicate supplier orgs,
        // must not be stopped by them), and the ones of a person go with the account (erasure).
        entity.HasOne<Org>()
            .WithMany()
            .HasForeignKey(n => n.OrgId)
            .OnDelete(DeleteBehavior.Cascade);
        entity.HasOne<User>()
            .WithMany()
            .HasForeignKey(n => n.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        // Once per event and user: the job that writes them, retried by Hangfire or queued twice for the same event, loses
        // the race on this index (23505) and re-reads. The key is the one of the push of the event.
        entity.HasIndex(n => new { n.DeliveryKey, n.UserId })
            .IsUnique()
            .HasDatabaseName(InAppNotification.OncePerEventAndUserIndexName);

        // The bell: the unread count of a user, and its list (newest first, optionally only the unread ones).
        entity.HasIndex(n => new { n.UserId, n.ReadAt, n.CreatedAt })
            .HasDatabaseName("IX_InAppNotifications_UserId_ReadAt_CreatedAt");

        // The tenant filter of every read, and the cascade of an org.
        entity.HasIndex(n => n.OrgId)
            .HasDatabaseName("IX_InAppNotifications_OrgId");

        // The daily retention deletes by age, whoever the user is.
        entity.HasIndex(n => n.CreatedAt)
            .HasDatabaseName("IX_InAppNotifications_CreatedAt");
    }
}
