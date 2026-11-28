using Casazen.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Casazen.Infrastructure.Data.Configurations;

internal sealed class OwnerStayRuleConfiguration : IEntityTypeConfiguration<OwnerStayRule>
{
    public void Configure(EntityTypeBuilder<OwnerStayRule> entity)
    {
        entity.HasOne<Org>().WithMany().HasForeignKey(r => r.OrgId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne<Property>().WithMany().HasForeignKey(r => r.PropertyId).OnDelete(DeleteBehavior.Cascade);
        entity.HasOne<PropertyOwner>().WithMany().HasForeignKey(r => r.PropertyOwnerId).OnDelete(DeleteBehavior.Cascade);
        entity.Property(r => r.BlackoutsJson).HasColumnType("jsonb");
        entity.HasIndex(r => r.PropertyId).IsUnique().HasFilter("\"PropertyOwnerId\" IS NULL").HasDatabaseName("UIX_OwnerStayRules_Property");
        entity.HasIndex(r => new { r.PropertyId, r.PropertyOwnerId }).IsUnique().HasFilter("\"PropertyOwnerId\" IS NOT NULL")
            .HasDatabaseName("UIX_OwnerStayRules_Property_Owner");
        entity.HasIndex(r => r.OrgId);
        entity.ToTable(table =>
        {
            table.HasCheckConstraint("CK_OwnerStayRules_Allowance", "\"YearlyNightAllowance\" IS NULL OR \"YearlyNightAllowance\" BETWEEN 0 AND 366");
            table.HasCheckConstraint("CK_OwnerStayRules_Advance", "\"MinAdvanceDays\" >= 0 AND (\"MaxAdvanceDays\" IS NULL OR \"MaxAdvanceDays\" >= \"MinAdvanceDays\")");
            table.HasCheckConstraint("CK_OwnerStayRules_Nights", "\"MinNights\" >= 1 AND (\"MaxNights\" IS NULL OR \"MaxNights\" >= \"MinNights\")");
            table.HasCheckConstraint("CK_OwnerStayRules_Period", "\"ActiveFrom\" IS NULL OR \"ActiveUntil\" IS NULL OR \"ActiveUntil\" >= \"ActiveFrom\"");
        });
    }
}

internal sealed class OwnerStayRequestConfiguration : IEntityTypeConfiguration<OwnerStayRequest>
{
    public void Configure(EntityTypeBuilder<OwnerStayRequest> entity)
    {
        entity.HasOne<Org>().WithMany().HasForeignKey(s => s.OrgId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne<Property>().WithMany().HasForeignKey(s => s.PropertyId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne<PropertyOwner>().WithMany().HasForeignKey(s => s.PropertyOwnerId).OnDelete(DeleteBehavior.Restrict);
        entity.HasIndex(s => new { s.PropertyId, s.Status, s.StartDate });
        entity.HasIndex(s => new { s.OrgId, s.PropertyOwnerId, s.StartDate });
        entity.HasIndex(s => new { s.Status, s.EndDate });
        entity.HasIndex(s => s.CalendarBlockId).IsUnique().HasFilter("\"CalendarBlockId\" IS NOT NULL");
        entity.ToTable(table => table.HasCheckConstraint("CK_OwnerStayRequests_Dates", "\"EndDate\" > \"StartDate\" AND \"Nights\" >= 1"));
    }
}

internal sealed class OwnerDocumentConfiguration : IEntityTypeConfiguration<OwnerDocument>
{
    public void Configure(EntityTypeBuilder<OwnerDocument> entity)
    {
        entity.HasOne<Org>().WithMany().HasForeignKey(d => d.OrgId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne<PropertyOwner>().WithMany().HasForeignKey(d => d.PropertyOwnerId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne<Property>().WithMany().HasForeignKey(d => d.PropertyId).OnDelete(DeleteBehavior.Restrict);
        entity.HasIndex(d => new { d.OrgId, d.PropertyOwnerId });
    }
}

internal sealed class OwnerDocumentVersionConfiguration : IEntityTypeConfiguration<OwnerDocumentVersion>
{
    public void Configure(EntityTypeBuilder<OwnerDocumentVersion> entity)
    {
        entity.HasOne<OwnerDocument>().WithMany().HasForeignKey(v => v.OwnerDocumentId).OnDelete(DeleteBehavior.Restrict);
        entity.HasIndex(v => new { v.OwnerDocumentId, v.VersionNumber }).IsUnique();
        entity.HasIndex(v => new { v.OrgId, v.ExpiresOn });
        entity.ToTable(table =>
        {
            table.HasCheckConstraint("CK_OwnerDocumentVersions_Number", "\"VersionNumber\" >= 1");
            table.HasCheckConstraint("CK_OwnerDocumentVersions_Dates", "\"EffectiveFrom\" IS NULL OR \"ExpiresOn\" IS NULL OR \"ExpiresOn\" >= \"EffectiveFrom\"");
        });
    }
}

internal sealed class OwnerDocumentDownloadConfiguration : IEntityTypeConfiguration<OwnerDocumentDownload>
{
    public void Configure(EntityTypeBuilder<OwnerDocumentDownload> entity)
    {
        entity.HasOne<OwnerDocument>().WithMany().HasForeignKey(d => d.OwnerDocumentId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne<OwnerDocumentVersion>().WithMany().HasForeignKey(d => d.OwnerDocumentVersionId).OnDelete(DeleteBehavior.Restrict);
        entity.HasIndex(d => new { d.OwnerDocumentId, d.DownloadedAt });
        entity.ToTable(table => table.HasCheckConstraint(
            "CK_OwnerDocumentDownloads_Who", "(\"OwnerUserId\" IS NULL) <> (\"UserId\" IS NULL)"));
    }
}

/// <summary>
/// The delegated contacts (E07/F07/US01/T02): the owner profile restricts; the secret of a link is unique; one contact in force (invited or active) per
/// owner profile and address (found by its keyed hash: the address itself is encrypted).
/// </summary>
internal sealed class OwnerPortalContactConfiguration : IEntityTypeConfiguration<OwnerPortalContact>
{
    public void Configure(EntityTypeBuilder<OwnerPortalContact> entity)
    {
        entity.HasOne<Org>().WithMany().HasForeignKey(c => c.OrgId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne<PropertyOwner>().WithMany().HasForeignKey(c => c.PropertyOwnerId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne<OwnerUser>().WithMany().HasForeignKey(c => c.OwnerUserId).OnDelete(DeleteBehavior.Restrict);
        entity.HasIndex(c => c.TokenHash).IsUnique();
        entity.HasIndex(c => new { c.OrgId, c.PropertyOwnerId, c.EmailHash }).IsUnique().HasFilter("\"Status\" <> 3")
            .HasDatabaseName("UIX_OwnerPortalContacts_Owner_Email_InForce");
        entity.HasIndex(c => new { c.OwnerUserId, c.Status });
        entity.ToTable(table => table.HasCheckConstraint("CK_OwnerPortalContacts_Permissions", "\"Permissions\" > 0"));
    }
}

internal sealed class OwnerConversationConfiguration : IEntityTypeConfiguration<OwnerConversation>
{
    public void Configure(EntityTypeBuilder<OwnerConversation> entity)
    {
        entity.HasOne<Org>().WithMany().HasForeignKey(c => c.OrgId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne<PropertyOwner>().WithMany().HasForeignKey(c => c.PropertyOwnerId).OnDelete(DeleteBehavior.Restrict);
        entity.HasIndex(c => new { c.OrgId, c.PropertyOwnerId, c.LastMessageAt });
    }
}

internal sealed class OwnerMessageConfiguration : IEntityTypeConfiguration<OwnerMessage>
{
    public void Configure(EntityTypeBuilder<OwnerMessage> entity)
    {
        entity.HasOne<OwnerConversation>().WithMany().HasForeignKey(m => m.ConversationId).OnDelete(DeleteBehavior.Restrict);
        entity.HasIndex(m => new { m.ConversationId, m.CreatedAt });
        entity.ToTable(table => table.HasCheckConstraint(
            "CK_OwnerMessages_Sender", "(\"SenderUserId\" IS NULL) <> (\"SenderOwnerUserId\" IS NULL)"));
    }
}
