using Casazen.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Casazen.Infrastructure.Data.Configurations;

/// <summary>
/// Who collects the tourist tax of a channel in a comune (ADR-037-R3): platform reference data, one row per channel and comune, never without
/// its source (the database refuses an empty source and a comune code that is not six digits). No seed.
/// </summary>
internal sealed class TouristTaxChannelRuleConfiguration : IEntityTypeConfiguration<TouristTaxChannelRule>
{
    /// <summary>Name of the check that a rule always has a source.</summary>
    public const string SourceRequiredCheckName = "CK_TouristTaxChannelRules_SourceUrlRequired";

    /// <summary>Name of the check that the comune is an ISTAT code of six digits.</summary>
    public const string IstatCodeCheckName = "CK_TouristTaxChannelRules_IstatCodeDigits";

    public void Configure(EntityTypeBuilder<TouristTaxChannelRule> entity)
    {
        entity.HasIndex(r => new { r.Channel, r.IstatCode })
            .IsUnique()
            .HasDatabaseName(TouristTaxChannelRule.ChannelComuneIndexName);
        entity.ToTable(t =>
        {
            t.HasCheckConstraint(SourceRequiredCheckName, "btrim(\"SourceUrl\") <> ''");
            t.HasCheckConstraint(IstatCodeCheckName, "\"IstatCode\" ~ '^[0-9]{6}$'");
        });
    }
}
