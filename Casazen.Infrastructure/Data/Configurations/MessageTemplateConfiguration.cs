using Casazen.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Casazen.Infrastructure.Data.Configurations;

/// <summary>The message templates of a host (E05/F01/US01/T01): one per organization, name and language (unique index).</summary>
internal sealed class MessageTemplateConfiguration : IEntityTypeConfiguration<MessageTemplate>
{
    public void Configure(EntityTypeBuilder<MessageTemplate> entity)
    {
        entity.HasOne<Org>()
            .WithMany()
            .HasForeignKey(t => t.OrgId)
            .OnDelete(DeleteBehavior.Restrict);

        entity.HasIndex(t => new { t.OrgId, t.Name, t.Language })
            .IsUnique()
            .HasDatabaseName(MessageTemplate.OrgNameLanguageIndexName);
    }
}
