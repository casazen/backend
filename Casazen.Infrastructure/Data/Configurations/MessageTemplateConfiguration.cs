using Casazen.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Casazen.Infrastructure.Data.Configurations;

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
