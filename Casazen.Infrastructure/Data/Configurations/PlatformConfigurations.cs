using Casazen.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Casazen.Infrastructure.Data.Configurations;

internal sealed class ApprovalQueueItemConfiguration : IEntityTypeConfiguration<ApprovalQueueItem>
{
    public void Configure(EntityTypeBuilder<ApprovalQueueItem> entity)
    {
        // ─── The approval queue (NF-06, E05/F04, NF-68) ─────────────────────────────────────────────────────
        // The list of an org reads its pending items (and the expiry job the pending ones past their time). The columns of the action, of the edit and of the
        // rejection are plain nullable columns (an existing row keeps working: OriginActionStatus 0 is NotRequired); the words are encrypted by EncryptedColumns.
        entity.HasIndex(i => new { i.OrgId, i.Status });
    }
}

internal sealed class RoutineExecutionConfiguration : IEntityTypeConfiguration<RoutineExecution>
{
    public void Configure(EntityTypeBuilder<RoutineExecution> entity)
    {
        // ─── Automation routines (NF-26, ADR-022) ────────────────────────────────
        // Unique constraint on (RoutineId, EventId) ensures idempotence: a routine executes at most once per event (ADR-022-R7).
        entity.HasIndex(e => new { e.RoutineId, e.EventId })
            .IsUnique();
    }
}

internal sealed class OutboxEventConfiguration : IEntityTypeConfiguration<OutboxEvent>
{
    public void Configure(EntityTypeBuilder<OutboxEvent> entity)
    {
        // ─── Transactional outbox of the domain events (ADR-022) ─────────────────
        // A time trigger emitted twice (a restart, two overlapping runs) loses on the unique key of its org (ADR-022-R8); the events
        // without a key are never compared. The dispatcher reads the undispatched events by OccurredAt and the cleanup the old
        // dispatched ones: both through (DispatchedAt, OccurredAt).
        entity.HasIndex(e => new { e.OrgId, e.DedupKey })
            .IsUnique()
            .HasFilter("\"DedupKey\" IS NOT NULL")
            .HasDatabaseName(OutboxEvent.DedupIndexName);
        entity.HasIndex(e => new { e.DispatchedAt, e.OccurredAt });
    }
}

internal sealed class InternalActivityConfiguration : IEntityTypeConfiguration<InternalActivity>
{
    public void Configure(EntityTypeBuilder<InternalActivity> entity)
    {
        entity.HasOne(a => a.Property)
            .WithMany()
            .HasForeignKey(a => a.PropertyId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.Cascade);
        entity.Property(a => a.Action)
            .HasConversion<string>()
            .HasMaxLength(50);
        entity.HasIndex(a => a.OrgId);
        entity.HasIndex(a => new { a.OrgId, a.Urgency, a.Deadline })
            .HasFilter("\"Action\" IS NOT NULL AND \"CompletedAt\" IS NULL")
            .HasDatabaseName("IX_InternalActivities_OpenTodo");
        entity.HasIndex(a => new { a.OrgId, a.ActivityType, a.EntityType, a.EntityId })
            .IsUnique()
            .HasFilter("\"Action\" IS NOT NULL AND \"CompletedAt\" IS NULL")
            .HasDatabaseName(InternalActivity.OpenTodoIndexName);
    }
}

internal sealed class WizardDraftConfiguration : IEntityTypeConfiguration<WizardDraft>
{
    public void Configure(EntityTypeBuilder<WizardDraft> entity)
    {
        // ─── Drafts of the wizards (ADR-024) ─────────────────────────────────────
        // One draft per person, wizard and entity. The unique indexes are what makes two devices that save the first draft at the same
        // moment lose to each other (the loser gets 23505 and is answered as a conflict). The database takes two NULLs for different
        // values, so the draft of a new entity (no EntityId) has an index of its own. The anonymized rows (UserId = '') are left out of
        // both: they all share the empty UserId, and two of them for the same org, wizard and entity would fail the retention job.
        // xmin is the concurrency token: two parallel saves of the same version cannot both win. The index on ExpiresAt serves the
        // retention, which reads the drafts that are over and not yet anonymized.
        entity.Property(d => d.RowVersion).IsRowVersion();
        entity.HasIndex(d => new { d.OrgId, d.UserId, d.WizardType, d.EntityId })
            .IsUnique()
            .HasFilter("\"EntityId\" IS NOT NULL AND \"UserId\" <> ''")
            .HasDatabaseName(WizardDraft.ExistingEntityIndexName);
        entity.HasIndex(d => new { d.OrgId, d.UserId, d.WizardType })
            .IsUnique()
            .HasFilter("\"EntityId\" IS NULL AND \"UserId\" <> ''")
            .HasDatabaseName(WizardDraft.NewEntityIndexName);
        entity.HasIndex(d => d.ExpiresAt)
            .HasFilter("\"UserId\" <> ''");
    }
}
