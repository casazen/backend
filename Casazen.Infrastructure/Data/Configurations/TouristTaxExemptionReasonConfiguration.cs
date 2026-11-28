using Casazen.Core.Entities;
using Casazen.Infrastructure.Data.Seeds;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Casazen.Infrastructure.Data.Configurations;

internal sealed class TouristTaxExemptionReasonConfiguration : IEntityTypeConfiguration<TouristTaxExemptionReason>
{
    public const string CodeIndexName = "UIX_TouristTaxExemptionReasons_Code";

    public void Configure(EntityTypeBuilder<TouristTaxExemptionReason> entity)
    {
        entity.HasIndex(r => r.Code)
            .IsUnique()
            .HasDatabaseName(CodeIndexName);
        entity.HasData(TouristTaxExemptionReasonSeed.Build());
    }
}
