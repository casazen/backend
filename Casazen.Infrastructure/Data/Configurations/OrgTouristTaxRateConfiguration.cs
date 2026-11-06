using Casazen.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Casazen.Infrastructure.Data.Configurations;

/// <summary>
/// The rate a host declares for a comune with no official one (AC-NF46-6): tenant-owned (the global tenant filter applies, every read also
/// names the org), found by comune code or by name within the org. The database refuses an amount that is not above zero, a cap under one
/// night, an exemption age outside 0 to 18 and a season with one bound only, whoever writes the row.
/// </summary>
internal sealed class OrgTouristTaxRateConfiguration : IEntityTypeConfiguration<OrgTouristTaxRate>
{
    public void Configure(EntityTypeBuilder<OrgTouristTaxRate> entity)
    {
        entity.HasOne<Org>().WithMany().HasForeignKey(r => r.OrgId).OnDelete(DeleteBehavior.Restrict);
        entity.HasIndex(r => new { r.OrgId, r.IstatCode });
        entity.HasIndex(r => new { r.OrgId, r.City });
        entity.ToTable(t =>
        {
            t.HasCheckConstraint("CK_OrgTouristTaxRates_RatePositive", "\"RatePerPersonPerNight\" > 0");
            t.HasCheckConstraint("CK_OrgTouristTaxRates_MaxNights", "\"MaxNights\" IS NULL OR \"MaxNights\" >= 1");
            t.HasCheckConstraint("CK_OrgTouristTaxRates_MinimumAge", "\"MinimumAge\" BETWEEN 0 AND 18");
            t.HasCheckConstraint(
                "CK_OrgTouristTaxRates_SeasonPair",
                "(\"SeasonStart\" IS NULL) = (\"SeasonEnd\" IS NULL)");
            t.HasCheckConstraint("CK_OrgTouristTaxRates_SourceNoteRequired", "btrim(\"SourceNote\") <> ''");
        });
    }
}
