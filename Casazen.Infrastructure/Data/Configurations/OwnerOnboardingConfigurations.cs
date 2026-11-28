using Casazen.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Casazen.Infrastructure.Data.Configurations;

/// <summary>
/// The key the agency gives its owners (E10/F02/US02/T01): unique inside the org, and only for the owners that have one (a filtered unique index,
/// so any number of owners without a key coexist). It is what lets the owner import find an owner again, and what stops two imports that race
/// from creating the same owner twice.
/// </summary>
internal sealed class PropertyOwnerConfiguration : IEntityTypeConfiguration<PropertyOwner>
{
    public void Configure(EntityTypeBuilder<PropertyOwner> entity)
    {
        entity.HasIndex(o => o.OrgId);
        entity.HasIndex(o => new { o.OrgId, o.ExternalKey })
            .IsUnique()
            .HasFilter("\"ExternalKey\" IS NOT NULL")
            .HasDatabaseName(PropertyOwner.OrgExternalKeyIndexName);

        entity.ToTable(table => table.HasCheckConstraint(
            "CK_PropertyOwners_ExternalShortLetUnits",
            $"\"ExternalShortLetUnits\" >= 0 AND \"ExternalShortLetUnits\" <= {PropertyOwner.ExternalShortLetUnitsMax}"));
    }
}
