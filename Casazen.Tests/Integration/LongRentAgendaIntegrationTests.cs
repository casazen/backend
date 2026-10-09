using System.Net;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Leases;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// LR-01, B2: <c>GET /api/long-rent/deadlines</c> and <c>GET /api/long-rent/overview</c>, over the real pipeline (PostgreSQL in CI,
/// InMemory in a local run). The world is described in <see cref="LongRentWorld"/>: "today" is 2026-10-09 in Rome.
/// </summary>
public class LongRentAgendaIntegrationTests(LongRentAggregatesFactory factory) : IClassFixture<LongRentAggregatesFactory>, IAsyncLifetime
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private LongRentWorld _world = null!;

    public async Task InitializeAsync() => _world = await LongRentWorld.SeedAsync(factory);

    public Task DisposeAsync() => Task.CompletedTask;

    // --- Deadlines --------------------------------------------------------------------------------------

    [Fact]
    public async Task Deadlines_ByDefault_AreTheNextNinetyDaysAndWhatIsPastAndOpen_InDateOrder()
    {
        using var client = _world.OwnerClient(factory);

        var answer = await GetAsync(client, "/api/long-rent/deadlines");

        Assert.Equal("2026-10-09", answer.GetProperty("from").GetString());
        Assert.Equal("2027-01-07", answer.GetProperty("to").GetString());
        Assert.False(answer.GetProperty("truncated").GetBoolean());
        Assert.Equal(
            [
                ("Rent", "2026-08-05", -65, true),
                ("Questura", "2026-10-03", -6, true),
                ("Rent", "2026-10-05", -4, true),
                ("RliRegistration", "2026-10-20", 11, false),
                ("Rent", "2026-11-05", 27, false),
                ("LeaseEnd", "2026-11-30", 52, false),
                ("Notice", "2026-11-30", 52, false),
            ],
            Items(answer).Select(i => (i.GetProperty("type").GetString()!, i.GetProperty("date").GetString()!, i.GetProperty("daysFromToday").GetInt32(), i.GetProperty("isOverdue").GetBoolean())));
    }

    [Fact]
    public async Task Deadlines_EachOneSaysWhichLeaseAndPropertyAndTheTenantByName()
    {
        using var client = _world.OwnerClient(factory);

        var items = Items(await GetAsync(client, "/api/long-rent/deadlines")).ToList();

        // Questura of the extra-EU tenant: 48 hours from the start (no delivery date declared), so due on 3 October.
        var questura = items.Single(i => Type(i) == "Questura");
        Assert.Equal(_world.Signed, questura.GetProperty("leaseId").GetGuid());
        Assert.Equal(_world.P2, questura.GetProperty("propertyId").GetGuid());
        Assert.Equal("Trilocale Murat", questura.GetProperty("propertyName").GetString());
        Assert.Equal("Luca", questura.GetProperty("tenantFirstName").GetString());
        Assert.Equal("Neri", questura.GetProperty("tenantLastName").GetString());
        Assert.Equal(JsonValueKind.Null, questura.GetProperty("installmentId").ValueKind);

        // Registration: min(stipula 20/9, start 1/10) + 30 days = 20 October.
        var rli = items.Single(i => Type(i) == "RliRegistration");
        Assert.Equal(_world.Signed, rli.GetProperty("leaseId").GetGuid());

        // The installments carry their id and amount.
        var october = items.Single(i => Type(i) == "Rent" && i.GetProperty("date").GetString() == "2026-10-05");
        Assert.Equal(_world.Installment("ActiveOct"), october.GetProperty("installmentId").GetGuid());
        Assert.Equal(900m, october.GetProperty("amount").GetDecimal());
        Assert.Equal(_world.Active, october.GetProperty("leaseId").GetGuid());
        Assert.Equal("Giulia", october.GetProperty("tenantFirstName").GetString());

        // End of the lease and last day of notice (6 months before the end of a lease of the 4+4 kind): 31 May 2027 and 30 November 2026.
        Assert.Equal(_world.Expiring, items.Single(i => Type(i) == "LeaseEnd").GetProperty("leaseId").GetGuid());
        Assert.Equal(_world.Long, items.Single(i => Type(i) == "Notice").GetProperty("leaseId").GetGuid());
    }

    [Theory]
    [InlineData("Rent", 3)]
    [InlineData("rent", 3)]
    [InlineData("Questura", 1)]
    [InlineData("RliRegistration", 1)]
    [InlineData("LeaseEnd", 1)]
    [InlineData("Notice", 1)]
    public async Task Deadlines_Type_NarrowsTheAgenda(string type, int expected)
    {
        using var client = _world.OwnerClient(factory);

        var items = Items(await GetAsync(client, $"/api/long-rent/deadlines?type={type}")).ToList();

        Assert.Equal(expected, items.Count);
        Assert.All(items, i => Assert.Equal(type, Type(i), ignoreCase: true));
    }

    [Fact]
    public async Task Deadlines_AWindowInTheFuture_DoesNotCarryWhatIsPast()
    {
        using var client = _world.OwnerClient(factory);

        var november = Items(await GetAsync(client, "/api/long-rent/deadlines?from=2026-11-01&to=2026-11-30")).ToList();

        Assert.Equal(
            [("Rent", "2026-11-05"), ("LeaseEnd", "2026-11-30"), ("Notice", "2026-11-30")],
            november.Select(i => (Type(i), i.GetProperty("date").GetString()!)));
    }

    [Fact]
    public async Task Deadlines_AWindowThatContainsToday_CarriesWhatIsPastBeforeItAndStillOpen()
    {
        using var client = _world.OwnerClient(factory);

        var october = Items(await GetAsync(client, "/api/long-rent/deadlines?from=2026-10-01&to=2026-10-31")).ToList();

        // August is before the window and still unpaid: it comes first, flagged overdue. The rest are the days of October.
        Assert.Equal(
            [("Rent", "2026-08-05"), ("Questura", "2026-10-03"), ("Rent", "2026-10-05"), ("RliRegistration", "2026-10-20")],
            october.Select(i => (Type(i), i.GetProperty("date").GetString()!)));
        Assert.All(october.Take(3), i => Assert.True(i.GetProperty("isOverdue").GetBoolean()));
    }

    [Fact]
    public async Task Deadlines_AYear_ListsTheEndsAndTheNoticeOfTheLeasesThatReachIt_ButNeverATransitoryNotice()
    {
        using var client = _world.OwnerClient(factory);

        var year = Items(await GetAsync(client, "/api/long-rent/deadlines?from=2026-10-09&to=2027-10-09&type=LeaseEnd")).ToList();
        var notices = Items(await GetAsync(client, "/api/long-rent/deadlines?from=2026-10-09&to=2027-10-09&type=Notice")).ToList();

        Assert.Equal(
            [_world.Expiring, _world.Transitory, _world.Anonymized, _world.Long],
            year.Select(i => i.GetProperty("leaseId").GetGuid()));
        // The notice: only for a contract with a renewal to refuse. A transitory ends by itself; a notice date already past is no deadline.
        Assert.Equal([_world.Long], notices.Select(i => i.GetProperty("leaseId").GetGuid()));
        // The tenant whose data were anonymized has no name.
        var anonymized = year.Single(i => i.GetProperty("leaseId").GetGuid() == _world.Anonymized);
        Assert.Equal(JsonValueKind.Null, anonymized.GetProperty("tenantFirstName").ValueKind);
        Assert.Equal(JsonValueKind.Null, anonymized.GetProperty("tenantLastName").ValueKind);
    }

    [Fact]
    public async Task Deadlines_WhatIsDone_IsNotListedAnyMore()
    {
        var world = await LongRentWorld.SeedAsync(factory);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            // The landlord declared the Questura communication, the lease was registered, and the August and October rents were paid.
            var signed = await db.LeaseContracts.SingleAsync(l => l.Id == world.Signed);
            signed.QuesturaCommunicationDate = LongRentWorld.Day("2026-10-02");
            signed.Status = LeaseStatus.Registered;
            foreach (var name in new[] { "ActiveAug", "ActiveOct" })
            {
                var entry = await db.RentLedgerEntries.SingleAsync(e => e.Id == world.Installment(name));
                entry.Status = RentLedgerStatus.Paid;
                entry.PaidVia = RentPaymentChannel.Offline;
                entry.PaidOn = new DateOnly(2026, 10, 8);
            }

            await db.SaveChangesAsync();
        }

        using var client = world.OwnerClient(factory);
        var items = Items(await GetAsync(client, "/api/long-rent/deadlines")).ToList();

        Assert.DoesNotContain(items, i => Type(i) is "Questura" or "RliRegistration");
        Assert.Equal(["2026-11-05"], items.Where(i => Type(i) == "Rent").Select(i => i.GetProperty("date").GetString()));
        // The lease of the signed contract is now a registered one: its end (30/9/2030) is far, nothing of it in the window.
        Assert.DoesNotContain(items, i => i.GetProperty("leaseId").GetGuid() == world.Signed);
    }

    [Fact]
    public async Task Deadlines_APaymentInFlight_IsNotADeadline_AFailedOneStillIs()
    {
        var world = await LongRentWorld.SeedAsync(factory);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            (await db.RentLedgerEntries.SingleAsync(e => e.Id == world.Installment("ActiveNov"))).Status = RentLedgerStatus.Processing;
            (await db.RentLedgerEntries.SingleAsync(e => e.Id == world.Installment("ActiveOct"))).Status = RentLedgerStatus.Failed;
            await db.SaveChangesAsync();
        }

        using var client = world.OwnerClient(factory);
        var rents = Items(await GetAsync(client, "/api/long-rent/deadlines?type=Rent")).ToList();

        Assert.Equal(["2026-08-05", "2026-10-05"], rents.Select(i => i.GetProperty("date").GetString()));
    }

    [Fact]
    public async Task Deadlines_MoreInstallmentsThanTheLimit_AreCutAndSaySo()
    {
        var world = await LongRentWorld.SeedAsync(factory);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var scheduleId = await db.RentSchedules.Where(s => s.LeaseContractId == world.Active).Select(s => s.Id).SingleAsync();
            for (var i = 0; i < LongRentDeadlineRules.MaxRentItems + 5; i++)
            {
                db.RentLedgerEntries.Add(new RentLedgerEntry
                {
                    OrgId = world.OrgId,
                    LeaseContractId = world.Active,
                    RentScheduleId = scheduleId,
                    PeriodStart = new DateOnly(2031, 1, 1).AddDays(i),
                    PeriodEnd = new DateOnly(2031, 1, 1).AddDays(i),
                    DueDate = new DateOnly(2026, 12, 1),
                    AmountDue = 10m,
                    Status = RentLedgerStatus.Scheduled,
                });
            }

            await db.SaveChangesAsync();
        }

        using var client = world.OwnerClient(factory);
        var answer = await GetAsync(client, "/api/long-rent/deadlines?type=Rent");

        Assert.True(answer.GetProperty("truncated").GetBoolean());
        Assert.Equal(LongRentDeadlineRules.MaxRentItems, Items(answer).Count());
        // The earliest ones are kept: the two overdue ones and the one of 5 November come first.
        Assert.Equal(["2026-08-05", "2026-10-05", "2026-11-05"], Items(answer).Take(3).Select(i => i.GetProperty("date").GetString()));
    }

    [Theory]
    [InlineData("from=2026-13-01", "long_rent_deadlines_range_invalid")]
    [InlineData("from=09/10/2026", "long_rent_deadlines_range_invalid")]
    [InlineData("to=oggi", "long_rent_deadlines_range_invalid")]
    [InlineData("from=2026-11-01&to=2026-10-01", "long_rent_deadlines_range_invalid")]
    [InlineData("from=2026-10-09&to=2027-10-11", "long_rent_deadlines_range_invalid")]
    [InlineData("from=1999-01-01", "long_rent_deadlines_range_invalid")]
    [InlineData("type=Imu", "long_rent_deadlines_type_unknown")]
    [InlineData("type=1", "long_rent_deadlines_type_unknown")]
    [InlineData("type=Rent,Questura", "long_rent_deadlines_type_unknown")]
    public async Task Deadlines_AnInvalidWindowOrType_Returns400WithTheCode(string query, string code)
    {
        using var client = _world.OwnerClient(factory);

        var response = await client.GetAsync($"/api/long-rent/deadlines?{query}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(code, Parse(await response.Content.ReadAsStringAsync()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Deadlines_TheLongestWindow_IsAccepted()
    {
        using var client = _world.OwnerClient(factory);

        var answer = await GetAsync(client, "/api/long-rent/deadlines?from=2026-10-09&to=2027-10-10");

        Assert.Equal("2027-10-10", answer.GetProperty("to").GetString());
    }

    [Fact]
    public async Task Deadlines_ALandlordWithLimitedScope_SeesOnlyTheDeadlinesOfItsProperties()
    {
        using var colleague = _world.ColleagueClient(factory);
        using var manager = _world.ManagerClient(factory);

        var own = Items(await GetAsync(colleague, "/api/long-rent/deadlines")).ToList();
        var all = Items(await GetAsync(manager, "/api/long-rent/deadlines")).ToList();

        var only = Assert.Single(own);
        Assert.Equal("Rent", Type(only));
        Assert.Equal(_world.Installment("ColleagueOct"), only.GetProperty("installmentId").GetGuid());
        // The manager of the org sees the colleague's rent among the others.
        Assert.Equal(8, all.Count);
        Assert.Contains(all, i => i.GetProperty("leaseId").GetGuid() == _world.Colleague);
        Assert.Empty(Items(await GetAsync(colleague, "/api/long-rent/deadlines?type=Questura")));
    }

    [Fact]
    public async Task Deadlines_AnotherOrg_SeesNothingOfThisWorld()
    {
        var stranger = await LongRentWorld.SeedAsync(factory);
        using var client = stranger.ManagerClient(factory);

        var items = Items(await GetAsync(client, "/api/long-rent/deadlines")).ToList();

        Assert.DoesNotContain(items, i => new[] { _world.Active, _world.Signed, _world.Colleague }.Contains(i.GetProperty("leaseId").GetGuid()));
        Assert.Equal(8, items.Count);
    }

    [Fact]
    public async Task Deadlines_WithoutLogin_Returns401_AndWithoutTheLongTermRole_403()
    {
        using var anonymous = factory.CreateClient();
        using var wrongRole = factory.CreateAuthenticatedClient(_world.OwnerId, "PropertyOwner");

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/long-rent/deadlines")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await wrongRole.GetAsync("/api/long-rent/deadlines")).StatusCode);
    }

    // --- Overview ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Overview_CountsTheLeasesTheWayTheViewsOfTheListDo()
    {
        using var client = _world.OwnerClient(factory);

        var overview = await GetAsync(client, "/api/long-rent/overview");

        Assert.Equal("2026-10-09", overview.GetProperty("today").GetString());
        var leases = overview.GetProperty("leases");
        Assert.Equal(5, leases.GetProperty("active").GetInt32());
        Assert.Equal(3, leases.GetProperty("expiring").GetInt32());
        Assert.Equal(2, leases.GetProperty("inPreparation").GetInt32());
        Assert.Equal(1, leases.GetProperty("toSign").GetInt32());
        Assert.Equal(1, leases.GetProperty("toRegister").GetInt32());
        Assert.Equal(1, leases.GetProperty("ended").GetInt32());

        // The same numbers as the lease list says with its views, for the same caller.
        foreach (var (view, name) in new[] { ("Active", "active"), ("Expiring", "expiring"), ("InPreparation", "inPreparation"), ("Ended", "ended") })
        {
            var list = JsonSerializer.Deserialize<JsonElement>(await (await client.GetAsync($"/api/leases?view={view}")).Content.ReadAsStringAsync(), JsonOptions);
            Assert.Equal(list.GetArrayLength(), leases.GetProperty(name).GetInt32());
        }
    }

    [Fact]
    public async Task Overview_TheRentOfTheCurrentMonth_IsTheNumbersOfTheRegister()
    {
        using var client = _world.OwnerClient(factory);

        var overview = await GetAsync(client, "/api/long-rent/overview");
        var register = await GetAsync(client, "/api/long-rent/rents?month=2026-10");

        Assert.Equal("2026-10", overview.GetProperty("rentMonth").GetString());
        Assert.Equal(register.GetProperty("counters").ToString(), overview.GetProperty("rents").ToString());
        Assert.Equal(2100m, overview.GetProperty("rents").GetProperty("expected").GetProperty("amount").GetDecimal());
    }

    [Fact]
    public async Task Overview_TheChecklist_HasWhatWaitsForTheLandlord_MostUrgentFirst()
    {
        using var client = _world.OwnerClient(factory);

        var checklist = (await GetAsync(client, "/api/long-rent/overview")).GetProperty("checklist").EnumerateArray().ToList();

        Assert.Equal(["RentOverdue", "RliRegistration", "Questura", "LeaseToSign"], checklist.Select(i => i.GetProperty("kind").GetString()));

        // Two installments past due, all months together: 900 of August and 900 of October; the oldest is on the lease Active.
        var rent = checklist[0];
        Assert.Equal(2, rent.GetProperty("count").GetInt32());
        Assert.Equal(1800m, rent.GetProperty("amount").GetDecimal());
        Assert.Equal("2026-08-05", rent.GetProperty("date").GetString());
        Assert.Equal(_world.Active, rent.GetProperty("leaseId").GetGuid());
        Assert.True(rent.GetProperty("isOverdue").GetBoolean());

        // The signed contract waits for its registration (due 20 October) and for the Questura communication (due 3 October, missed).
        Assert.Equal(("2026-10-20", false), (checklist[1].GetProperty("date").GetString()!, checklist[1].GetProperty("isOverdue").GetBoolean()));
        Assert.Equal(_world.Signed, checklist[1].GetProperty("leaseId").GetGuid());
        Assert.Equal(("2026-10-03", true), (checklist[2].GetProperty("date").GetString()!, checklist[2].GetProperty("isOverdue").GetBoolean()));
        Assert.Equal(_world.Signed, checklist[2].GetProperty("leaseId").GetGuid());

        // The draft, which starts on 1 November.
        Assert.Equal(1, checklist[3].GetProperty("count").GetInt32());
        Assert.Equal("2026-11-01", checklist[3].GetProperty("date").GetString());
        Assert.Equal(_world.Draft, checklist[3].GetProperty("leaseId").GetGuid());
        Assert.False(checklist[3].GetProperty("isOverdue").GetBoolean());
    }

    [Fact]
    public async Task Overview_TheNextDeadline_IsTheFirstThatIsNotPast()
    {
        using var client = _world.OwnerClient(factory);

        var next = (await GetAsync(client, "/api/long-rent/overview")).GetProperty("nextDeadline");

        // The missed ones are in the checklist; the next one to come is the registration of the signed contract.
        Assert.Equal("RliRegistration", next.GetProperty("type").GetString());
        Assert.Equal("2026-10-20", next.GetProperty("date").GetString());
        Assert.False(next.GetProperty("isOverdue").GetBoolean());
        Assert.Equal(_world.Signed, next.GetProperty("leaseId").GetGuid());
    }

    [Fact]
    public async Task Overview_ALandlordWithLimitedScope_SeesOnlyItsOwnNumbers()
    {
        using var client = _world.ColleagueClient(factory);

        var overview = await GetAsync(client, "/api/long-rent/overview");

        Assert.Equal(1, overview.GetProperty("leases").GetProperty("active").GetInt32());
        Assert.Equal(0, overview.GetProperty("leases").GetProperty("inPreparation").GetInt32());
        Assert.Equal(0, overview.GetProperty("checklist").GetArrayLength());
        var rents = overview.GetProperty("rents");
        Assert.Equal(1, rents.GetProperty("expected").GetProperty("count").GetInt32());
        Assert.Equal(900m, rents.GetProperty("pending").GetProperty("amount").GetDecimal());
        Assert.Equal(0, rents.GetProperty("overdue").GetProperty("count").GetInt32());
        var next = overview.GetProperty("nextDeadline");
        Assert.Equal("Rent", next.GetProperty("type").GetString());
        Assert.Equal(_world.Installment("ColleagueOct"), next.GetProperty("installmentId").GetGuid());
    }

    [Fact]
    public async Task Overview_AnOrgWithoutLeases_IsAllZeroAndNothingToDo()
    {
        var owner = $"auth0|lr01-empty-{Guid.NewGuid():N}";
        await factory.SeedOrgForOwnerAsync(owner);
        using var client = factory.CreateAuthenticatedClient(owner, "LongTermLandlord");

        var overview = await GetAsync(client, "/api/long-rent/overview");

        Assert.Equal(0, overview.GetProperty("leases").GetProperty("active").GetInt32());
        Assert.Equal(0, overview.GetProperty("leases").GetProperty("ended").GetInt32());
        Assert.Equal(0, overview.GetProperty("rents").GetProperty("expected").GetProperty("count").GetInt32());
        Assert.Equal(0m, overview.GetProperty("rents").GetProperty("overdue").GetProperty("amount").GetDecimal());
        Assert.Equal(0, overview.GetProperty("checklist").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, overview.GetProperty("nextDeadline").ValueKind);
    }

    [Fact]
    public async Task Overview_WithoutLogin_Returns401_AndWithoutTheLongTermRole_403()
    {
        using var anonymous = factory.CreateClient();
        using var wrongRole = factory.CreateAuthenticatedClient(_world.OwnerId, "PropertyOwner");

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/long-rent/overview")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await wrongRole.GetAsync("/api/long-rent/overview")).StatusCode);
    }

    // --- helpers ----------------------------------------------------------------------------------------

    private static JsonElement Parse(string json) => JsonSerializer.Deserialize<JsonElement>(json, JsonOptions);

    private static async Task<JsonElement> GetAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return Parse(await response.Content.ReadAsStringAsync());
    }

    private static IEnumerable<JsonElement> Items(JsonElement answer) => answer.GetProperty("items").EnumerateArray();

    private static string Type(JsonElement item) => item.GetProperty("type").GetString()!;
}
