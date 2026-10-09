using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Tests.Unit.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// SR-03 over the real pipeline (routes, policies, model binding, error contract, JSON): the reads of the short rent area, as the
/// web app will call them. An owner, a collaborator limited to one of the org's two properties (a person of the database only: no
/// role in the token), and the data of <see cref="HostScopeScenario"/> on both properties. Nothing here depends on the day the
/// tests run. On PostgreSQL in CI; on the in-memory fallback locally (the seed data of the model is written first).
/// </summary>
public class ShortRentReadApiHttpTests(OrgInvitationsFactory factory) : IClassFixture<OrgInvitationsFactory>
{
    private sealed record World(
        string OwnerId,
        Guid OrgId,
        string CollaboratorId,
        Guid CollaboratorMemberId,
        Guid GrantedId,
        Guid HiddenId,
        Guid GrantedBookingId,
        Guid HiddenBookingId);

    private static readonly SemaphoreSlim SeedLock = new(1, 1);
    private static readonly Regex BookingCodeShape = new("^[0-9A-Z]{5}-[0-9A-Z]{5}$", RegexOptions.Compiled);

    private HttpClient Owner(World world) => factory.CreateAuthenticatedClient(world.OwnerId, roles: "PropertyOwner");

    private HttpClient Collaborator(World world) => factory.CreateAuthenticatedClient(world.CollaboratorId);

    /// <summary>An org with its owner, a collaborator limited to the first of two properties full of data.</summary>
    private async Task<World> NewWorldAsync(bool limited = true)
    {
        await EnsureSeedsAsync();
        var (ownerId, orgId) = await OrgTeamHttp.SeedOwnerWithTeamRowsAsync(factory);
        var collaboratorId = await OrgTeamHttp.AddMemberAsync(factory, orgId, OrgRole.Collaborator, ["short-rent"]);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (granted, hidden, _) = await HostScopeScenario.SeedPropertiesOfAsync(db, orgId, ownerId);
        var memberId = await db.OrgMembers.IgnoreQueryFilters().Where(m => m.UserId == collaboratorId).Select(m => m.Id).SingleAsync();
        var grantedBooking = await db.Bookings.IgnoreQueryFilters().Where(b => b.PropertyId == granted.Id).Select(b => b.Id).FirstAsync();
        var hiddenBooking = await db.Bookings.IgnoreQueryFilters().Where(b => b.PropertyId == hidden.Id).Select(b => b.Id).FirstAsync();
        var world = new World(ownerId, orgId, collaboratorId, memberId, granted.Id, hidden.Id, grantedBooking, hiddenBooking);

        if (limited)
        {
            using var owner = Owner(world);
            var response = await owner.PutAsJsonAsync(
                $"/api/orgs/me/members/{memberId}/properties", new { propertyScope = "Selected", propertyIds = new[] { granted.Id } });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        return world;
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK)
    {
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(code, problem.GetProperty("code").GetString());
    }

    // --- GET /api/dashboard/today ---------------------------------------------------------------------

    [Fact]
    public async Task Today_TheOwnerSeesTheWholeOrg_TheCollaboratorOnlyItsProperty_WithoutTheMoneyItCannotRead()
    {
        var world = await NewWorldAsync();
        // A confirmed stay of the first property whose payment failed: the owner is told, the collaborator (no payment.read) is not.
        var failedBookingId = await AddStayWithAFailedPaymentAsync(world);
        using var owner = Owner(world);
        using var collaborator = Collaborator(world);

        var ownerDay = await JsonAsync(await owner.GetAsync("/api/dashboard/today"));
        var collaboratorDay = await JsonAsync(await collaborator.GetAsync("/api/dashboard/today"));

        // The shape the Home reads.
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}$", ownerDay.GetProperty("today").GetString());
        foreach (var list in new[] { "arrivals", "departures", "upcoming", "approvals", "todo" })
        {
            Assert.True(ownerDay.GetProperty(list).GetProperty("count").GetInt32() >= 0, list);
            Assert.Equal(JsonValueKind.Array, ownerDay.GetProperty(list).GetProperty("items").ValueKind);
        }

        var ownerTodo = ownerDay.GetProperty("todo").GetProperty("items").EnumerateArray().ToList();
        var collaboratorTodo = collaboratorDay.GetProperty("todo").GetProperty("items").EnumerateArray().ToList();

        // Properties to activate and turnovers to close do not depend on the day: both properties for the owner, one for the other.
        Assert.Equal(
            new[] { world.GrantedId, world.HiddenId }.Order(),
            ownerTodo.Where(i => i.GetProperty("action").GetString() == "ActivateProperty").Select(i => i.GetProperty("propertyId").GetGuid()).Order());
        Assert.Equal(
            [world.GrantedId],
            collaboratorTodo.Where(i => i.GetProperty("action").GetString() == "ActivateProperty").Select(i => i.GetProperty("propertyId").GetGuid()));
        Assert.Equal(
            2,
            ownerTodo.Count(i => i.GetProperty("action").GetString() == "ConfirmPropertyReady"));
        Assert.Equal(
            1,
            collaboratorTodo.Count(i => i.GetProperty("action").GetString() == "ConfirmPropertyReady"));

        // Every thing to do says what it lacks, where it leads (a key and ids, never a path), and its place in the order.
        foreach (var item in ownerTodo)
        {
            Assert.NotEmpty(item.GetProperty("missing").EnumerateArray());
            Assert.All(item.GetProperty("missing").EnumerateArray(), m => Assert.False(string.IsNullOrWhiteSpace(m.GetProperty("code").GetString())));
            Assert.InRange(item.GetProperty("priority").GetInt32(), 1, 8);
            Assert.False(item.TryGetProperty("url", out _) || item.TryGetProperty("route", out _) || item.TryGetProperty("link", out _));
        }

        var priorities = ownerTodo.Select(i => i.GetProperty("priority").GetInt32()).ToList();
        Assert.Equal(priorities.Order(), priorities);

        // The failed payment: only for who may read payments.
        Assert.Contains(
            ownerTodo,
            i => i.GetProperty("action").GetString() == "ReviewFailedPayment" && i.GetProperty("bookingId").GetGuid() == failedBookingId);
        Assert.DoesNotContain(collaboratorTodo, i => i.GetProperty("action").GetString() == "ReviewFailedPayment");

        // Nothing of the hidden property reaches the collaborator, in any list.
        var text = collaboratorDay.GetRawText();
        Assert.DoesNotContain(world.HiddenId.ToString(), text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(world.HiddenBookingId.ToString(), text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Today_ASupplierWithoutHostPermissions_IsForbidden()
    {
        using var supplier = factory.CreateAuthenticatedClient($"auth0|sr03-supplier-{Guid.NewGuid():N}", roles: "Supplier");

        Assert.Equal(HttpStatusCode.Forbidden, (await supplier.GetAsync("/api/dashboard/today")).StatusCode);
    }

    private async Task<Guid> AddStayWithAFailedPaymentAsync(World world)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var guestId = await db.Guests.IgnoreQueryFilters().Where(g => g.OrgId == world.OrgId).Select(g => g.Id).FirstAsync();
        var booking = new Booking
        {
            OrgId = world.OrgId,
            PropertyId = world.GrantedId,
            GuestId = guestId,
            Status = BookingStatus.Confirmed,
            Source = BookingSource.Direct,
            CheckInDate = TimeProvider.System.TodayInRome().AddDays(60),
            CheckOutDate = TimeProvider.System.TodayInRome().AddDays(63),
            NumberOfGuests = 2,
            BasePrice = 300m,
            TotalPrice = 300m,
        };
        db.Bookings.Add(booking);
        db.Payments.Add(new Payment
        {
            OrgId = world.OrgId,
            BookingId = booking.Id,
            Status = PaymentStatus.Failed,
            Amount = 300m,
            TransactionId = $"tx-{Guid.NewGuid():N}",
        });
        await db.SaveChangesAsync();
        return booking.Id;
    }

    // --- GET /api/dashboard/kpis ----------------------------------------------------------------------

    [Fact]
    public async Task Kpis_Next30DaysWithTheComparison_OwnerGetsTheMoney_CollaboratorDoesNot()
    {
        var world = await NewWorldAsync();
        using var owner = Owner(world);
        using var collaborator = Collaborator(world);

        var ownerKpis = await JsonAsync(await owner.GetAsync("/api/dashboard/kpis?period=Next30Days&compare=true"));
        var collaboratorKpis = await JsonAsync(await collaborator.GetAsync("/api/dashboard/kpis?period=Next30Days&compare=true"));

        Assert.Equal("Next30Days", ownerKpis.GetProperty("period").GetProperty("kind").GetString());
        Assert.Equal(30, ownerKpis.GetProperty("period").GetProperty("nights").GetInt32());
        Assert.Equal(2, ownerKpis.GetProperty("propertyCount").GetInt32());
        Assert.Equal(1, collaboratorKpis.GetProperty("propertyCount").GetInt32());

        // The three numbers of the Home: occupancy and revenue as before, the cash, the share of the booking site.
        Assert.True(ownerKpis.GetProperty("revenue").TryGetProperty("amountCents", out _));
        var collected = ownerKpis.GetProperty("collected");
        Assert.Equal("EUR", collected.GetProperty("currency").GetString());
        Assert.Equal(collected.GetProperty("amount").GetDecimal() * 100m, collected.GetProperty("amountCents").GetInt64());
        var direct = ownerKpis.GetProperty("directShare");
        Assert.True(direct.GetProperty("stays").GetInt32() >= direct.GetProperty("directStays").GetInt32());

        // The period before: the same days in number, ending the day this one starts.
        var previous = ownerKpis.GetProperty("previous");
        Assert.Equal(30, previous.GetProperty("period").GetProperty("nights").GetInt32());
        Assert.Equal(
            DateOnly.Parse(ownerKpis.GetProperty("period").GetProperty("from").GetString()!).AddDays(-1),
            DateOnly.Parse(previous.GetProperty("period").GetProperty("to").GetString()!));
        Assert.True(previous.TryGetProperty("occupancy", out _));

        // No payment.read: no cash, now or before.
        Assert.Equal(JsonValueKind.Null, collaboratorKpis.GetProperty("collected").ValueKind);
        Assert.Equal(JsonValueKind.Null, collaboratorKpis.GetProperty("previous").GetProperty("collected").ValueKind);
    }

    [Fact]
    public async Task Kpis_WithoutTheNewParameters_AreTheOldAnswerPlusTheNewFields()
    {
        var world = await NewWorldAsync(limited: false);
        using var owner = Owner(world);

        var kpis = await JsonAsync(await owner.GetAsync("/api/dashboard/kpis"));

        Assert.Equal("Month", kpis.GetProperty("period").GetProperty("kind").GetString());
        foreach (var field in new[] { "today", "propertyCount", "occupancy", "revenue", "arrivalsToday", "departuresToday", "upcomingCheckIns", "recentBookings" })
            Assert.True(kpis.TryGetProperty(field, out _), field);
        Assert.Equal(JsonValueKind.Null, kpis.GetProperty("previous").ValueKind);
    }

    [Fact]
    public async Task Kpis_OneProperty_NarrowsEverything_AndOnlyOneTheCallerReaches()
    {
        var world = await NewWorldAsync();
        using var owner = Owner(world);
        using var collaborator = Collaborator(world);

        var one = await JsonAsync(await owner.GetAsync($"/api/dashboard/kpis?propertyId={world.HiddenId}"));

        Assert.Equal(1, one.GetProperty("propertyCount").GetInt32());
        Assert.All(
            one.GetProperty("recentBookings").EnumerateArray(),
            stay => Assert.Equal(world.HiddenId, stay.GetProperty("propertyId").GetGuid()));
        // The collaborator was not given that property, and an unknown one is the same: one 404, nothing tells them apart.
        await AssertProblemAsync(await collaborator.GetAsync($"/api/dashboard/kpis?propertyId={world.HiddenId}"), HttpStatusCode.NotFound, "not_found");
        await AssertProblemAsync(await owner.GetAsync($"/api/dashboard/kpis?propertyId={Guid.NewGuid()}"), HttpStatusCode.NotFound, "not_found");
        Assert.Equal(HttpStatusCode.OK, (await collaborator.GetAsync($"/api/dashboard/kpis?propertyId={world.GrantedId}")).StatusCode);
    }

    [Theory]
    [InlineData("?period=Next30Days&month=2026-10")]
    [InlineData("?period=Next60Days")]
    [InlineData("?period=2")]
    public async Task Kpis_AnInvalidPeriod_Is400WithItsCode(string query)
    {
        var world = await NewWorldAsync(limited: false);
        using var owner = Owner(world);

        await AssertProblemAsync(await owner.GetAsync($"/api/dashboard/kpis{query}"), HttpStatusCode.BadRequest, "dashboard_invalid_period");
    }

    // --- GET /api/compliance/summary ------------------------------------------------------------------

    [Fact]
    public async Task Cockpit_EveryItemSaysWhatItLacks_AndTheOldFieldsAreThere()
    {
        var world = await NewWorldAsync();
        using var owner = Owner(world);
        using var collaborator = Collaborator(world);

        var summary = await JsonAsync(await owner.GetAsync("/api/compliance/summary"));
        var limited = await JsonAsync(await collaborator.GetAsync("/api/compliance/summary"));

        var items = summary.EnumerateObject().SelectMany(s => s.Value.GetProperty("items").EnumerateArray()).ToList();
        Assert.NotEmpty(items);
        foreach (var item in items)
        {
            foreach (var field in new[] { "id", "label", "action", "propertyId", "bookingId" })
                Assert.True(item.TryGetProperty(field, out _), field);
            Assert.NotEmpty(item.GetProperty("missing").EnumerateArray());
        }

        var activation = summary.GetProperty("propertiesPending").GetProperty("items").EnumerateArray().First();
        Assert.Contains(
            activation.GetProperty("missing").EnumerateArray(),
            m => m.GetProperty("code").GetString() == "activation_cin_missing" && m.GetProperty("field").GetString() == "cin");
        Assert.Equal(2, summary.GetProperty("propertiesPending").GetProperty("count").GetInt32());
        Assert.Equal(1, limited.GetProperty("propertiesPending").GetProperty("count").GetInt32());
    }

    // --- GET /api/bookings/search ---------------------------------------------------------------------

    [Fact]
    public async Task BookingSearch_APageOfBookings_WithTheCodeOfEach_InsideTheScope()
    {
        var world = await NewWorldAsync();
        using var owner = Owner(world);
        using var collaborator = Collaborator(world);

        var all = await JsonAsync(await owner.GetAsync("/api/bookings/search?pageSize=100"));
        var mine = await JsonAsync(await collaborator.GetAsync("/api/bookings/search?pageSize=100"));

        Assert.Equal(1, all.GetProperty("page").GetInt32());
        Assert.Equal(100, all.GetProperty("pageSize").GetInt32());
        Assert.Equal(6, all.GetProperty("totalCount").GetInt32());
        Assert.Equal(6, all.GetProperty("items").GetArrayLength());
        Assert.All(all.GetProperty("items").EnumerateArray(), b => Assert.Matches(BookingCodeShape, b.GetProperty("bookingCode").GetString()));
        // The collaborator: the three bookings of its property, never one of the other.
        Assert.Equal(3, mine.GetProperty("totalCount").GetInt32());
        Assert.All(mine.GetProperty("items").EnumerateArray(), b => Assert.Equal(world.GrantedId, b.GetProperty("propertyId").GetGuid()));
        // Latest check-in first.
        var checkIns = all.GetProperty("items").EnumerateArray().Select(b => b.GetProperty("checkInDate").GetDateTime()).ToList();
        Assert.Equal(checkIns.OrderByDescending(d => d), checkIns);
    }

    [Fact]
    public async Task BookingSearch_StatusTextAndPages()
    {
        var world = await NewWorldAsync();
        using var owner = Owner(world);

        var pending = await JsonAsync(await owner.GetAsync("/api/bookings/search?status=Pending"));
        var two = await JsonAsync(await owner.GetAsync("/api/bookings/search?status=Pending&status=CheckedOut"));
        var commas = await JsonAsync(await owner.GetAsync("/api/bookings/search?status=Pending,CheckedOut"));
        var byName = await JsonAsync(await owner.GetAsync("/api/bookings/search?q=anna%20trullo"));
        var byCode = await JsonAsync(await owner.GetAsync($"/api/bookings/{world.HiddenBookingId}"));
        var code = byCode.GetProperty("bookingCode").GetString()!;
        var byCodeSearch = await JsonAsync(await owner.GetAsync($"/api/bookings/search?q={Uri.EscapeDataString(code)}"));
        var second = await JsonAsync(await owner.GetAsync("/api/bookings/search?pageSize=4&page=2"));

        Assert.Equal(2, pending.GetProperty("totalCount").GetInt32());
        Assert.All(pending.GetProperty("items").EnumerateArray(), b => Assert.Equal("Pending", b.GetProperty("status").GetString()));
        Assert.Equal(4, two.GetProperty("totalCount").GetInt32());
        Assert.Equal(4, commas.GetProperty("totalCount").GetInt32());
        // The guests of the scenario are named after their property.
        Assert.Equal(3, byName.GetProperty("totalCount").GetInt32());
        Assert.All(byName.GetProperty("items").EnumerateArray(), b => Assert.Equal(world.GrantedId, b.GetProperty("propertyId").GetGuid()));
        Assert.Equal(world.HiddenBookingId, Assert.Single(byCodeSearch.GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid());
        Assert.Equal((2, 4, 6, 2), (
            second.GetProperty("page").GetInt32(),
            second.GetProperty("pageSize").GetInt32(),
            second.GetProperty("totalCount").GetInt32(),
            second.GetProperty("items").GetArrayLength()));
    }

    [Fact]
    public async Task BookingSearch_WhatTheCollaboratorMayNotFind_IsNotThere()
    {
        var world = await NewWorldAsync();
        using var owner = Owner(world);
        using var collaborator = Collaborator(world);
        var hiddenCode = (await JsonAsync(await owner.GetAsync($"/api/bookings/{world.HiddenBookingId}"))).GetProperty("bookingCode").GetString()!;

        var byCode = await JsonAsync(await collaborator.GetAsync($"/api/bookings/search?q={Uri.EscapeDataString(hiddenCode)}"));
        var byName = await JsonAsync(await collaborator.GetAsync("/api/bookings/search?q=anna%20casa%20bianca"));

        Assert.Equal(0, byCode.GetProperty("totalCount").GetInt32());
        Assert.Equal(0, byName.GetProperty("totalCount").GetInt32());
        // The property itself, as in the plain list.
        Assert.Equal(HttpStatusCode.Forbidden, (await collaborator.GetAsync($"/api/bookings/search?propertyId={world.HiddenId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await collaborator.GetAsync($"/api/bookings/search?propertyId={Guid.NewGuid()}")).StatusCode);
    }

    [Fact]
    public async Task BookingSearch_InvalidRequests_AreAnswered400WithTheirCodes()
    {
        var world = await NewWorldAsync(limited: false);
        using var owner = Owner(world);

        await AssertProblemAsync(
            await owner.GetAsync("/api/bookings/search?status=Done"), HttpStatusCode.BadRequest, "booking_list_invalid_status");
        await AssertProblemAsync(
            await owner.GetAsync("/api/bookings/search?status=3"), HttpStatusCode.BadRequest, "booking_list_invalid_status");
        await AssertProblemAsync(
            await owner.GetAsync("/api/bookings/search?from=2026-11-01&to=2026-10-01"), HttpStatusCode.BadRequest, "booking_list_invalid_range");
        // A date that is not a date is a validation error of the framework, with the code of every other one.
        await AssertProblemAsync(
            await owner.GetAsync("/api/bookings/search?from=ieri"), HttpStatusCode.BadRequest, "validation_error");
    }

    [Fact]
    public async Task Bookings_ThePlainList_NowCarriesTheBookingCodeToo()
    {
        var world = await NewWorldAsync(limited: false);
        using var owner = Owner(world);

        var list = await JsonAsync(await owner.GetAsync("/api/bookings"));
        var one = await JsonAsync(await owner.GetAsync($"/api/bookings/{world.GrantedBookingId}"));

        // The plain list is paged since PC-14: { items, totalCount, page, pageSize }.
        Assert.Equal(JsonValueKind.Object, list.ValueKind);
        var items = list.GetProperty("items").EnumerateArray().ToList();
        Assert.NotEmpty(items);
        Assert.All(items, b => Assert.Matches(BookingCodeShape, b.GetProperty("bookingCode").GetString()));
        Assert.Matches(BookingCodeShape, one.GetProperty("bookingCode").GetString());
    }

    // --- GET /api/payments ----------------------------------------------------------------------------

    [Fact]
    public async Task Payments_RowsWithTheGuestAndThePropertyAndTheAmountsInCents_StillTheFieldsTheWebAppReads()
    {
        var world = await NewWorldAsync(limited: false);
        using var owner = Owner(world);

        var rows = (await JsonAsync(await owner.GetAsync("/api/payments"))).EnumerateArray().ToList();

        Assert.Equal(4, rows.Count);
        foreach (var row in rows)
        {
            // What the web app reads today (a superset of the old entity's fields it uses).
            foreach (var field in new[] { "id", "bookingId", "amount", "status", "method", "createdAt", "updatedAt", "refundedAmount", "description" })
                Assert.True(row.TryGetProperty(field, out _), field);
            // What the new screens read.
            Assert.Matches(BookingCodeShape, row.GetProperty("bookingCode").GetString());
            Assert.StartsWith("Anna ", row.GetProperty("guestName").GetString());
            Assert.Contains(row.GetProperty("propertyName").GetString(), new[] { "Trullo", "Casa Bianca" });
            Assert.Equal(row.GetProperty("amount").GetDecimal() * 100m, row.GetProperty("amountCents").GetInt64());
            Assert.Equal("EUR", row.GetProperty("currency").GetString());
            Assert.Equal("Completed", row.GetProperty("status").GetString());
            // The entity came with its whole booking and the Stripe account of the host: not any more.
            Assert.False(row.TryGetProperty("booking", out _));
            Assert.False(row.TryGetProperty("stripeAccountId", out _));
            Assert.False(row.TryGetProperty("orgId", out _));
        }
    }

    [Fact]
    public async Task Payments_ByBookingByPropertyAndByPeriod()
    {
        var world = await NewWorldAsync(limited: false);
        using var owner = Owner(world);

        var ofBooking = (await JsonAsync(await owner.GetAsync($"/api/payments?bookingId={world.HiddenBookingId}"))).EnumerateArray().ToList();
        var ofProperty = (await JsonAsync(await owner.GetAsync($"/api/payments?propertyId={world.GrantedId}"))).EnumerateArray().ToList();
        // The payments of the scenario were settled on 5 October 2026.
        var inPeriod = (await JsonAsync(await owner.GetAsync("/api/payments?from=2026-10-05&to=2026-10-05"))).EnumerateArray().ToList();
        var before = (await JsonAsync(await owner.GetAsync("/api/payments?to=2026-10-04"))).EnumerateArray().ToList();
        var after = (await JsonAsync(await owner.GetAsync("/api/payments?from=2026-10-06"))).EnumerateArray().ToList();

        Assert.Single(ofBooking);
        Assert.All(ofBooking, row => Assert.Equal(world.HiddenBookingId, row.GetProperty("bookingId").GetGuid()));
        Assert.Equal(2, ofProperty.Count);
        Assert.All(ofProperty, row => Assert.Equal(world.GrantedId, row.GetProperty("propertyId").GetGuid()));
        Assert.Equal(4, inPeriod.Count);
        Assert.Empty(before);
        Assert.Empty(after);
    }

    [Fact]
    public async Task Payments_TheLimitsOfTheRequest_And_TheCollaboratorWhoCannotReadThemAtAll()
    {
        var world = await NewWorldAsync();
        using var owner = Owner(world);
        using var collaborator = Collaborator(world);

        await AssertProblemAsync(
            await owner.GetAsync("/api/payments?from=2026-11-01&to=2026-10-01"), HttpStatusCode.BadRequest, "payment_list_invalid_range");
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync($"/api/payments?bookingId={Guid.NewGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync($"/api/payments?propertyId={Guid.NewGuid()}")).StatusCode);
        // payment.read is not a collaborator's permission, whatever property it was given.
        Assert.Equal(HttpStatusCode.Forbidden, (await collaborator.GetAsync("/api/payments")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await collaborator.GetAsync($"/api/payments?bookingId={world.GrantedBookingId}")).StatusCode);
    }

    // --- The seed data the in-memory fallback lacks ----------------------------------------------------

    /// <summary>
    /// On PostgreSQL the migrations seed the contexts, roles and permissions. The in-memory fallback of the host has none (and
    /// <c>EnsureCreated</c> seeds only a store nobody has touched, which the host's start-up already did): write the seed data
    /// of the model, once, so the same tests run locally.
    /// </summary>
    private async Task EnsureSeedsAsync()
    {
        if (factory.UsesPostgreSql)
            return;

        await SeedLock.WaitAsync();
        try
        {
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            if (await db.Roles.AnyAsync())
                return;

            var model = ((IInfrastructure<IServiceProvider>)db).Instance.GetRequiredService<IDesignTimeModel>().Model;
            foreach (var entityType in model.GetEntityTypes().Where(e => e.ClrType.Name is "AppContext" or "Role" or "RolePermission"))
            {
                foreach (var row in entityType.GetSeedData())
                {
                    var entity = Activator.CreateInstance(entityType.ClrType)!;
                    // Only the columns: a seed row also carries the navigation lists of the model (shared objects), and adding
                    // them would attach and change the seed of the model itself, which every other host of the process reads.
                    foreach (var (name, value) in row.Where(column => entityType.FindProperty(column.Key) is not null))
                        entityType.ClrType.GetProperty(name)!.SetValue(entity, value);
                    db.Add(entity);
                }
            }

            await db.SaveChangesAsync();
        }
        finally
        {
            SeedLock.Release();
        }
    }
}
