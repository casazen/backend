using Casazen.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Data.Configurations;

internal static class StorefrontChatConfiguration
{
    internal static void Configure(ModelBuilder model)
    {
        var thread = model.Entity<StorefrontChatThread>();
        thread.Property(t => t.AccessHash).HasMaxLength(64);
        thread.Property(t => t.VisitorName).HasMaxLength(100);
        thread.HasIndex(t => new { t.OrgId, t.PropertyId, t.AccessHash }).IsUnique();
        thread.HasIndex(t => new { t.OrgId, t.LastMessageAt });
        thread.HasOne(t => t.Property).WithMany().HasForeignKey(t => t.PropertyId).OnDelete(DeleteBehavior.Cascade);

        var message = model.Entity<StorefrontChatMessage>();
        message.Property(m => m.IdempotencyKey).HasMaxLength(64);
        message.HasIndex(m => new { m.ThreadId, m.Direction, m.IdempotencyKey }).IsUnique();
        message.HasIndex(m => new { m.ThreadId, m.SentAt });
        thread.HasMany(t => t.Messages).WithOne().HasForeignKey(m => m.ThreadId).OnDelete(DeleteBehavior.Cascade);
    }
}
