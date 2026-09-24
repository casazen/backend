using Casazen.Core.Entities.Enums;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Data.Seeds;
using Casazen.Tests.Integration.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// LT-10 on a real PostgreSQL database: the <c>LeaseContractTypeAndConcordatoRules</c> migration separates the contract
/// type from the tax regime of the existing leases (A7-13) without guessing, and leaves the database with exactly the
/// current MB reference data (<see cref="CanoneConcordatoMbSeed"/>): 55 comuni with Misinto, contiguous bands, the 11
/// signatories, the calculation rules (A7-10, A7-11), while the 2024 migration stays frozen (A7-22).
/// </summary>
public class LeaseContractTypeAndConcordatoRulesMigrationPostgresTests : IAsyncLifetime
{
    private PostgresTestDatabase? _database;

    public async Task InitializeAsync() => _database = await PostgresTestDatabase.CreateAsync();

    public async Task DisposeAsync()
    {
        if (_database is not null)
            await _database.DisposeAsync();
    }

    [PostgresFact]
    public async Task Migrate_ExistingLeases_SetsContractTypeAndTaxRegimeFromTheLegacyValue()
    {
        await using var db = _database!.CreateContext();
        db.GetService<IMigrator>().Migrate(PreviousMigration(db));
        var org = Guid.NewGuid();
        var property = Guid.NewGuid();
        var (cedolare, ordinario, concordato) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO "Orgs" ("Id", "Name", "Slug", "PlanTier", "DisplayName", "ContactEmail", "IsActive", "CreatedAt", "UpdatedAt")
            VALUES ({org}, 'Org LT-10', {"lt10-" + org.ToString("N")}, 0, 'Org LT-10', '', true, now(), now());

            INSERT INTO "Properties" (
                "Id", "OwnerId", "OrgId", "Name", "Description", "Address", "City", "PostalCode",
                "Latitude", "Longitude", "Bedrooms", "Bathrooms", "MaxGuests", "NightlyRate", "CleaningFee", "DamageDeposit",
                "Amenities", "PhotoUrls", "HouseRules", "Timezone", "IsActive", "CreatedAt", "UpdatedAt")
            VALUES ({property}, 'auth0|lt10', {org}, 'Casa', 'Casa LT-10', 'Via Roma 1', 'Seveso', '20822', 0, 0, 1, 1, 2, 100, 0, 0,
                ARRAY[]::integer[], ARRAY[]::text[], '', 'Europe/Rome', true, now(), now());

            INSERT INTO "LeaseContracts" (
                "Id", "PropertyId", "OrgId", "Status", "FiscalRegime", "StartDate", "EndDate", "MonthlyRent",
                "ErasureRequested", "DataRetentionUntil", "CreatedAt", "UpdatedAt")
            VALUES
              ({cedolare},   {property}, {org}, 0, 0, '2026-09-01', '2030-08-31', 800, false, '2036-09-01', now(), now()),
              ({ordinario},  {property}, {org}, 0, 1, '2026-09-01', '2030-08-31', 800, false, '2036-09-01', now(), now()),
              ({concordato}, {property}, {org}, 0, 2, '2026-09-01', '2029-08-31', 400, false, '2036-09-01', now(), now());
            """);

        await db.Database.MigrateAsync();

        var leases = await db.LeaseContracts.IgnoreQueryFilters().AsNoTracking()
            .Where(l => l.PropertyId == property)
            .ToDictionaryAsync(l => l.Id);
        Assert.Equal((LeaseContractType.Libero, (LeaseTaxRegime?)LeaseTaxRegime.CedolareSecca),
            (leases[cedolare].ContractType, leases[cedolare].TaxRegime));
        Assert.Equal((LeaseContractType.Libero, (LeaseTaxRegime?)LeaseTaxRegime.Ordinario),
            (leases[ordinario].ContractType, leases[ordinario].TaxRegime));
        // The old value did not say the tax regime of a concordato lease: unknown, never guessed.
        Assert.Equal((LeaseContractType.Concordato, (LeaseTaxRegime?)null),
            (leases[concordato].ContractType, leases[concordato].TaxRegime));
        Assert.All(leases.Values, l => Assert.Null(l.ConcordatoAssessment));
        Assert.Equal(FiscalRegime.CanoneConcordato, leases[concordato].FiscalRegime);
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
    }

    [PostgresFact]
    public async Task Migrate_FreshDatabase_HoldsExactlyTheCurrentMbReferenceData()
    {
        await using var db = _database!.CreateContext();
        await db.Database.MigrateAsync();

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

    private static string PreviousMigration(AppDbContext db)
    {
        var all = db.Database.GetMigrations().ToList();
        var index = all.FindIndex(m => m.EndsWith("_LeaseContractTypeAndConcordatoRules", StringComparison.Ordinal));
        Assert.True(index > 0);
        return all[index - 1];
    }
}
