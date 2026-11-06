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

        // AW-R02 (E03/F01/US05/T01): the Questura account of the row. gen_random_uuid() is volatile, so adding the column computes it for
        // each existing row: every row that existed got a key of its own. A new row brings its own key (the entity's initializer); the
        // properties of one «Gestione Appartamenti» account share theirs. The existing rows are single structures at version 1.
        entity.Property(c => c.AccountKey).HasDefaultValueSql("gen_random_uuid()");
        entity.Property(c => c.Profile)
            .HasDefaultValue(AlloggiatiProfile.SingleStructure)
            .HasSentinel((AlloggiatiProfile)(-1));
        entity.Property(c => c.CredentialVersion)
            .HasDefaultValue(1)
            .HasSentinel(-1);
        entity.HasIndex(c => new { c.OrgId, c.AccountKey });
    }
}
