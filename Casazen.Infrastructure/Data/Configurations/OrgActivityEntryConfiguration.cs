using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Suppliers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Casazen.Infrastructure.Data.Configurations;

internal sealed class OrgActivityEntryConfiguration : IEntityTypeConfiguration<OrgActivityEntry>
{
    public void Configure(EntityTypeBuilder<OrgActivityEntry> entity)
    {
        // The log of an org goes with it. No foreign key to the account: the history outlives it and holds ids only.
        entity.HasOne<Org>()
            .WithMany()
            .HasForeignKey(e => e.OrgId)
            .OnDelete(DeleteBehavior.Cascade);

        // The page of the people who administer the org: the newest lines of one org (the tenant filter reads OrgId too).
        entity.HasIndex(e => new { e.OrgId, e.When })
            .IsDescending(false, true)
            .HasDatabaseName("IX_OrgActivityEntries_OrgId_When");

        // The filter by event, and the count of the access requests of one person.
        entity.HasIndex(e => new { e.OrgId, e.Type })
            .HasDatabaseName("IX_OrgActivityEntries_OrgId_Type");

        // The retention deletes across every org what is older than the cutoff.
        entity.HasIndex(e => e.When)
            .HasDatabaseName("IX_OrgActivityEntries_When");
    }
}
