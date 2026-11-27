using Casazen.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Casazen.Infrastructure.Data.Configurations;

/// <summary>
/// The knowledge base of the assistant (E05/F05/US01/T01, NF-66): the entries of a property (cascade: they go with it) or of the whole organization (no property).
/// The text is encrypted at rest by <c>EncryptedColumns</c> (purpose <c>Casazen.Assistant.Content</c>), so the column has no length: the payload is longer than the
/// text, and the 1,000 characters are the service's rule. The index serves the retrieval of a property's entries by topic.
/// </summary>
internal sealed class PropertyKnowledgeEntryConfiguration : IEntityTypeConfiguration<PropertyKnowledgeEntry>
{
    public void Configure(EntityTypeBuilder<PropertyKnowledgeEntry> entity)
    {
        entity.HasOne<Org>()
            .WithMany()
            .HasForeignKey(e => e.OrgId)
            .OnDelete(DeleteBehavior.Restrict);
        entity.HasOne<Property>()
            .WithMany()
            .HasForeignKey(e => e.PropertyId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.Cascade);

        // The entries of a property (or of the organization, PropertyId null) by topic.
        entity.HasIndex(e => new { e.OrgId, e.PropertyId, e.Topic })
            .HasDatabaseName("IX_PropertyKnowledgeEntries_OrgId_PropertyId_Topic");
    }
}

/// <summary>
/// The rules of an organization for its assistant (E05/F05/US01/T01, NF-66): one row per organization (unique index). The lists are <c>text[]</c>. The version of the
/// instructions is the concurrency token of the row: two changes at the same moment cannot both claim the same version, the second one is refused and retried.
/// </summary>
internal sealed class OrgAssistantRulesConfiguration : IEntityTypeConfiguration<OrgAssistantRules>
{
    public void Configure(EntityTypeBuilder<OrgAssistantRules> entity)
    {
        entity.HasOne<Org>()
            .WithMany()
            .HasForeignKey(r => r.OrgId)
            .OnDelete(DeleteBehavior.Restrict);

        entity.HasIndex(r => r.OrgId)
            .IsUnique()
            .HasDatabaseName(OrgAssistantRules.OrgIndexName);

        entity.Property(r => r.InstructionVersion).IsConcurrencyToken();
    }
}

/// <summary>The exceptions of a property to the rules of its organization (E05/F05/US01/T01): one row per property (unique index), gone with the property (cascade).</summary>
internal sealed class PropertyAssistantSettingsConfiguration : IEntityTypeConfiguration<PropertyAssistantSettings>
{
    public void Configure(EntityTypeBuilder<PropertyAssistantSettings> entity)
    {
        entity.HasOne<Org>()
            .WithMany()
            .HasForeignKey(s => s.OrgId)
            .OnDelete(DeleteBehavior.Restrict);
        entity.HasOne<Property>()
            .WithMany()
            .HasForeignKey(s => s.PropertyId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasIndex(s => new { s.OrgId, s.PropertyId })
            .IsUnique()
            .HasDatabaseName(PropertyAssistantSettings.OrgPropertyIndexName);
    }
}

/// <summary>
/// What the assistant does in each situation (E05/F05/US01/T01, NF-66, NF-67): the organization's row of a situation and, as an exception, one row per property and
/// situation. Two partial unique indexes keep it so, since a unique index lets a null property through: one on (organization, situation) where there is no property,
/// one on (organization, property, situation) where there is one. The response template is encrypted by <c>EncryptedColumns</c> (no column length).
/// </summary>
internal sealed class AssistantSituationSettingConfiguration : IEntityTypeConfiguration<AssistantSituationSetting>
{
    public void Configure(EntityTypeBuilder<AssistantSituationSetting> entity)
    {
        entity.HasOne<Org>()
            .WithMany()
            .HasForeignKey(s => s.OrgId)
            .OnDelete(DeleteBehavior.Restrict);
        entity.HasOne<Property>()
            .WithMany()
            .HasForeignKey(s => s.PropertyId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasIndex(s => new { s.OrgId, s.Situation })
            .IsUnique()
            .HasFilter("\"PropertyId\" IS NULL")
            .HasDatabaseName(AssistantSituationSetting.OrgSituationIndexName);
        entity.HasIndex(s => new { s.OrgId, s.PropertyId, s.Situation })
            .IsUnique()
            .HasFilter("\"PropertyId\" IS NOT NULL")
            .HasDatabaseName(AssistantSituationSetting.PropertySituationIndexName);
    }
}

/// <summary>The lessons the assistant proposes (E05/F05/US01/T01, NF-66): the text is encrypted by <c>EncryptedColumns</c>; the proposals waiting for a decision are read by (organization, status).</summary>
internal sealed class AssistantLessonConfiguration : IEntityTypeConfiguration<AssistantLesson>
{
    public void Configure(EntityTypeBuilder<AssistantLesson> entity)
    {
        entity.HasOne<Org>()
            .WithMany()
            .HasForeignKey(l => l.OrgId)
            .OnDelete(DeleteBehavior.Restrict);
        entity.HasOne<Property>()
            .WithMany()
            .HasForeignKey(l => l.PropertyId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasIndex(l => new { l.OrgId, l.Status })
            .HasDatabaseName("IX_AssistantLessons_OrgId_Status");
    }
}

/// <summary>
/// The log of what the assistant decided for each incoming message (E05/F05/US01/T01, NF-67, PO-09): ids and codes only, never text. One row per message (unique index on
/// the id of the message: processing it twice finds the first row). The draft it points to is a foreign key that lets go (SET NULL) when the item of the approval queue
/// is removed; the property lets go too, because the log is the audit of what was sent to a guest and outlives a property that is removed. <c>SourceRefs</c> is jsonb.
/// </summary>
internal sealed class AssistantDecisionLogConfiguration : IEntityTypeConfiguration<AssistantDecisionLog>
{
    public void Configure(EntityTypeBuilder<AssistantDecisionLog> entity)
    {
        entity.HasOne<Org>()
            .WithMany()
            .HasForeignKey(l => l.OrgId)
            .OnDelete(DeleteBehavior.Restrict);
        entity.HasOne<Property>()
            .WithMany()
            .HasForeignKey(l => l.PropertyId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.SetNull);
        entity.HasOne<ApprovalQueueItem>()
            .WithMany()
            .HasForeignKey(l => l.ApprovalItemId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.SetNull);

        // One decision per message: a second processing of the same message loses the race here (23505) and reads the first one.
        entity.HasIndex(l => l.InboundMessageId)
            .IsUnique()
            .HasDatabaseName(AssistantDecisionLog.InboundMessageIndexName);

        // The decisions of a property (or of the conversations that have none), newest first.
        entity.HasIndex(l => new { l.OrgId, l.PropertyId, l.CreatedAt })
            .HasDatabaseName("IX_AssistantDecisionLogs_OrgId_PropertyId_CreatedAt");
    }
}

/// <summary>The changes of trust level CasaZen proposes (E05/F05/US01/T01, NF-67): the proposals waiting for a decision are read by (organization, status); they go with their property (cascade).</summary>
internal sealed class AssistantTrustProposalConfiguration : IEntityTypeConfiguration<AssistantTrustProposal>
{
    public void Configure(EntityTypeBuilder<AssistantTrustProposal> entity)
    {
        entity.HasOne<Org>()
            .WithMany()
            .HasForeignKey(p => p.OrgId)
            .OnDelete(DeleteBehavior.Restrict);
        entity.HasOne<Property>()
            .WithMany()
            .HasForeignKey(p => p.PropertyId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasIndex(p => new { p.OrgId, p.Status })
            .HasDatabaseName("IX_AssistantTrustProposals_OrgId_Status");
    }
}
