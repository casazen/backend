using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Options;
using Casazen.Core.Suppliers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Casazen.Infrastructure.Data.Configurations;

internal sealed class PropertySafetyChecklistItemConfiguration : IEntityTypeConfiguration<PropertySafetyChecklistItem>
{
    public void Configure(EntityTypeBuilder<PropertySafetyChecklistItem> entity)
    {
        entity.HasIndex(i => new { i.ChecklistId, i.Code }).IsUnique();
        entity.HasOne(i => i.EvidenceDocument)
            .WithMany()
            .HasForeignKey(i => i.EvidenceDocumentId)
            .OnDelete(DeleteBehavior.SetNull);
        entity.HasIndex(i => i.EvidenceDocumentId);
        entity.HasOne<Org>().WithMany().HasForeignKey(i => i.OrgId)
            .OnDelete(DeleteBehavior.Restrict);
        entity.HasIndex(i => i.OrgId);
    }
}
