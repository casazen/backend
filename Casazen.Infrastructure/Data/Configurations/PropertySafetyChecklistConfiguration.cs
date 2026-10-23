using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Options;
using Casazen.Core.Suppliers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Casazen.Infrastructure.Data.Configurations;

internal sealed class PropertySafetyChecklistConfiguration : IEntityTypeConfiguration<PropertySafetyChecklist>
{
    public void Configure(EntityTypeBuilder<PropertySafetyChecklist> entity)
    {
        entity.HasOne(c => c.Property)
            .WithOne()
            .HasForeignKey<PropertySafetyChecklist>(c => c.PropertyId)
            .OnDelete(DeleteBehavior.Cascade);
        entity.HasIndex(c => c.PropertyId).IsUnique();
        entity.HasOne<Org>().WithMany().HasForeignKey(c => c.OrgId)
            .OnDelete(DeleteBehavior.Restrict);
        entity.HasIndex(c => c.OrgId);
        entity.HasMany(c => c.Items)
            .WithOne(i => i.Checklist)
            .HasForeignKey(i => i.ChecklistId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
