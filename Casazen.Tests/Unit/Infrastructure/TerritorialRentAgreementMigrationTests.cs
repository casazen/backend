using Casazen.Core.Entities.Enums;
using Casazen.Infrastructure.Data.Seeds;
using Xunit;

namespace Casazen.Tests.Unit.Infrastructure;

/// <summary>
/// The MB reference data of the territorial rent agreements (<see cref="CanoneConcordatoMbSeed"/>), which the database holds
/// since the migrations that seeded them. The tests that re-read the script of those migrations went away once they reached
/// production; the data in the migrated database is checked in <c>LeaseContractTypeAndConcordatoRulesMigrationPostgresTests</c>.
/// </summary>
public class TerritorialRentAgreementMigrationTests
{
    [Fact]
    public void MbSeed_MissingComuni_HaveNoBands()
    {
        var missing = CanoneConcordatoMbSeed.BuildAgreements()
            .Where(a => a.DataCompleteness == DataCompleteness.Missing)
            .ToList();

        Assert.Equal(53, missing.Count);
        Assert.All(missing, a => Assert.Empty(a.Bands));
        Assert.Equal(55, CanoneConcordatoMbSeed.ProvinceComuni.Length);
        Assert.All(
            CanoneConcordatoMbSeed.BuildAgreements().Where(a => CanoneConcordatoMbSeed.PilotComuni.Contains(a.Comune)),
            a => Assert.NotEmpty(a.Bands));
    }
}
