using System.Text.Json;
using Casazen.Core.Authorization;
using Casazen.Core.Condominium;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Documents;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Casazen.Tests.Unit.Condominium;

public class CondominiumHistoricalImportServiceTests
{
    [Fact]
    public async Task UnverifiedHouse_StaysStaged_AndDoesNotChangeBalances()
    {
        await using var db = NewDb();
        var (scope, _) = await SeedAsync(db);
        var staged = await Service(db).StageAsync(scope, Snapshot(), "user");

        var confirmed = await Service(db).ConfirmAsync(scope, staged.Id, "user");

        Assert.False(confirmed.CasaZenIsSource);
        Assert.All(confirmed.Houses, house => Assert.False(house.AffectsBalances));
        Assert.Equal(0, await db.Condominiums.CountAsync());
        Assert.Equal(0, await db.CondominiumPayments.CountAsync());
    }

    [Fact]
    public async Task VerifiedHouse_MatchesCasaAToTheCent_AndASecondCopyDoesNotRewriteIt()
    {
        await using var db = NewDb();
        var (scope, property) = await SeedAsync(db);
        var imports = Service(db);
        var json = Snapshot();
        var staged = await imports.StageAsync(scope, json, "user");
        var again = await imports.StageAsync(scope, json, "user");
        Assert.True(again.AlreadyImported);
        Assert.Equal(staged.Id, again.Id);

        var house = staged.Houses.Single(item => item.Name == "Casa A");
        await imports.VerifyPropertyAsync(scope, staged.Id, house.Id, property.Id, "user");
        var confirmed = await imports.ConfirmAsync(scope, staged.Id, "user");

        var posted = confirmed.Houses.Single(item => item.Name == "Casa A");
        Assert.True(posted.AffectsBalances);
        Assert.Equal(CondominiumHistoricalImportService.ReadOnlyEnvironment, confirmed.CasaZenIsSource ? confirmed.CondolegerReadOnly : null);
        var year = posted.Years.Single();
        Assert.Equal(490.30m, year.Ordinary);
        Assert.Equal(0m, year.Extraordinary);
        Assert.Equal(490.30m, year.FinalAmount);
        Assert.Equal(0m, year.Prior);
        Assert.Equal(412.80m, year.Payments);
        Assert.Equal(77.50m, year.Settlement);
        Assert.Equal(12, year.InstallmentCount);
        Assert.Equal(490.30m, year.InstallmentTotal);

        var left = confirmed.Houses.Single(item => item.Name == "Casa B");
        Assert.False(left.AffectsBalances);
        Assert.Equal(1, await db.Condominiums.CountAsync());

        var repeat = await imports.ConfirmAsync(scope, staged.Id, "user");
        Assert.True(repeat.Houses.Single(item => item.Name == "Casa A").Confirmed);
        Assert.Equal(1, await db.CondominiumPayments.CountAsync(payment => payment.ExternalSourceHash == "payment:casa-a"));
    }

    [Fact]
    public async Task AnotherOrg_CannotConfirmTheSnapshot()
    {
        await using var db = NewDb();
        var (scope, property) = await SeedAsync(db);
        var imports = Service(db);
        var staged = await imports.StageAsync(scope, Snapshot(), "user");
        await imports.VerifyPropertyAsync(scope, staged.Id, staged.Houses.First().Id, property.Id, "user");

        await Assert.ThrowsAsync<NotFoundException>(() =>
            imports.ConfirmAsync(new HostScope(Guid.NewGuid()), staged.Id, "user"));
        Assert.Equal(0, await db.Condominiums.CountAsync());
    }

    [Fact]
    public async Task TwoYears_SecondHasNoPrior_MatchesWithoutTheSeededBalance()
    {
        await using var db = NewDb();
        var (scope, property) = await SeedAsync(db);
        var imports = Service(db);
        var staged = await imports.StageAsync(scope, YearSnapshot(
            Year("2023/2024", new DateOnly(2023, 6, 1), new DateOnly(2024, 5, 31), 100_000, 100_000, 0, 80_000, "pay:year-1"),
            Year("2024/2025", new DateOnly(2024, 6, 1), new DateOnly(2025, 5, 31), 49_030, 49_030, 0, 41_280, "pay:year-2")), "user");
        await imports.VerifyPropertyAsync(scope, staged.Id, staged.Houses.Single().Id, property.Id, "user");

        var confirmed = await imports.ConfirmAsync(scope, staged.Id, "user");

        Assert.True(confirmed.Houses.Single().AffectsBalances);
        Assert.Equal(1, await db.Condominiums.CountAsync());
        var second = await db.CondominiumPeriods.SingleAsync(period => period.Label == "2024/2025");
        Assert.False(await db.CondominiumPriorBalances.AnyAsync(balance => balance.CondominiumPeriodId == second.Id));
        Assert.Equal(49_030, CondoExportV1.ToCents(await InstallmentTotalAsync(db, second.Id)));
        Assert.Equal(12, await db.CondominiumInstallments.CountAsync(row => row.CondominiumPeriodId == second.Id));
    }

    [Fact]
    public async Task TwoYears_PriorOnTheSecond_StaysOffTheRates()
    {
        await using var db = NewDb();
        var (scope, property) = await SeedAsync(db);
        var imports = Service(db);
        var staged = await imports.StageAsync(scope, YearSnapshot(
            Year("2023/2024", new DateOnly(2023, 6, 1), new DateOnly(2024, 5, 31), 100_000, 100_000, 0, 80_000, "pay:year-1"),
            Year("2024/2025", new DateOnly(2024, 6, 1), new DateOnly(2025, 5, 31), 49_030, 49_030, 20_000, 41_280, "pay:year-2")), "user");
        await imports.VerifyPropertyAsync(scope, staged.Id, staged.Houses.Single().Id, property.Id, "user");

        await imports.ConfirmAsync(scope, staged.Id, "user");

        var second = await db.CondominiumPeriods.SingleAsync(period => period.Label == "2024/2025");
        var prior = await db.CondominiumPriorBalances.SingleAsync(balance => balance.CondominiumPeriodId == second.Id);
        Assert.Equal(200m, prior.Amount);
        Assert.Equal(PriorYearBalanceDisposition.CarryForward, prior.Disposition);
        Assert.Equal(49_030, CondoExportV1.ToCents(await InstallmentTotalAsync(db, second.Id)));
    }

    [Fact]
    public async Task LaterSnapshot_ReusesTheBook_AndAConfirmedYearStaysStaged()
    {
        await using var db = NewDb();
        var (scope, property) = await SeedAsync(db);
        var imports = Service(db);
        var first = await imports.StageAsync(scope, Snapshot(), "user");
        var casa = first.Houses.Single(item => item.Name == "Casa A");
        await imports.VerifyPropertyAsync(scope, first.Id, casa.Id, property.Id, "user");
        await imports.ConfirmAsync(scope, first.Id, "user");
        var condominiumId = await db.Condominiums.Select(item => item.Id).SingleAsync();

        var next = await imports.StageAsync(scope, YearSnapshot(
            Year("2025/2026", new DateOnly(2025, 6, 1), new DateOnly(2026, 5, 31), 120_000, 120_000, 0, 100_000, "pay:year-next")), "user");
        await imports.VerifyPropertyAsync(scope, next.Id, next.Houses.Single().Id, property.Id, "user");
        var added = await imports.ConfirmAsync(scope, next.Id, "user");

        Assert.True(added.Houses.Single().AffectsBalances);
        Assert.Equal(condominiumId, await db.Condominiums.Select(item => item.Id).SingleAsync());
        var period = await db.CondominiumPeriods.SingleAsync(item => item.Label == "2025/2026");
        Assert.False(await db.CondominiumPriorBalances.AnyAsync(balance => balance.CondominiumPeriodId == period.Id));
        Assert.Equal(120_000, CondoExportV1.ToCents(await InstallmentTotalAsync(db, period.Id)));

        var again = await imports.StageAsync(scope, YearSnapshot(
            Year("2024/2025", new DateOnly(2024, 6, 1), new DateOnly(2025, 5, 31), 49_030, 49_030, 0, 41_280, "pay:repeat")), "user");
        await imports.VerifyPropertyAsync(scope, again.Id, again.Houses.Single().Id, property.Id, "user");
        var repeat = await imports.ConfirmAsync(scope, again.Id, "user");

        Assert.False(repeat.Houses.Single().Confirmed);
        Assert.Equal(1, await db.CondominiumPayments.CountAsync(payment => payment.ExternalSourceHash == "payment:casa-a"));
        Assert.Equal(condominiumId, await db.Condominiums.Select(item => item.Id).SingleAsync());
    }

    [Fact]
    public async Task PropertyAlreadyLinked_ConfirmPostsIntoThatBook()
    {
        await using var db = NewDb();
        var (scope, property) = await SeedAsync(db);
        var accounting = Accounting(db);
        var existing = await accounting.CreateCondominiumAsync(
            scope, new CreateCondominiumCommand("Casa", null, null, null, 6), "user");
        await accounting.LinkPropertyAsync(
            scope, existing.Id, new LinkPropertyCommand(property.Id, null, null, null, null), "user");
        var imports = Service(db);
        var staged = await imports.StageAsync(scope, Snapshot(), "user");
        var casa = staged.Houses.Single(item => item.Name == "Casa A");
        await imports.VerifyPropertyAsync(scope, staged.Id, casa.Id, property.Id, "user");

        var confirmed = await imports.ConfirmAsync(scope, staged.Id, "user");

        Assert.True(confirmed.Houses.Single(item => item.Name == "Casa A").AffectsBalances);
        Assert.Equal(existing.Id, await db.Condominiums.Select(item => item.Id).SingleAsync());
        Assert.Equal(1, await db.CondominiumPayments.CountAsync(payment => payment.ExternalSourceHash == "payment:casa-a"));
    }

    private static CondominiumHistoricalImportService Service(AppDbContext db) =>
        new(db, Accounting(db), NullLogger<CondominiumHistoricalImportService>.Instance);

    private static CondominiumAccountingService Accounting(AppDbContext db) =>
        new(db, new MigraDocPdfDocumentRenderer(), NullLogger<CondominiumAccountingService>.Instance);

    private static AppDbContext NewDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static async Task<(HostScope Scope, Property Property)> SeedAsync(AppDbContext db)
    {
        var org = new Casazen.Core.Entities.Org { Name = "Demo", Slug = "demo-" + Guid.NewGuid().ToString("N")[..8] };
        var property = new Property { OrgId = org.Id, OwnerId = "user", Name = "Casa", Address = "Via Test 1", City = "Milano" };
        db.Add(org);
        db.Add(property);
        await db.SaveChangesAsync();
        return (new HostScope(org.Id), property);
    }

    private static string Snapshot()
    {
        var rows = OwnerInstallmentPlan.Generate(new DateOnly(2024, 6, 1), CondominiumInstallmentCadence.Monthly, 490.30m, 0m, 0m);
        var installments = rows.Select(row => new { fiscalPeriodId = "fiscal-period:1", totalCents = CondoExportV1.ToCents(row.Total) });
        var document = new
        {
            format = CondoExportV1.Format,
            currency = "EUR",
            amountUnit = "cents",
            houses = new object[]
            {
                House("house:1", "Casa A", "fiscal-period:1", 49030, 0, 49030, 0, [("payment:casa-a", 41280)], installments),
                House("house:2", "Casa B", "fiscal-period:2", 240000, 60000, 240000, 0, [("payment:casa-b", 240000)],
                    OwnerInstallmentPlan.Generate(new DateOnly(2024, 6, 1), CondominiumInstallmentCadence.Monthly, 2400m, 600m, 0m)
                        .Select(row => new { fiscalPeriodId = "fiscal-period:2", totalCents = CondoExportV1.ToCents(row.Total) })),
            },
        };
        return JsonSerializer.Serialize(document);
    }

    private static object House(
        string id,
        string name,
        string periodId,
        long ordinary,
        long extraordinary,
        long finalAmount,
        long prior,
        (string Id, long Amount)[] payments,
        IEnumerable<object> installments) => new
        {
            id,
            name,
            location = "TEST",
            fiscalStartMonth = 6,
            fiscalPeriods = new[] { new { id = periodId, label = "2024/2025", startDate = "2024-06-01", endDate = "2025-05-31" } },
            dues = new object[]
        {
            new { id = id + ":ordinary", fiscalPeriodId = periodId, kind = "budget", voice = "ordinary", amountCents = ordinary },
            new { id = id + ":extra", fiscalPeriodId = periodId, kind = "budget", voice = "extraordinary", amountCents = extraordinary },
            new { id = id + ":final", fiscalPeriodId = periodId, kind = "final", voice = (string?)null, amountCents = finalAmount },
        },
            priorBalances = new[] { new { id = id + ":prior", fiscalPeriodId = periodId, amountCents = prior } },
            payments = payments.Select(payment => new { id = payment.Id, fiscalPeriodId = periodId, amountCents = payment.Amount, paidOn = "2025-05-01" }),
            installments,
        };

    private sealed record YearDraft(
        string Label, DateOnly Start, DateOnly End, long Ordinary, long FinalAmount, long Prior, long Paid, string PaymentId);

    private static YearDraft Year(
        string label, DateOnly start, DateOnly end, long ordinary, long finalAmount, long prior, long paid, string paymentId) =>
        new(label, start, end, ordinary, finalAmount, prior, paid, paymentId);

    private static string YearSnapshot(params YearDraft[] years)
    {
        var document = new
        {
            format = CondoExportV1.Format,
            currency = "EUR",
            amountUnit = "cents",
            houses = new object[]
            {
                new
                {
                    id = "house:years",
                    name = "Casa A",
                    location = "TEST",
                    fiscalStartMonth = 6,
                    fiscalPeriods = years.Select(year => new
                    {
                        id = year.Label,
                        label = year.Label,
                        startDate = year.Start.ToString("yyyy-MM-dd"),
                        endDate = year.End.ToString("yyyy-MM-dd"),
                    }),
                    dues = years.SelectMany(year => new object[]
                    {
                        new { id = year.Label + ":ordinary", fiscalPeriodId = year.Label, kind = "budget", voice = "ordinary", amountCents = year.Ordinary },
                        new { id = year.Label + ":final", fiscalPeriodId = year.Label, kind = "final", voice = (string?)null, amountCents = year.FinalAmount },
                    }),
                    priorBalances = years.Where(year => year.Prior != 0).Select(year => new
                    {
                        id = year.Label + ":prior",
                        fiscalPeriodId = year.Label,
                        amountCents = year.Prior,
                    }),
                    payments = years.Select(year => new
                    {
                        id = year.PaymentId,
                        fiscalPeriodId = year.Label,
                        amountCents = year.Paid,
                        paidOn = year.End.ToString("yyyy-MM-dd"),
                    }),
                    installments = years.SelectMany(year => OwnerInstallmentPlan.Generate(
                            year.Start, CondominiumInstallmentCadence.Monthly, CondoExportV1.FromCents(year.Ordinary), 0m, 0m)
                        .Select(row => new { fiscalPeriodId = year.Label, totalCents = CondoExportV1.ToCents(row.Total) })),
                },
            },
        };
        return JsonSerializer.Serialize(document);
    }

    private static async Task<decimal> InstallmentTotalAsync(AppDbContext db, Guid periodId) =>
        await db.CondominiumInstallments
            .Where(row => row.CondominiumPeriodId == periodId && row.Status != CondominiumInstallmentStatus.Cancelled)
            .SumAsync(row => row.TotalAmount);
}
