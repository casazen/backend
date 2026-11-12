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

    private static CondominiumHistoricalImportService Service(AppDbContext db) =>
        new(db, new CondominiumAccountingService(db, new MigraDocPdfDocumentRenderer(), NullLogger<CondominiumAccountingService>.Instance), NullLogger<CondominiumHistoricalImportService>.Instance);

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
}
