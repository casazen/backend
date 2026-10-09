using System.Net;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// LR-01, B1: <c>GET /api/long-rent/rents</c>, the rent register of the area, over the real pipeline (PostgreSQL in CI, InMemory in a
/// local run). The world is described in <see cref="LongRentWorld"/>: "today" is 2026-10-09 in Rome.
/// </summary>
public class LongRentRentRegisterIntegrationTests(LongRentAggregatesFactory factory) : IClassFixture<LongRentAggregatesFactory>, IAsyncLifetime
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private LongRentWorld _world = null!;

    public async Task InitializeAsync() => _world = await LongRentWorld.SeedAsync(factory);

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Register_TheCurrentMonthByDefault_ListsTheInstallmentsDueInItByDueDate()
    {
        using var client = _world.OwnerClient(factory);

        var page = await GetAsync(client, "/api/long-rent/rents");

        Assert.Equal("2026-10", page.GetProperty("month").GetString());
        // Due 2/10 (paid), 5/10 (overdue), 7/10 (a payment in flight): the cancelled installment of the month is not part of the rent.
        Assert.Equal(
            [_world.Installment("ExpiringOct"), _world.Installment("ActiveOct"), _world.Installment("TransitoryOct")],
            Ids(page));
        Assert.Equal(3, page.GetProperty("total").GetInt32());
        Assert.Equal(1, page.GetProperty("page").GetInt32());
        Assert.Equal(25, page.GetProperty("pageSize").GetInt32());
        Assert.Equal(24, page.GetProperty("reminderIntervalHours").GetInt32());
        Assert.False(page.GetProperty("onlinePaymentsAvailable").GetBoolean());
    }

    [Fact]
    public async Task Register_AnotherMonth_ListsThatMonth()
    {
        using var client = _world.OwnerClient(factory);

        var august = await GetAsync(client, "/api/long-rent/rents?month=2026-08");
        var november = await GetAsync(client, "/api/long-rent/rents?month=2026-11");
        var empty = await GetAsync(client, "/api/long-rent/rents?month=2031-01");

        Assert.Equal([_world.Installment("ActiveAug")], Ids(august));
        Assert.Equal([_world.Installment("ActiveNov")], Ids(november));
        Assert.Empty(Ids(empty));
        Assert.Equal(0, empty.GetProperty("total").GetInt32());
        Assert.Equal(0, empty.GetProperty("counters").GetProperty("expected").GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task Register_TheNumbersOfTheMonth_AreExpectedCollectedPendingAndOverdue_WhateverTheFilter()
    {
        using var client = _world.OwnerClient(factory);

        foreach (var url in new[] { "/api/long-rent/rents?month=2026-10", "/api/long-rent/rents?month=2026-10&status=Overdue", "/api/long-rent/rents?month=2026-10&status=Paid&pageSize=1" })
        {
            var counters = (await GetAsync(client, url)).GetProperty("counters");

            Assert.Equal((3, 2100m), Counter(counters, "expected"));
            Assert.Equal((1, 700m), Counter(counters, "collected"));
            Assert.Equal((1, 500m), Counter(counters, "pending"));
            Assert.Equal((1, 900m), Counter(counters, "overdue"));
        }
    }

    [Fact]
    public async Task Register_Status_NarrowsTheListNotTheNumbers()
    {
        using var client = _world.OwnerClient(factory);

        var paid = await GetAsync(client, "/api/long-rent/rents?month=2026-10&status=Paid");
        var pending = await GetAsync(client, "/api/long-rent/rents?month=2026-10&status=pending");
        var overdue = await GetAsync(client, "/api/long-rent/rents?month=2026-10&status=OVERDUE");
        var all = await GetAsync(client, "/api/long-rent/rents?month=2026-10&status=All");

        Assert.Equal([_world.Installment("ExpiringOct")], Ids(paid));
        Assert.Equal([_world.Installment("TransitoryOct")], Ids(pending));
        Assert.Equal([_world.Installment("ActiveOct")], Ids(overdue));
        Assert.Equal(3, Ids(all).Count);
        Assert.Equal(1, overdue.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Register_AnInstallmentPastDueAndUnpaid_IsOverdueWithItsDays_ComputedByTheLedgerRule()
    {
        using var client = _world.OwnerClient(factory);

        var rows = Rows(await GetAsync(client, "/api/long-rent/rents?month=2026-10"));

        var overdue = rows[_world.Installment("ActiveOct")];
        Assert.True(overdue.GetProperty("isOverdue").GetBoolean());
        Assert.Equal(4, overdue.GetProperty("daysOverdue").GetInt32());
        Assert.Equal("Scheduled", overdue.GetProperty("status").GetString());

        // The same installment on the ledger of its lease says the same.
        var ledger = await client.GetAsync($"/api/leases/{_world.Active}/rent");
        var installment = JsonSerializer.Deserialize<JsonElement>(await ledger.Content.ReadAsStringAsync(), JsonOptions)
            .GetProperty("installments").EnumerateArray()
            .Single(i => i.GetProperty("id").GetGuid() == _world.Installment("ActiveOct"));
        Assert.True(installment.GetProperty("isOverdue").GetBoolean());

        // A payment in flight is not overdue, whatever its date; a paid installment neither.
        var processing = rows[_world.Installment("TransitoryOct")];
        Assert.Equal("Processing", processing.GetProperty("status").GetString());
        Assert.False(processing.GetProperty("isOverdue").GetBoolean());
        Assert.Equal(JsonValueKind.Null, processing.GetProperty("daysOverdue").ValueKind);
        Assert.False(rows[_world.Installment("ExpiringOct")].GetProperty("isOverdue").GetBoolean());
    }

    [Fact]
    public async Task Register_ARow_CarriesThePropertyTheFirstTenantByNameAndWhatCanBeDone()
    {
        using var client = _world.OwnerClient(factory);

        var response = await client.GetAsync("/api/long-rent/rents?month=2026-10");
        var body = await response.Content.ReadAsStringAsync();
        var rows = Rows(JsonSerializer.Deserialize<JsonElement>(body, JsonOptions));

        var row = rows[_world.Installment("ActiveOct")];
        Assert.Equal(_world.Active, row.GetProperty("leaseId").GetGuid());
        Assert.Equal(_world.P1, row.GetProperty("propertyId").GetGuid());
        Assert.Equal("Bilocale Sparano", row.GetProperty("propertyName").GetString());
        Assert.Equal("Giulia", row.GetProperty("tenantFirstName").GetString());
        Assert.Equal("Verdi", row.GetProperty("tenantLastName").GetString());
        Assert.Equal("2026-10-01", row.GetProperty("periodStart").GetString());
        Assert.Equal("2026-10-31", row.GetProperty("periodEnd").GetString());
        Assert.Equal("2026-10-05", row.GetProperty("dueDate").GetString());
        Assert.Equal(900m, row.GetProperty("amount").GetDecimal());
        Assert.Equal("EUR", row.GetProperty("currency").GetString());
        Assert.True(row.GetProperty("canRemind").GetBoolean());
        Assert.Equal(0, row.GetProperty("reminderCount").GetInt32());
        Assert.Equal(JsonValueKind.Null, row.GetProperty("lastReminderAt").ValueKind);

        // A paid installment shows how and when; it cannot be reminded. Neither can one being processed.
        var paid = rows[_world.Installment("ExpiringOct")];
        Assert.Equal("Offline", paid.GetProperty("paidVia").GetString());
        Assert.Equal("2026-10-03", paid.GetProperty("paidOn").GetString());
        Assert.False(paid.GetProperty("canRemind").GetBoolean());
        Assert.False(rows[_world.Installment("TransitoryOct")].GetProperty("canRemind").GetBoolean());

        // The tenant by name only: no fiscal code, no address.
        Assert.DoesNotContain("VRDGLI85B02F205A", body, StringComparison.Ordinal);
        Assert.DoesNotContain("@", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Register_APageAtATime_NeverRepeatsNorSkipsARow()
    {
        var world = await LongRentWorld.SeedAsync(factory);
        // Seven more installments of the same lease, five of them due on the same day: the order must still be total.
        await AddInstallmentsAsync(world, world.Active, "2026-10-20", "2026-10-20", "2026-10-20", "2026-10-20", "2026-10-20", "2026-10-21", "2026-10-30");
        using var client = world.OwnerClient(factory);

        var everything = Ids(await GetAsync(client, "/api/long-rent/rents?month=2026-10&pageSize=100"));
        Assert.Equal(10, everything.Count);
        Assert.Equal(everything.Count, everything.Distinct().Count());

        var paged = new List<Guid>();
        for (var number = 1; number <= 4; number++)
        {
            var page = await GetAsync(client, $"/api/long-rent/rents?month=2026-10&pageSize=3&page={number}");
            Assert.Equal(10, page.GetProperty("total").GetInt32());
            Assert.Equal(number, page.GetProperty("page").GetInt32());
            paged.AddRange(Ids(page));
        }

        // The pages, one after the other, are the whole list in the same order; asking again gives the same page.
        Assert.Equal(everything, paged);
        Assert.Equal(
            Ids(await GetAsync(client, "/api/long-rent/rents?month=2026-10&pageSize=3&page=2")),
            Ids(await GetAsync(client, "/api/long-rent/rents?month=2026-10&pageSize=3&page=2")));
        // The due dates never go back.
        var dueDates = (await GetAsync(client, "/api/long-rent/rents?month=2026-10&pageSize=100")).GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("dueDate").GetString()!).ToList();
        Assert.Equal(dueDates.Order(StringComparer.Ordinal), dueDates);
    }

    [Fact]
    public async Task Register_PageAndPageSize_AreKeptInsideTheirLimits()
    {
        using var client = _world.OwnerClient(factory);

        var tooBig = await GetAsync(client, "/api/long-rent/rents?month=2026-10&pageSize=1000&page=0");
        Assert.Equal(100, tooBig.GetProperty("pageSize").GetInt32());
        Assert.Equal(1, tooBig.GetProperty("page").GetInt32());

        var tooFar = await GetAsync(client, "/api/long-rent/rents?month=2026-10&pageSize=2&page=9");
        Assert.Empty(Ids(tooFar));
        Assert.Equal(3, tooFar.GetProperty("total").GetInt32());
    }

    [Theory]
    [InlineData("month=2026-13", "rent_register_month_invalid")]
    [InlineData("month=2026-1", "rent_register_month_invalid")]
    [InlineData("month=2026-10-05", "rent_register_month_invalid")]
    [InlineData("month=ottobre", "rent_register_month_invalid")]
    [InlineData("month=1999-12", "rent_register_month_invalid")]
    [InlineData("month=2101-01", "rent_register_month_invalid")]
    [InlineData("status=late", "rent_register_status_unknown")]
    [InlineData("status=2", "rent_register_status_unknown")]
    [InlineData("status=Paid,Overdue", "rent_register_status_unknown")]
    public async Task Register_AnInvalidMonthOrStatus_Returns400WithTheCode(string query, string code)
    {
        using var client = _world.OwnerClient(factory);

        var response = await client.GetAsync($"/api/long-rent/rents?{query}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(code, JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync(), JsonOptions).GetProperty("code").GetString());
    }

    // --- Who sees what ----------------------------------------------------------------------------------

    [Fact]
    public async Task Register_ALandlordWithLimitedScope_SeesNeitherTheRowsNorTheNumbersOfOtherProperties()
    {
        using var client = _world.ColleagueClient(factory);

        var page = await GetAsync(client, "/api/long-rent/rents?month=2026-10");

        Assert.Equal([_world.Installment("ColleagueOct")], Ids(page));
        var counters = page.GetProperty("counters");
        Assert.Equal((1, 900m), Counter(counters, "expected"));
        Assert.Equal((0, 0m), Counter(counters, "collected"));
        Assert.Equal((1, 900m), Counter(counters, "pending"));
        Assert.Equal((0, 0m), Counter(counters, "overdue"));
        // The overdue installment of the owner's lease is not there, not even by asking for it.
        Assert.Empty(Ids(await GetAsync(client, "/api/long-rent/rents?month=2026-10&status=Overdue")));
        Assert.Empty(Ids(await GetAsync(client, "/api/long-rent/rents?month=2026-08")));
    }

    [Fact]
    public async Task Register_AnOrgWideManager_SeesEveryPropertyOfTheOrg()
    {
        using var client = _world.ManagerClient(factory);

        var page = await GetAsync(client, "/api/long-rent/rents?month=2026-10");

        Assert.Equal(4, Ids(page).Count);
        Assert.Contains(_world.Installment("ColleagueOct"), Ids(page));
        Assert.Equal((4, 3000m), Counter(page.GetProperty("counters"), "expected"));
    }

    [Fact]
    public async Task Register_AnotherOrg_SeesNothingOfThisWorld()
    {
        var stranger = await LongRentWorld.SeedAsync(factory);
        using var client = stranger.ManagerClient(factory);

        var page = await GetAsync(client, "/api/long-rent/rents?month=2026-10");

        Assert.DoesNotContain(_world.Installment("ActiveOct"), Ids(page));
        Assert.DoesNotContain(_world.Installment("ColleagueOct"), Ids(page));
        Assert.Equal(4, Ids(page).Count);
    }

    [Fact]
    public async Task Register_WithoutLogin_Returns401_AndWithoutTheLongTermRole_403()
    {
        using var anonymous = factory.CreateClient();
        using var wrongRole = factory.CreateAuthenticatedClient(_world.OwnerId, "PropertyOwner");

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/long-rent/rents")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await wrongRole.GetAsync("/api/long-rent/rents")).StatusCode);
    }

    // --- helpers ----------------------------------------------------------------------------------------

    private static async Task<JsonElement> GetAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync(), JsonOptions);
    }

    private static List<Guid> Ids(JsonElement page) =>
        page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).ToList();

    private static Dictionary<Guid, JsonElement> Rows(JsonElement page) =>
        page.GetProperty("items").EnumerateArray().ToDictionary(i => i.GetProperty("id").GetGuid());

    private static (int Count, decimal Amount) Counter(JsonElement counters, string name) =>
        (counters.GetProperty(name).GetProperty("count").GetInt32(), counters.GetProperty(name).GetProperty("amount").GetDecimal());

    /// <summary>More installments on the schedule of a lease, each due on the given day (the periods are only there to be distinct).</summary>
    private async Task AddInstallmentsAsync(LongRentWorld world, Guid leaseId, params string[] dueDates)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var scheduleId = await db.RentSchedules.Where(s => s.LeaseContractId == leaseId).Select(s => s.Id).SingleAsync();
        var firstPeriod = new DateOnly(2030, 1, 1);
        for (var i = 0; i < dueDates.Length; i++)
        {
            db.RentLedgerEntries.Add(new RentLedgerEntry
            {
                OrgId = world.OrgId,
                LeaseContractId = leaseId,
                RentScheduleId = scheduleId,
                PeriodStart = firstPeriod.AddMonths(i),
                PeriodEnd = firstPeriod.AddMonths(i + 1).AddDays(-1),
                DueDate = DateOnly.Parse(dueDates[i]),
                AmountDue = 100m + i,
                Status = RentLedgerStatus.Scheduled,
            });
        }

        await db.SaveChangesAsync();
    }
}
