using Casazen.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Casazen.Infrastructure.Data.Configurations;

internal sealed class OrgActivityEntryConfiguration : IEntityTypeConfiguration<OrgActivityEntry>
{
    public void Configure(EntityTypeBuilder<OrgActivityEntry> entity)
    {
        entity.HasOne<Org>()
            .WithMany()
            .HasForeignKey(e => e.OrgId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasIndex(e => new { e.OrgId, e.When })
            .IsDescending(false, true)
            .HasDatabaseName("IX_OrgActivityEntries_OrgId_When");

        entity.HasIndex(e => new { e.OrgId, e.Type })
            .HasDatabaseName("IX_OrgActivityEntries_OrgId_Type");

        entity.HasIndex(e => e.When)
            .HasDatabaseName("IX_OrgActivityEntries_When");
    }
}
