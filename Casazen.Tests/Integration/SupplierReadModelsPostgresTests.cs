using System.Net.Http.Json;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Infrastructure.Data;
using Casazen.Tests.Integration.Postgres;
using Casazen.Tests.Unit;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// SP-04e on PostgreSQL (CI): what the in-memory provider cannot prove. The overview, the counters, the text search and the payments
/// are computed by the database on real rows (<c>count(*) FILTER</c>, <c>timestamptz</c> comparisons at the edges of the Rome days
/// around the end of summer time, <c>sum</c> of the money, <c>LIKE</c> with the wildcards of the supplier's text escaped), and each
/// supplier only reads its own. Every expected number is computed in the test, from the rows it wrote, with its own arithmetic on the
/// Rome calendar: nothing is copied from the code under test.
/// </summary>
[Collection(SupplierCatalogHostsCollection.Name)]
public class SupplierReadModelsPostgresTests : IClassFixture<CasazenWebApplicationFactory>
{
    private static readonly TimeZoneInfo Rome = TimeZoneInfo.FindSystemTimeZoneById("Europe/Rome");

    /// <summary>Wednesday 28 October 2026, 12:00 in Rome: winter time since the 25th, summer time at the start of the period before.</summary>
    private static readonly DateTimeOffset Today = new(2026, 10, 28, 11, 0, 0, TimeSpan.Zero);

    private readonly CasazenWebApplicationFactory _factory;

    public SupplierReadModelsPostgresTests(CasazenWebApplicationFactory factory) => _factory = factory;

    // ─── The overview, counted by the database ───

    [PostgresFact]
    public async Task Overview_CountsOnRealRows_LikeAnIndependentComputationOnTheCalendarOfRome()
    {
        var world = await ServiceRequestWorlds.SeedAsync(_factory);
        var rows = Rows();
        await AddAsync(world, rows);
        await using var app = WithClock(Today);
        using var client = ClientOf(app, world.SupplierUserId);

        var body = await client.GetFromJsonAsync<JsonElement>("/api/supplier/dashboard/overview?period=current-month");

        var currentDays = (From: new DateOnly(2026, 10, 1), To: new DateOnly(2026, 10, 28));
        var previousDays = (From: new DateOnly(2026, 9, 3), To: new DateOnly(2026, 9, 30)); // 28 days, adjacent
        AssertPeriod(body.GetProperty("current"), rows, currentDays);
        AssertPeriod(body.GetProperty("previous"), rows, previousDays);
        // The test has real edges in it: at least one row falls on each side of both periods.
        Assert.True(rows.Count(r => InDays(r.Created, currentDays)) > 0 && rows.Count(r => InDays(r.Created, previousDays)) > 0);
    }

    [PostgresTheory]
    [InlineData("last-30-days")]
    [InlineData("last-3-months")]
    public async Task Overview_TheOtherPeriods_AreCountedOnTheSameRows(string period)
    {
        var world = await ServiceRequestWorlds.SeedAsync(_factory);
        var rows = Rows();
        await AddAsync(world, rows);
        await using var app = WithClock(Today);
        using var client = ClientOf(app, world.SupplierUserId);

        var body = await client.GetFromJsonAsync<JsonElement>($"/api/supplier/dashboard/overview?period={period}");

        var today = new DateOnly(2026, 10, 28);
        var currentFrom = period == "last-30-days" ? today.AddDays(-29) : today.AddMonths(-3).AddDays(1);
        var length = today.DayNumber - currentFrom.DayNumber + 1;
        var previousTo = currentFrom.AddDays(-1);
        AssertPeriod(body.GetProperty("current"), rows, (currentFrom, today));
        AssertPeriod(body.GetProperty("previous"), rows, (previousTo.AddDays(1 - length), previousTo));
    }

    [PostgresFact]
    public async Task Overview_TheMoney_IsSummedByTheDatabaseFromThePaymentsPaidInEachPeriod_OnlyWhileTheFlagIsOn()
    {
        var world = await ServiceRequestWorlds.SeedAsync(_factory);
        var rows = new List<(DateTime PaidAt, int Amount, decimal Percent, ServicePaymentStatus Status)>
        {
            (Utc("2026-10-02T09:00:00Z"), 6000, 10m, ServicePaymentStatus.Paid),
            (Utc("2026-10-27T10:00:00Z"), 12345, 10m, ServicePaymentStatus.Paid), // 1234.5 -> 1235 away from zero
            (Utc("2026-09-30T21:59:59Z"), 7000, 3.5m, ServicePaymentStatus.Paid), // 23:59:59 on 30 September in Rome: the period before
            (Utc("2026-09-30T22:00:00Z"), 8000, 0m, ServicePaymentStatus.Paid), // 00:00 on 1 October: the current one
            (Utc("2026-10-28T22:59:59Z"), 5000, 10m, ServicePaymentStatus.Paid), // the last second of today (winter time)
            (Utc("2026-10-28T23:00:00Z"), 9999, 10m, ServicePaymentStatus.Paid), // tomorrow: in neither
            (Utc("2026-10-10T09:00:00Z"), 4000, 10m, ServicePaymentStatus.Refunded), // not counted
            (Utc("2026-10-11T09:00:00Z"), 4000, 10m, ServicePaymentStatus.Requested), // not paid
        };
        await AddPaymentsAsync(world, rows);
        await using var on = WithClock(Today, paymentsFlag: true);
        await using var off = WithClock(Today, paymentsFlag: false);
        using var supplierOn = ClientOf(on, world.SupplierUserId);
        using var supplierOff = ClientOf(off, world.SupplierUserId);

        var withFlag = await supplierOn.GetFromJsonAsync<JsonElement>("/api/supplier/dashboard/overview");
        var withoutFlag = await supplierOff.GetFromJsonAsync<JsonElement>("/api/supplier/dashboard/overview");

        static int Fee(int amount, decimal percent) => (int)decimal.Round(amount * percent / 100m, 0, MidpointRounding.AwayFromZero);
        var paid = rows.Where(r => r.Status == ServicePaymentStatus.Paid).ToList();
        var current = paid.Where(r => r.PaidAt >= Utc("2026-09-30T22:00:00Z") && r.PaidAt < Utc("2026-10-28T23:00:00Z")).ToList();
        var previous = paid.Where(r => r.PaidAt >= Utc("2026-09-02T22:00:00Z") && r.PaidAt < Utc("2026-09-30T22:00:00Z")).ToList();
        Assert.True(withFlag.GetProperty("onlinePayments").GetBoolean());
        AssertMoney(withFlag.GetProperty("current"), current.Sum(r => (long)r.Amount), current.Sum(r => (long)Fee(r.Amount, r.Percent)));
        AssertMoney(withFlag.GetProperty("previous"), previous.Sum(r => (long)r.Amount), previous.Sum(r => (long)Fee(r.Amount, r.Percent)));
        Assert.Equal(4, current.Count);
        Assert.Single(previous);
        // The flag off: the same rows, and no money.
        Assert.False(withoutFlag.GetProperty("onlinePayments").GetBoolean());
        AssertMoney(withoutFlag.GetProperty("current"), 0, 0);
        AssertMoney(withoutFlag.GetProperty("previous"), 0, 0);
    }

    // ─── The counters ───

    [PostgresFact]
    public async Task Counts_AreTheNumbersOfTheTabsOnRealRows_AndEqualTheTotalsOfTheLists()
    {
        var world = await ServiceRequestWorlds.SeedAsync(_factory);
        var proposal = (Start: Utc("2026-11-04T08:00:00Z"), End: Utc("2026-11-04T10:00:00Z"));
        await AddAsync(
            world,
            [
                .. Enumerable.Range(0, 4).Select(i => Row(ServiceRequestStatus.Richiesto, $"2026-10-2{i}T08:00:00Z")),
                .. Enumerable.Range(0, 3).Select(i => Row(ServiceRequestStatus.Richiesto, $"2026-10-1{i}T08:00:00Z", proposal: proposal)),
                .. Enumerable.Range(0, 2).Select(i => Row(ServiceRequestStatus.PresoInCarico, $"2026-10-0{i + 1}T08:00:00Z", taken: $"2026-10-0{i + 1}T08:10:00Z")),
                Row(ServiceRequestStatus.InCorso, "2026-10-05T08:00:00Z", taken: "2026-10-05T08:10:00Z"),
                .. Enumerable.Range(0, 5).Select(i => Row(ServiceRequestStatus.Completato, $"2026-09-0{i + 1}T08:00:00Z", taken: $"2026-09-0{i + 1}T08:10:00Z", completed: $"2026-09-0{i + 1}T12:00:00Z")),
                Row(ServiceRequestStatus.Pagato, "2026-08-01T08:00:00Z", taken: "2026-08-01T08:10:00Z", completed: "2026-08-01T12:00:00Z"),
                Row(ServiceRequestStatus.Rifiutato, "2026-08-02T08:00:00Z", updated: "2026-08-02T09:00:00Z"),
                Row(ServiceRequestStatus.Annullato, "2026-08-03T08:00:00Z", updated: "2026-08-03T09:00:00Z"),
            ]);
        using var supplier = _factory.Supplier(world);

        var counts = await supplier.GetFromJsonAsync<JsonElement>("/api/supplier/inbox/counts");

        Assert.Equal(4, counts.GetProperty("toAnswer").GetInt32());
        Assert.Equal(3, counts.GetProperty("awaitingCustomer").GetInt32());
        Assert.Equal(3, counts.GetProperty("scheduled").GetInt32());
        Assert.Equal(5, counts.GetProperty("toCollect").GetInt32());
        Assert.Equal(3, counts.GetProperty("archive").GetInt32());
        Assert.Equal(0, counts.GetProperty("paymentsLate").GetInt32());
        foreach (var (tab, counter) in new[]
                 {
                     ("da-rispondere", "toAnswer"), ("in-attesa", "awaitingCustomer"), ("programmate", "scheduled"),
                     ("da-incassare", "toCollect"), ("archivio", "archive"),
                 })
        {
            var list = await supplier.GetFromJsonAsync<JsonElement>($"/api/supplier/inbox?tab={tab}&pageSize=1");
            Assert.Equal(counts.GetProperty(counter).GetInt32(), list.GetProperty("total").GetInt32());
        }
    }

    [PostgresFact]
    public async Task Counts_OverviewAndTheSearch_NeverMixTwoSuppliers()
    {
        var a = await ServiceRequestWorlds.SeedAsync(_factory);
        var b = await ServiceRequestWorlds.SeedAsync(_factory);
        await AddAsync(a, [.. Enumerable.Range(0, 3).Select(i => Row(ServiceRequestStatus.Richiesto, $"2026-10-2{i}T08:00:00Z", name: "Servizio solo di alfa"))]);
        await AddAsync(b, [.. Enumerable.Range(0, 5).Select(i => Row(ServiceRequestStatus.Richiesto, $"2026-10-2{i}T08:00:00Z", name: "Servizio solo di beta"))]);
        await AddAsync(b, [Row(ServiceRequestStatus.Completato, "2026-10-02T08:00:00Z", taken: "2026-10-02T08:10:00Z", completed: "2026-10-03T08:00:00Z")]);
        await using var app = WithClock(Today);
        using var supplierA = ClientOf(app, a.SupplierUserId);
        using var supplierB = ClientOf(app, b.SupplierUserId);

        var countsA = await supplierA.GetFromJsonAsync<JsonElement>("/api/supplier/inbox/counts");
        var countsB = await supplierB.GetFromJsonAsync<JsonElement>("/api/supplier/inbox/counts");
        var overviewA = (await supplierA.GetFromJsonAsync<JsonElement>("/api/supplier/dashboard/overview")).GetProperty("current");
        var overviewB = (await supplierB.GetFromJsonAsync<JsonElement>("/api/supplier/dashboard/overview")).GetProperty("current");
        var foundByA = await supplierA.GetFromJsonAsync<JsonElement>("/api/supplier/inbox?q=solo%20di%20beta");
        var foundByB = await supplierB.GetFromJsonAsync<JsonElement>("/api/supplier/inbox?q=solo%20di%20beta");

        Assert.Equal((3, 0), (countsA.GetProperty("toAnswer").GetInt32(), countsA.GetProperty("toCollect").GetInt32()));
        Assert.Equal((5, 1), (countsB.GetProperty("toAnswer").GetInt32(), countsB.GetProperty("toCollect").GetInt32()));
        Assert.Equal(3, overviewA.GetProperty("received").GetInt32());
        Assert.Equal(6, overviewB.GetProperty("received").GetInt32());
        Assert.Equal(0, foundByA.GetProperty("total").GetInt32());
        Assert.Equal(5, foundByB.GetProperty("total").GetInt32());
    }

    // ─── The search, on PostgreSQL ───

    [PostgresFact]
    public async Task Search_APercentOrAnUnderscore_IsTheCharacterItIsInTheLikeOfPostgreSql_AndTheCaseDoesNotMatter()
    {
        var world = await ServiceRequestWorlds.SeedAsync(_factory);
        await AddAsync(
            world,
            [
                Row(ServiceRequestStatus.Richiesto, "2026-10-20T08:00:00Z", name: "Sconto 100% sul totale"),
                Row(ServiceRequestStatus.Richiesto, "2026-10-21T08:00:00Z", name: "Sconto 100 sul totale"),
                Row(ServiceRequestStatus.Richiesto, "2026-10-22T08:00:00Z", name: "Codice a_b"),
                Row(ServiceRequestStatus.Richiesto, "2026-10-23T08:00:00Z", name: "Codice axb"),
                Row(ServiceRequestStatus.Richiesto, "2026-10-24T08:00:00Z", name: "Pulizia di fine soggiorno"),
            ]);
        using var supplier = _factory.Supplier(world);

        Assert.Equal(new[] { "Sconto 100% sul totale" }, await NamesAsync(supplier, "q=100%25"));
        Assert.Equal(new[] { "Codice a_b" }, await NamesAsync(supplier, "q=a_b"));
        Assert.Empty(await NamesAsync(supplier, "q=%25%25"));
        Assert.Empty(await NamesAsync(supplier, "q=__"));
        Assert.Equal(new[] { "Pulizia di fine soggiorno" }, await NamesAsync(supplier, "q=PULIZIA"));
        Assert.Equal(new[] { "Pulizia di fine soggiorno" }, await NamesAsync(supplier, "q=fine%20sogg"));
    }

    // ─── helpers ───

    private sealed record RowSeed(
        ServiceRequestStatus Status,
        DateTime Created,
        DateTime? Taken,
        DateTime? Completed,
        DateTime? Updated,
        (DateTime Start, DateTime End)? Proposal,
        string? Name);

    private static RowSeed Row(
        ServiceRequestStatus status,
        string created,
        string? taken = null,
        string? completed = null,
        string? updated = null,
        (DateTime Start, DateTime End)? proposal = null,
        string? name = null) =>
        new(
            status,
            Utc(created),
            taken is null ? null : Utc(taken),
            completed is null ? null : Utc(completed),
            updated is null ? null : Utc(updated),
            proposal,
            name);

    private static DateTime Utc(string value) =>
        DateTime.Parse(value, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal);

    private static DateOnly RomeDay(DateTime utc) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Rome));

    private static bool InDays(DateTime utc, (DateOnly From, DateOnly To) days) => RomeDay(utc) >= days.From && RomeDay(utc) <= days.To;

    /// <summary>
    /// Requests across September and October: every status, with the dates that go with it, and the instants at the edges of the Rome
    /// days (a minute before and at midnight in Rome, summer and winter time) that decide which period a request falls in.
    /// </summary>
    private static List<RowSeed> Rows()
    {
        var rows = new List<RowSeed>();
        var random = new Random(2026);
        var statuses = new[]
        {
            ServiceRequestStatus.Richiesto, ServiceRequestStatus.PresoInCarico, ServiceRequestStatus.InCorso, ServiceRequestStatus.Completato,
            ServiceRequestStatus.Pagato, ServiceRequestStatus.Rifiutato, ServiceRequestStatus.Annullato,
        };
        for (var i = 0; i < 60; i++)
        {
            var created = Utc("2026-08-25T06:00:00Z").AddMinutes(random.Next(0, 65 * 24 * 60));
            var status = statuses[random.Next(statuses.Length)];
            var tookIt = status is ServiceRequestStatus.PresoInCarico or ServiceRequestStatus.InCorso or ServiceRequestStatus.Completato
                             or ServiceRequestStatus.Pagato
                         || (status == ServiceRequestStatus.Annullato && random.Next(2) == 0); // cancelled after the take
            DateTime? taken = tookIt ? created.AddMinutes(random.Next(1, 3 * 24 * 60)) : null;
            DateTime? completed = status is ServiceRequestStatus.Completato or ServiceRequestStatus.Pagato ? taken!.Value.AddMinutes(random.Next(60, 2000)) : null;
            DateTime? updated = status is ServiceRequestStatus.Rifiutato or ServiceRequestStatus.Annullato ? created.AddMinutes(random.Next(1, 4000)) : null;
            rows.Add(new RowSeed(status, created, taken, completed, updated, null, null));
        }

        // The edges: created a second before and at the first instant of the current month in Rome (summer time: 22:00 UTC the day
        // before), and a second before and at the first instant of tomorrow in Rome (winter time: 23:00 UTC).
        foreach (var edge in new[] { "2026-09-30T21:59:59Z", "2026-09-30T22:00:00Z", "2026-10-28T22:59:59Z", "2026-10-28T23:00:00Z", "2026-09-02T21:59:59Z", "2026-09-02T22:00:00Z" })
            rows.Add(Row(ServiceRequestStatus.Richiesto, edge));
        foreach (var edge in new[] { "2026-09-30T21:59:59Z", "2026-09-30T22:00:00Z", "2026-10-28T22:59:59Z", "2026-10-28T23:00:00Z" })
        {
            rows.Add(Row(ServiceRequestStatus.PresoInCarico, "2026-08-30T08:00:00Z", taken: edge));
            rows.Add(Row(ServiceRequestStatus.Rifiutato, "2026-08-30T08:00:00Z", updated: edge));
            rows.Add(Row(ServiceRequestStatus.Completato, "2026-08-30T08:00:00Z", taken: "2026-08-30T08:10:00Z", completed: edge));
        }

        return rows;
    }

    private static void AssertPeriod(JsonElement period, List<RowSeed> rows, (DateOnly From, DateOnly To) days)
    {
        Assert.Equal(days.From.ToString("yyyy-MM-dd"), period.GetProperty("from").GetString());
        Assert.Equal(days.To.ToString("yyyy-MM-dd"), period.GetProperty("to").GetString());

        var received = rows.Count(r => InDays(r.Created, days));
        var takenInPeriod = rows.Where(r => r.Taken is { } t && InDays(t, days)).ToList();
        var accepted = takenInPeriod.Count;
        var rejected = rows.Count(r => r.Status == ServiceRequestStatus.Rifiutato && InDays(r.Updated!.Value, days));
        var completed = rows.Count(r => r.Status is ServiceRequestStatus.Completato or ServiceRequestStatus.Pagato && InDays(r.Completed!.Value, days));

        Assert.Equal(received, period.GetProperty("received").GetInt32());
        Assert.Equal(accepted, period.GetProperty("accepted").GetInt32());
        Assert.Equal(rejected, period.GetProperty("rejected").GetInt32());
        Assert.Equal(completed, period.GetProperty("completedJobs").GetInt32());
        if (accepted + rejected == 0)
        {
            Assert.Equal(JsonValueKind.Null, period.GetProperty("acceptanceRate").ValueKind);
        }
        else
        {
            Assert.Equal(Math.Round((double)accepted / (accepted + rejected), 4, MidpointRounding.AwayFromZero), period.GetProperty("acceptanceRate").GetDouble());
        }

        if (accepted == 0)
        {
            Assert.Equal(JsonValueKind.Null, period.GetProperty("averageResponseMinutes").ValueKind);
        }
        else
        {
            var mean = takenInPeriod.Average(r => (r.Taken!.Value - r.Created).TotalMinutes);
            // The database keeps microseconds and .NET 100 ns: a whole-minute average cannot differ by more than one minute at a .5 edge.
            Assert.InRange(period.GetProperty("averageResponseMinutes").GetInt32(), (int)Math.Floor(mean), (int)Math.Ceiling(mean));
        }
    }

    private static void AssertMoney(JsonElement period, long gross, long commission)
    {
        Assert.Equal(gross, period.GetProperty("paidGrossCents").GetInt64());
        Assert.Equal(commission, period.GetProperty("commissionCents").GetInt64());
        Assert.Equal(gross - commission, period.GetProperty("paidNetCents").GetInt64());
    }

    private async Task AddAsync(ServiceRequestWorld world, IEnumerable<RowSeed> rows)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        foreach (var row in rows)
        {
            db.ServiceRequests.Add(NewRequest(world, row));
        }

        await db.SaveChangesAsync();
    }

    private static ServiceRequest NewRequest(ServiceRequestWorld world, RowSeed row) => new()
    {
        OrgId = world.HostOrgId,
        PropertyId = world.PropertyId,
        BookingId = world.BookingId,
        RentalContext = ServiceRequestRentalContext.ShortRent,
        SupplierOrgId = world.SupplierOrgId,
        Category = "cleaning",
        Status = row.Status,
        ServiceNameSnapshot = row.Name,
        TakenAt = row.Taken,
        CompletedAt = row.Completed,
        PaidAt = row.Status == ServiceRequestStatus.Pagato ? row.Completed?.AddDays(1) : null,
        RejectionReason = row.Status == ServiceRequestStatus.Rifiutato ? "Non disponibile" : null,
        CancelledAt = row.Status == ServiceRequestStatus.Annullato ? row.Updated : null,
        CancelledBy = row.Status == ServiceRequestStatus.Annullato ? ServiceRequestActorParty.Host : null,
        FinalAmountCents = row.Status is ServiceRequestStatus.Completato or ServiceRequestStatus.Pagato ? 6000 : null,
        ProposedStartUtc = row.Proposal?.Start,
        ProposedEndUtc = row.Proposal?.End,
        ProposedAt = row.Proposal is null ? null : row.Created.AddMinutes(30),
        CreatedAt = row.Created,
        UpdatedAt = row.Updated ?? row.Completed ?? row.Taken ?? row.Created,
    };

    private async Task AddPaymentsAsync(
        ServiceRequestWorld world,
        IEnumerable<(DateTime PaidAt, int Amount, decimal Percent, ServicePaymentStatus Status)> payments)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        foreach (var (paidAt, amount, percent, status) in payments)
        {
            var collected = status is ServicePaymentStatus.Paid or ServicePaymentStatus.PartiallyRefunded or ServicePaymentStatus.Refunded;
            var request = NewRequest(
                world,
                Row(
                    collected ? ServiceRequestStatus.Pagato : ServiceRequestStatus.Completato,
                    "2026-09-01T08:00:00Z",
                    taken: "2026-09-01T08:10:00Z",
                    completed: "2026-09-02T08:00:00Z"));
            request.PaymentMode = ServiceRequestPaymentMode.Online;
            request.FinalAmountCents = amount;
            db.ServiceRequests.Add(request);
            var fee = (int)decimal.Round(amount * percent / 100m, 0, MidpointRounding.AwayFromZero);
            db.ServiceRequestPayments.Add(new ServiceRequestPayment
            {
                ServiceRequestId = request.Id,
                SupplierOrgId = world.SupplierOrgId,
                PayerKind = ServicePayerKind.Host,
                PayerOrgId = world.HostOrgId,
                AmountCents = amount,
                Currency = "eur",
                CommissionPercent = percent,
                ApplicationFeeCents = fee,
                NetCents = amount - fee,
                Status = status,
                RequestedAt = Utc("2026-09-02T08:00:00Z"),
                LastSentAt = Utc("2026-09-02T08:00:00Z"),
                SentCount = 1,
                PaidAt = collected ? paidAt : null,
                PaidVia = collected ? ServicePaymentChannel.Stripe : null,
                CreatedAt = Utc("2026-09-02T08:00:00Z"),
                UpdatedAt = paidAt,
            });
        }

        await db.SaveChangesAsync();
    }

    private static async Task<List<string>> NamesAsync(HttpClient supplier, string query)
    {
        var body = await supplier.GetFromJsonAsync<JsonElement>($"/api/supplier/inbox?{query}&pageSize=100");
        return body.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("serviceName").GetString()!).ToList();
    }

    private WebApplicationFactory<Program> WithClock(DateTimeOffset now, bool paymentsFlag = false) =>
        _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?> { ["Features:SupplierOnlinePayments"] = paymentsFlag ? "true" : "false" }));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(new FixedTimeProvider(now));
            });
        });

    private static HttpClient ClientOf(WebApplicationFactory<Program> app, string userId)
    {
        var client = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(TestAuthHandler.SchemeName, "test");
        client.DefaultRequestHeaders.Add("X-Test-User", userId);
        client.DefaultRequestHeaders.Add("X-Test-Roles", "Supplier");
        return client;
    }
}
