using Casazen.Core.Entities;
using Casazen.Infrastructure.Data.Seeds;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Casazen.Infrastructure.Data.Configurations;

/// <summary>
/// The ISTAT region table (E21/F01/US01/T01, decision O6): platform reference data, one row for each region and one for each autonomous province of
/// Trentino-Alto Adige, seeded (<see cref="IstatRegionRuleSeed"/>) with every row «da verificare». The database refuses a row with no source, an address
/// that is not https, a deadline day on a deadline that is not monthly (or a monthly one without it), a statistical field outside the three lists and a
/// code that is not a region or a province code, whoever writes it.
/// </summary>
internal sealed class IstatRegionRuleConfiguration : IEntityTypeConfiguration<IstatRegionRule>
{
    /// <summary>Name of the check that the region is an ISTAT code of two digits.</summary>
    public const string RegionCodeCheckName = "CK_IstatRegionRules_RegionIstatCode";

    /// <summary>Name of the check that a province, when there is one, is a plate code.</summary>
    public const string ProvinceCodeCheckName = "CK_IstatRegionRules_ProvinceCode";

    /// <summary>Name of the check that a row always has a source, and that it is https.</summary>
    public const string SourceCheckName = "CK_IstatRegionRules_SourceUrlHttps";

    /// <summary>Name of the check that a portal, when there is one, is https.</summary>
    public const string PortalCheckName = "CK_IstatRegionRules_PortalUrlHttps";

    /// <summary>Name of the check that only a monthly deadline has a day, from 1 to 31.</summary>
    public const string DeadlineCheckName = "CK_IstatRegionRules_Deadline";

    /// <summary>Name of the check that the required fields are a list of the three statistical fields.</summary>
    public const string RequiredFieldsCheckName = "CK_IstatRegionRules_RequiredGuestFields";

    /// <summary>Name of the check that the deadline kind and the coverage are values of their enums.</summary>
    public const string EnumsCheckName = "CK_IstatRegionRules_Enums";

    public void Configure(EntityTypeBuilder<IstatRegionRule> entity)
    {
        // One row for a region (no province) and one for each province of a region: two partial unique indexes, as a single one would let a second
        // row of a region in (the database takes two NULLs for different values).
        entity.HasIndex(r => r.RegionIstatCode)
            .IsUnique()
            .HasFilter("\"ProvinceCode\" IS NULL")
            .HasDatabaseName(IstatRegionRule.RegionIndexName);
        entity.HasIndex(r => new { r.RegionIstatCode, r.ProvinceCode })
            .IsUnique()
            .HasFilter("\"ProvinceCode\" IS NOT NULL")
            .HasDatabaseName(IstatRegionRule.RegionProvinceIndexName);
        entity.ToTable(t =>
        {
            t.HasCheckConstraint(RegionCodeCheckName, "\"RegionIstatCode\" ~ '^[0-9]{2}$'");
            t.HasCheckConstraint(ProvinceCodeCheckName, "\"ProvinceCode\" IS NULL OR \"ProvinceCode\" ~ '^[A-Z]{2}$'");
            t.HasCheckConstraint(SourceCheckName, "\"SourceUrl\" LIKE 'https://%'");
            t.HasCheckConstraint(PortalCheckName, "\"PortalUrl\" IS NULL OR \"PortalUrl\" LIKE 'https://%'");
            t.HasCheckConstraint(
                DeadlineCheckName,
                "(\"DeadlineKind\" = 2 AND \"DeadlineDay\" BETWEEN 1 AND 31) OR (\"DeadlineKind\" <> 2 AND \"DeadlineDay\" IS NULL)");
            t.HasCheckConstraint(
                RequiredFieldsCheckName,
                "\"RequiredGuestFields\" IS NULL OR \"RequiredGuestFields\" ~ '^((TourismType|TravelWay|EducationLevel)(,(TourismType|TravelWay|EducationLevel))*)?$'");
            t.HasCheckConstraint(EnumsCheckName, "\"DeadlineKind\" IN (0, 1, 2) AND \"OctorateCoverage\" IN (0, 1, 2)");
        });
        entity.HasData(IstatRegionRuleSeed.Build());
    }
}
