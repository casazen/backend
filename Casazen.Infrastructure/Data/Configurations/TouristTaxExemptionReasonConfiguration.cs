using Casazen.Core.Entities;
using Casazen.Infrastructure.Data.Seeds;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Casazen.Infrastructure.Data.Configurations;

/// <summary>
/// The reasons of a tourist tax exemption (NF-46): platform reference data, unique by code, seeded with deterministic ids
/// (<see cref="TouristTaxExemptionReasonSeed"/>) and with no Octorate code (Q-004).
/// </summary>
internal sealed class TouristTaxExemptionReasonConfiguration : IEntityTypeConfiguration<TouristTaxExemptionReason>
{
    /// <summary>Name of the unique index on the code.</summary>
    public const string CodeIndexName = "UIX_TouristTaxExemptionReasons_Code";

    public void Configure(EntityTypeBuilder<TouristTaxExemptionReason> entity)
    {
        entity.HasIndex(r => r.Code)
            .IsUnique()
            .HasDatabaseName(CodeIndexName);
        entity.HasData(TouristTaxExemptionReasonSeed.Build());
    }
}
