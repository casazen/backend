using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Options;
using Casazen.Core.Suppliers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Casazen.Infrastructure.Data.Configurations;

internal sealed class PropertyQuesturaCredentialsConfiguration : IEntityTypeConfiguration<PropertyQuesturaCredentials>
{
    public void Configure(EntityTypeBuilder<PropertyQuesturaCredentials> entity)
    {
        entity.HasOne(c => c.Property)
            .WithMany()
            .HasForeignKey(c => c.PropertyId)
            .OnDelete(DeleteBehavior.Cascade);
        entity.HasIndex(c => c.PropertyId).IsUnique();
        entity.HasOne<Org>().WithMany().HasForeignKey(c => c.OrgId)
            .OnDelete(DeleteBehavior.Restrict);
        entity.HasIndex(c => c.OrgId);
    }
}
