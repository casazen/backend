using Casazen.Core.Entities.Enums;
using Casazen.Infrastructure.Data.Seeds;
using Casazen.Tests.Integration.Postgres;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// LT-10 on a real PostgreSQL database: the migrated database holds exactly the current MB reference data
/// (<see cref="CanoneConcordatoMbSeed"/>): 55 comuni with Misinto, contiguous bands, the 11 signatories, the calculation
/// rules (A7-10, A7-11). The test of the backfill of the existing leases (contract type and tax regime, A7-13) went away once
/// the <c>LeaseContractTypeAndConcordatoRules</c> migration reached production.
/// </summary>
public class LeaseContractTypeAndConcordatoRulesMigrationPostgresTests : IAsyncLifetime
{
    private PostgresTestDatabase? _database;

    public async Task InitializeAsync() => _database = await PostgresTestDatabase.CreateMigratedAsync();

    public async Task DisposeAsync()
    {
        if (_database is not null)
            await _database.DisposeAsync();
    }

    [PostgresFact]
    public async Task Migrate_FreshDatabase_HoldsExactlyTheCurrentMbReferenceData()
    {
        await using var db = _database!.CreateContext();

        var stored = await db.TerritorialRentAgreements.AsNoTracking()
            .Include(a => a.Bands)
            .Include(a => a.Signatories)
            .Where(a => a.AgreementName == CanoneConcordatoMbSeed.AgreementName)
            .ToDictionaryAsync(a => a.Id);
        var expected = CanoneConcordatoMbSeed.BuildAgreements();

        Assert.Equal(55, stored.Count);
        Assert.Contains(stored.Values, a => a.Comune == "Misinto" && a.DataCompleteness == DataCompleteness.Missing);
        foreach (var seed in expected)
        {
            var row = stored[seed.Id];
            Assert.Equal(
                (seed.Comune, seed.DataCompleteness, seed.LastVerifiedAt, seed.SourceUrl, seed.RequiredTypeACount),
                (row.Comune, row.DataCompleteness, row.LastVerifiedAt, row.SourceUrl, row.RequiredTypeACount));
            Assert.Equal(
                (seed.SubFascia2MinTypeBCount, seed.SubFascia3MinTypeCCount, seed.SubFascia3MinQualifyingTypeDCount,
                    seed.SubFascia3QualifyingTypeDElements, seed.SubFascia3MaxMinTypeDCount, seed.StoveHeatingMinTypeBCount,
                    seed.CoefficientCombination),
                (row.SubFascia2MinTypeBCount, row.SubFascia3MinTypeCCount, row.SubFascia3MinQualifyingTypeDCount,
                    row.SubFascia3QualifyingTypeDElements, row.SubFascia3MaxMinTypeDCount, row.StoveHeatingMinTypeBCount,
                    row.CoefficientCombination));
            Assert.Equal(
                new[]
                {
                    seed.FurnishedUpliftPercent, seed.AirConditioningUpliftPercent, seed.SmallSqmUpliftPercent,
                    seed.MidSqmUpliftPercent, seed.LargeSqmReductionPercent, seed.GarageAppurtenancePercent,
                    seed.BalconyAppurtenancePercent, seed.OtherAppurtenancePercent, seed.GreenAreaAppurtenancePercent,
                    seed.Duration4UpliftPercent, seed.Duration5UpliftPercent, seed.Duration6UpliftPercent,
                },
                new[]
                {
                    row.FurnishedUpliftPercent, row.AirConditioningUpliftPercent, row.SmallSqmUpliftPercent,
                    row.MidSqmUpliftPercent, row.LargeSqmReductionPercent, row.GarageAppurtenancePercent,
                    row.BalconyAppurtenancePercent, row.OtherAppurtenancePercent, row.GreenAreaAppurtenancePercent,
                    row.Duration4UpliftPercent, row.Duration5UpliftPercent, row.Duration6UpliftPercent,
                });
            Assert.Equal(
                (seed.SmallSqmMax, seed.MidSqmMin, seed.MidSqmMax, seed.LargeSqmMin),
                (row.SmallSqmMax, row.MidSqmMin, row.MidSqmMax, row.LargeSqmMin));
            Assert.Equal(
                seed.Bands.Select(BandKey).Order(),
                row.Bands.Select(BandKey).Order());
            Assert.Equal(
                seed.Signatories.Select(s => $"{s.Id}|{s.Name}|{s.Role}|{s.Contact}").Order(),
                row.Signatories.Select(s => $"{s.Id}|{s.Name}|{s.Role}|{s.Contact}").Order());
        }
    }

    private static string BandKey(Core.Entities.ConcordatoRentBand b) =>
        $"{b.Id}|{b.ZoneName}|{b.CadastralSheets}|{b.MinSqm}|{b.MaxSqm}|{b.SubFascia1MinEurSqmYear:0.00}|{b.SubFascia1MaxEurSqmYear:0.00}" +
        $"|{b.SubFascia2MinEurSqmYear:0.00}|{b.SubFascia2MaxEurSqmYear:0.00}|{b.SubFascia3MinEurSqmYear:0.00}|{b.SubFascia3MaxEurSqmYear:0.00}";
}
