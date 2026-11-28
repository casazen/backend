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

    private async Task<World> NewWorldAsync(bool limited = true)
    {
        await EnsureSeedsAsync();
        var (ownerId, orgId) = await OrgTeamHttp.SeedOwnerWithTeamRowsAsync(factory);
        var collaboratorId = await OrgTeamHttp.AddMemberAsync(factory, orgId, OrgRole.Collaborator, ["short-rent"]);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (granted, hidden, _) = await HostScopeScenario.SeedPropertiesOfAsync(db, orgId, ownerId);
        var memberId = await db.OrgMembers.IgnoreQueryFilters().Where(m => m.UserId == collaboratorId).Select(m => m.Id).SingleAsync();
        // Each property also has a pending request with no payment: pick a stay that was paid, otherwise
        // /api/payments?bookingId= is empty depending on PostgreSQL row order.
        var grantedBooking = await db.Bookings.IgnoreQueryFilters()
            .Where(b => b.PropertyId == granted.Id && db.Payments.Any(p => p.BookingId == b.Id))
            .Select(b => b.Id).FirstAsync();
        var hiddenBooking = await db.Bookings.IgnoreQueryFilters()
            .Where(b => b.PropertyId == hidden.Id && db.Payments.Any(p => p.BookingId == b.Id))
            .Select(b => b.Id).FirstAsync();
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

    [Fact]
    public async Task Today_TheOwnerSeesTheWholeOrg_TheCollaboratorOnlyItsProperty_WithoutTheMoneyItCannotRead()
    {
        var world = await NewWorldAsync();
        var failedBookingId = await AddStayWithAFailedPaymentAsync(world);
        using var owner = Owner(world);
        using var collaborator = Collaborator(world);

        var ownerDay = await JsonAsync(await owner.GetAsync("/api/dashboard/today"));
        var collaboratorDay = await JsonAsync(await collaborator.GetAsync("/api/dashboard/today"));

        Assert.Matches(@"^\d{4}-\d{2}-\d{2}$", ownerDay.GetProperty("today").GetString());
        foreach (var list in new[] { "arrivals", "departures", "upcoming", "approvals", "todo" })
        {
            Assert.True(ownerDay.GetProperty(list).GetProperty("count").GetInt32() >= 0, list);
            Assert.Equal(JsonValueKind.Array, ownerDay.GetProperty(list).GetProperty("items").ValueKind);
        }

        var ownerTodo = ownerDay.GetProperty("todo").GetProperty("items").EnumerateArray().ToList();
        var collaboratorTodo = collaboratorDay.GetProperty("todo").GetProperty("items").EnumerateArray().ToList();

        // These houses have no CIN, so the home does not offer activation. Turnovers to close do not depend on the day.
        Assert.DoesNotContain(ownerTodo, i => i.GetProperty("action").GetString() == "ActivateProperty");
        Assert.DoesNotContain(collaboratorTodo, i => i.GetProperty("action").GetString() == "ActivateProperty");
        Assert.Equal(
            2,
            ownerTodo.Count(i => i.GetProperty("action").GetString() == "ConfirmPropertyReady"));
        Assert.Equal(
            1,
            collaboratorTodo.Count(i => i.GetProperty("action").GetString() == "ConfirmPropertyReady"));

        foreach (var item in ownerTodo)
        {
            Assert.NotEmpty(item.GetProperty("missing").EnumerateArray());
            Assert.All(item.GetProperty("missing").EnumerateArray(), m => Assert.False(string.IsNullOrWhiteSpace(m.GetProperty("code").GetString())));
            Assert.InRange(item.GetProperty("priority").GetInt32(), 1, 8);
            Assert.False(item.TryGetProperty("url", out _) || item.TryGetProperty("route", out _) || item.TryGetProperty("link", out _));
        }

        var priorities = ownerTodo.Select(i => i.GetProperty("priority").GetInt32()).ToList();
        Assert.Equal(priorities.Order(), priorities);

        Assert.Contains(
            ownerTodo,
            i => i.GetProperty("action").GetString() == "ReviewFailedPayment" && i.GetProperty("bookingId").GetGuid() == failedBookingId);
        Assert.DoesNotContain(collaboratorTodo, i => i.GetProperty("action").GetString() == "ReviewFailedPayment");

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

        Assert.True(ownerKpis.GetProperty("revenue").TryGetProperty("amountCents", out _));
        var collected = ownerKpis.GetProperty("collected");
        Assert.Equal("EUR", collected.GetProperty("currency").GetString());
        Assert.Equal(collected.GetProperty("amount").GetDecimal() * 100m, collected.GetProperty("amountCents").GetInt64());
        var direct = ownerKpis.GetProperty("directShare");
        Assert.True(direct.GetProperty("stays").GetInt32() >= direct.GetProperty("directStays").GetInt32());

        var previous = ownerKpis.GetProperty("previous");
        Assert.Equal(30, previous.GetProperty("period").GetProperty("nights").GetInt32());
        Assert.Equal(
            DateOnly.Parse(ownerKpis.GetProperty("period").GetProperty("from").GetString()!).AddDays(-1),
            DateOnly.Parse(previous.GetProperty("period").GetProperty("to").GetString()!));
        Assert.True(previous.TryGetProperty("occupancy", out _));

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

        // A house with no CIN is not an activation the home can open.
        Assert.Equal(0, summary.GetProperty("propertiesPending").GetProperty("count").GetInt32());
        Assert.Equal(0, limited.GetProperty("propertiesPending").GetProperty("count").GetInt32());
    }

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
        Assert.Equal(3, mine.GetProperty("totalCount").GetInt32());
        Assert.All(mine.GetProperty("items").EnumerateArray(), b => Assert.Equal(world.GrantedId, b.GetProperty("propertyId").GetGuid()));
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

    [Fact]
    public async Task Payments_RowsWithTheGuestAndThePropertyAndTheAmountsInCents_StillTheFieldsTheWebAppReads()
    {
        var world = await NewWorldAsync(limited: false);
        using var owner = Owner(world);

        var rows = (await JsonAsync(await owner.GetAsync("/api/payments"))).EnumerateArray().ToList();

        Assert.Equal(4, rows.Count);
        foreach (var row in rows)
        {
            foreach (var field in new[] { "id", "bookingId", "amount", "status", "method", "createdAt", "updatedAt", "refundedAmount", "description" })
                Assert.True(row.TryGetProperty(field, out _), field);
            Assert.Matches(BookingCodeShape, row.GetProperty("bookingCode").GetString());
            Assert.StartsWith("Anna ", row.GetProperty("guestName").GetString());
            Assert.Contains(row.GetProperty("propertyName").GetString(), new[] { "Trullo", "Casa Bianca" });
            Assert.Equal(row.GetProperty("amount").GetDecimal() * 100m, row.GetProperty("amountCents").GetInt64());
            Assert.Equal("EUR", row.GetProperty("currency").GetString());
            Assert.Equal("Completed", row.GetProperty("status").GetString());
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
        Assert.Equal(HttpStatusCode.Forbidden, (await collaborator.GetAsync("/api/payments")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await collaborator.GetAsync($"/api/payments?bookingId={world.GrantedBookingId}")).StatusCode);
    }

    private sealed record Around(
        Guid Upcoming,
        Guid UpcomingHidden,
        Guid ArrivesToday,
        Guid InHouse,
        Guid Completed,
        Guid Cancelled,
        Guid Request,
        Guid LeavesToday,
        Guid SupplierOrgId);

    private async Task<Around> AddStaysAroundTodayAsync(World world)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var today = TimeProvider.System.TodayInRome();
        var guestId = await db.Guests.IgnoreQueryFilters().Where(g => g.OrgId == world.OrgId).Select(g => g.Id).FirstAsync();
        var supplierOrgId = await SupplierOrgIdAsync(world);

        Booking Stay(Guid propertyId, BookingStatus status, BookingSource source, int checkInDay, int nights, decimal total)
        {
            return new Booking
            {
                OrgId = world.OrgId,
                PropertyId = propertyId,
                GuestId = guestId,
                Status = status,
                Source = source,
                CheckInDate = today.AddDays(checkInDay),
                CheckOutDate = today.AddDays(checkInDay + nights),
                NumberOfGuests = 2,
                BasePrice = total,
                TotalPrice = total,
            };
        }

        var upcoming = Stay(world.GrantedId, BookingStatus.Confirmed, BookingSource.Direct, 5, 3, 300m);
        var upcomingHidden = Stay(world.HiddenId, BookingStatus.Confirmed, BookingSource.Direct, 6, 3, 450m);
        var arrivesToday = Stay(world.GrantedId, BookingStatus.Confirmed, BookingSource.Airbnb, 0, 3, 500m);
        var inHouse = Stay(world.GrantedId, BookingStatus.CheckedIn, BookingSource.Manual, -2, 3, 400m);
        var completed = Stay(world.GrantedId, BookingStatus.CheckedOut, BookingSource.BookingCom, -10, 3, 200m);
        var cancelled = Stay(world.GrantedId, BookingStatus.Cancelled, BookingSource.Expedia, 20, 2, 250m);
        var request = Stay(world.GrantedId, BookingStatus.Pending, BookingSource.Direct, 10, 3, 600m);
        request.PaymentOption = PaymentOption.OnSite;
        request.GuestEmailVerifiedAt = DateTime.UtcNow;
        request.RequestExpiresAt = DateTime.UtcNow.AddHours(20);
        var leavesToday = Stay(world.GrantedId, BookingStatus.CheckedIn, BookingSource.Direct, -3, 3, 350m);
        db.Bookings.AddRange(upcoming, upcomingHidden, arrivesToday, inHouse, completed, cancelled, request, leavesToday);
        db.Payments.Add(new Payment
        {
            OrgId = world.OrgId,
            BookingId = arrivesToday.Id,
            Status = PaymentStatus.Completed,
            Amount = 150m,
            TransactionId = $"tx-{Guid.NewGuid():N}",
            ProcessedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        return new Around(
            upcoming.Id, upcomingHidden.Id, arrivesToday.Id, inHouse.Id, completed.Id, cancelled.Id, request.Id, leavesToday.Id, supplierOrgId);
    }

    private async Task<Guid> SupplierOrgIdAsync(World world)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.ServiceRequests.IgnoreQueryFilters()
            .Where(r => r.OrgId == world.OrgId).Select(r => r.SupplierOrgId).FirstAsync();
    }

    private async Task<Guid> AddServiceRequestAsync(
        World world,
        Guid supplierOrgId,
        Guid propertyId,
        ServiceRequestStatus status,
        DateTime? start = null,
        Guid? bookingId = null,
        bool proposal = false,
        bool amountToConfirm = false)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var request = new ServiceRequest
        {
            OrgId = world.OrgId,
            PropertyId = propertyId,
            BookingId = bookingId,
            SupplierOrgId = supplierOrgId,
            RentalContext = ServiceRequestRentalContext.ShortRent,
            Category = "cleaning",
            Status = status,
            ScheduledStartUtc = start,
            ScheduledEndUtc = start?.AddHours(2),
            FinalAmountNeedsConfirmation = amountToConfirm,
        };
        if (proposal)
        {
            request.ProposedStartUtc = DateTime.UtcNow.AddDays(3);
            request.ProposedEndUtc = DateTime.UtcNow.AddDays(3).AddHours(2);
            request.ProposedAt = DateTime.UtcNow;
        }

        db.ServiceRequests.Add(request);
        await db.SaveChangesAsync();
        return request.Id;
    }

    [Fact]
    public async Task Overview_TheOwnerGetsTheMoney_TheCollaboratorDoesNot_EverythingElseIsThereForBoth()
    {
        var world = await NewWorldAsync();
        await AddStaysAroundTodayAsync(world);
        using var owner = Owner(world);
        using var collaborator = Collaborator(world);

        var ownerHome = await JsonAsync(await owner.GetAsync("/api/dashboard/overview?period=Next30Days"));
        var collaboratorHome = await JsonAsync(await collaborator.GetAsync("/api/dashboard/overview?period=Next30Days"));

        Assert.Equal("Next30Days", ownerHome.GetProperty("period").GetProperty("kind").GetString());
        Assert.Equal(30, ownerHome.GetProperty("period").GetProperty("nights").GetInt32());
        Assert.Equal(30, ownerHome.GetProperty("previous").GetProperty("period").GetProperty("nights").GetInt32());
        Assert.Equal(
            DateOnly.Parse(ownerHome.GetProperty("period").GetProperty("from").GetString()!).AddDays(-1),
            DateOnly.Parse(ownerHome.GetProperty("previous").GetProperty("period").GetProperty("to").GetString()!));
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}$", ownerHome.GetProperty("today").GetString());
        Assert.Equal(2, ownerHome.GetProperty("propertyCount").GetInt32());
        Assert.Equal(1, collaboratorHome.GetProperty("propertyCount").GetInt32());

        var revenue = ownerHome.GetProperty("revenue");
        Assert.Equal("accrual", revenue.GetProperty("basis").GetString());
        Assert.Equal("EUR", revenue.GetProperty("currency").GetString());
        Assert.Equal(revenue.GetProperty("amount").GetDecimal() * 100m, revenue.GetProperty("amountCents").GetInt64());
        Assert.True(revenue.TryGetProperty("deltaPercent", out _));
        Assert.Equal("cash", ownerHome.GetProperty("collected").GetProperty("basis").GetString());
        Assert.True(ownerHome.GetProperty("occupancy").TryGetProperty("deltaPoints", out _));

        var series = ownerHome.GetProperty("series");
        Assert.Equal("week", series.GetProperty("unit").GetString());
        var buckets = series.GetProperty("buckets").EnumerateArray().ToList();
        Assert.NotEmpty(buckets);
        Assert.Equal(revenue.GetProperty("amount").GetDecimal(), buckets.Sum(b => b.GetProperty("amount").GetDecimal()));
        Assert.Equal(
            ownerHome.GetProperty("previous").GetProperty("revenue").GetProperty("amount").GetDecimal(),
            buckets.Sum(b => b.GetProperty("previousAmount").GetDecimal()));
        Assert.All(buckets, b => Assert.Matches(@"^\d{4}-W\d{2}$", b.GetProperty("label").GetString()));

        foreach (var money in new[] { "revenue", "collected", "series" })
            Assert.False(collaboratorHome.TryGetProperty(money, out _), money);
        Assert.False(collaboratorHome.GetProperty("previous").TryGetProperty("revenue", out _));
        Assert.False(collaboratorHome.GetProperty("previous").TryGetProperty("collected", out _));
        Assert.True(collaboratorHome.GetProperty("occupancy").TryGetProperty("rate", out _));
        Assert.True(collaboratorHome.GetProperty("upcoming").TryGetProperty("pendingApproval", out _));
        Assert.True(collaboratorHome.GetProperty("directShare").TryGetProperty("stays", out _));
    }

    [Fact]
    public async Task Overview_TheStaysAroundToday_AreCounted_TheCollaboratorSeesThoseOfItsPropertyOnly()
    {
        var world = await NewWorldAsync();
        using var owner = Owner(world);
        using var collaborator = Collaborator(world);
        var ownerBefore = await JsonAsync(await owner.GetAsync("/api/dashboard/overview?period=Next30Days"));
        var collaboratorBefore = await JsonAsync(await collaborator.GetAsync("/api/dashboard/overview?period=Next30Days"));

        var around = await AddStaysAroundTodayAsync(world);
        var ownerAfter = await JsonAsync(await owner.GetAsync("/api/dashboard/overview?period=Next30Days"));
        var collaboratorAfter = await JsonAsync(await collaborator.GetAsync("/api/dashboard/overview?period=Next30Days"));

        static int Upcoming(JsonElement home, string field) => home.GetProperty("upcoming").GetProperty(field).GetInt32();
        Assert.Equal(4, Upcoming(ownerAfter, "count") - Upcoming(ownerBefore, "count"));
        Assert.Equal(1, Upcoming(ownerAfter, "pendingApproval") - Upcoming(ownerBefore, "pendingApproval"));
        Assert.Equal(3, Upcoming(collaboratorAfter, "count") - Upcoming(collaboratorBefore, "count"));
        Assert.Equal(1, Upcoming(collaboratorAfter, "pendingApproval") - Upcoming(collaboratorBefore, "pendingApproval"));

        var ownerGain = ownerAfter.GetProperty("revenue").GetProperty("amount").GetDecimal()
            - ownerBefore.GetProperty("revenue").GetProperty("amount").GetDecimal();
        Assert.InRange(ownerGain, 1383.32m, 1383.34m);
        Assert.Equal(
            ownerAfter.GetProperty("revenue").GetProperty("amount").GetDecimal(),
            ownerAfter.GetProperty("series").GetProperty("buckets").EnumerateArray().Sum(b => b.GetProperty("amount").GetDecimal()));

        var next = ownerAfter.GetProperty("nextArrivals").EnumerateArray().ToList();
        Assert.Equal(3, next.Count);
        Assert.Equal(around.ArrivesToday, next[0].GetProperty("bookingId").GetGuid());
        var days = next.Select(n => n.GetProperty("checkInDate").GetString()).ToList();
        Assert.Equal(days.Order(), days);
        Assert.All(next, n => Assert.Matches(BookingCodeShape, n.GetProperty("bookingCode").GetString()));
        Assert.DoesNotContain(
            collaboratorAfter.GetProperty("nextArrivals").EnumerateArray(),
            n => n.GetProperty("bookingId").GetGuid() == around.UpcomingHidden);
        Assert.DoesNotContain(world.HiddenId.ToString(), collaboratorAfter.GetRawText(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Overview_AFreePeriod_NeedsBothDaysInOrder_AtMost366_AndKpisStillDoesNotKnowIt()
    {
        var world = await NewWorldAsync(limited: false);
        using var owner = Owner(world);

        var custom = await JsonAsync(await owner.GetAsync("/api/dashboard/overview?period=Custom&from=2026-10-01&to=2026-10-31"));

        var period = custom.GetProperty("period");
        Assert.Equal("Custom", period.GetProperty("kind").GetString());
        Assert.Equal("2026-10-01", period.GetProperty("from").GetString());
        Assert.Equal("2026-10-31", period.GetProperty("to").GetString());
        Assert.Equal(31, period.GetProperty("nights").GetInt32());
        Assert.Equal(31, custom.GetProperty("previous").GetProperty("period").GetProperty("nights").GetInt32());
        Assert.Equal(1200m, custom.GetProperty("revenue").GetProperty("amount").GetDecimal());
        var year = await JsonAsync(await owner.GetAsync("/api/dashboard/overview?period=Custom&from=2026-01-01&to=2026-12-31"));
        Assert.Equal("month", year.GetProperty("series").GetProperty("unit").GetString());
        Assert.Equal(12, year.GetProperty("series").GetProperty("buckets").GetArrayLength());

        foreach (var query in new[]
                 {
                     "period=Custom",
                     "period=Custom&from=2026-10-01",
                     "period=Custom&from=2026-10-31&to=2026-10-01",
                     "period=Custom&from=2026-01-01&to=2027-01-02",
                     "period=Custom&month=2026-10&from=2026-10-01&to=2026-10-31",
                     "period=Month&from=2026-10-01&to=2026-10-31",
                     "period=Next60Days",
                     "period=3",
                     "period=Last30Days,Next30Days&from=2026-10-01&to=2026-10-31",
                     "period=Month,Last30Days",
                 })
        {
            await AssertProblemAsync(await owner.GetAsync($"/api/dashboard/overview?{query}"), HttpStatusCode.BadRequest, "dashboard_invalid_period");
        }

        await AssertProblemAsync(
            await owner.GetAsync("/api/dashboard/kpis?period=Custom&from=2026-10-01&to=2026-10-31"), HttpStatusCode.BadRequest, "dashboard_invalid_period");
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync("/api/dashboard/kpis?period=Next30Days")).StatusCode);
    }

    [Fact]
    public async Task Overview_OneProperty_NarrowsEverything_AndOnlyOneTheCallerReaches()
    {
        var world = await NewWorldAsync();
        await AddStaysAroundTodayAsync(world);
        using var owner = Owner(world);
        using var collaborator = Collaborator(world);

        var one = await JsonAsync(await owner.GetAsync($"/api/dashboard/overview?period=Next30Days&propertyId={world.HiddenId}"));

        Assert.Equal(1, one.GetProperty("propertyCount").GetInt32());
        Assert.NotEmpty(one.GetProperty("nextArrivals").EnumerateArray());
        Assert.All(one.GetProperty("nextArrivals").EnumerateArray(), n => Assert.Equal(world.HiddenId, n.GetProperty("propertyId").GetGuid()));
        await AssertProblemAsync(
            await collaborator.GetAsync($"/api/dashboard/overview?propertyId={world.HiddenId}"), HttpStatusCode.NotFound, "not_found");
        await AssertProblemAsync(await owner.GetAsync($"/api/dashboard/overview?propertyId={Guid.NewGuid()}"), HttpStatusCode.NotFound, "not_found");
        Assert.Equal(HttpStatusCode.OK, (await collaborator.GetAsync($"/api/dashboard/overview?propertyId={world.GrantedId}")).StatusCode);
    }

    [Fact]
    public async Task Overview_ASupplierWithoutHostPermissions_IsForbidden()
    {
        using var supplier = factory.CreateAuthenticatedClient($"auth0|sr03b-supplier-{Guid.NewGuid():N}", roles: "Supplier");

        Assert.Equal(HttpStatusCode.Forbidden, (await supplier.GetAsync("/api/dashboard/overview")).StatusCode);
    }

    [Fact]
    public async Task Today_TheInterventionsOfTheDay_TheAlloggiatiOfEachArrival_TheCleaningOfEachDeparture()
    {
        var world = await NewWorldAsync();
        var around = await AddStaysAroundTodayAsync(world);
        var morning = RomeCalendar.ToUtc(TimeProvider.System.TodayInRomeAsDateOnly(), new TimeOnly(10, 0));
        var today = await AddServiceRequestAsync(world, around.SupplierOrgId, world.GrantedId, ServiceRequestStatus.PresoInCarico, morning);
        var hiddenToday = await AddServiceRequestAsync(world, around.SupplierOrgId, world.HiddenId, ServiceRequestStatus.PresoInCarico, morning);
        var cleaning = await AddServiceRequestAsync(
            world, around.SupplierOrgId, world.GrantedId, ServiceRequestStatus.Richiesto, bookingId: around.LeavesToday);
        using var owner = Owner(world);
        using var collaborator = Collaborator(world);

        var ownerDay = await JsonAsync(await owner.GetAsync("/api/dashboard/today"));
        var collaboratorDay = await JsonAsync(await collaborator.GetAsync("/api/dashboard/today"));

        var services = ownerDay.GetProperty("services");
        Assert.True(services.GetProperty("count").GetInt32() >= 3);
        var ids = services.GetProperty("items").EnumerateArray().Select(s => s.GetProperty("id").GetGuid()).ToList();
        Assert.Contains(today, ids);
        Assert.Contains(hiddenToday, ids);
        Assert.Contains(cleaning, ids);
        var item = services.GetProperty("items").EnumerateArray().First(s => s.GetProperty("id").GetGuid() == today);
        Assert.Equal("cleaning", item.GetProperty("category").GetString());
        Assert.Equal("PresoInCarico", item.GetProperty("status").GetString());
        Assert.Equal("Trullo", item.GetProperty("propertyName").GetString());
        Assert.False(string.IsNullOrWhiteSpace(item.GetProperty("supplierName").GetString()));
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}T", item.GetProperty("scheduledStart").GetString());
        var mine = collaboratorDay.GetProperty("services").GetProperty("items").EnumerateArray().Select(s => s.GetProperty("id").GetGuid()).ToList();
        Assert.Contains(today, mine);
        Assert.DoesNotContain(hiddenToday, mine);

        // An arrival of today says where its Alloggiati communication stands, and its term.
        var arrival = ownerDay.GetProperty("arrivals").GetProperty("items").EnumerateArray()
            .First(s => s.GetProperty("bookingId").GetGuid() == around.ArrivesToday);
        Assert.Equal("DaInviareManualmente", arrival.GetProperty("alloggiati").GetProperty("status").GetString());
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}T", arrival.GetProperty("alloggiati").GetProperty("deadlineAt").GetString());
        Assert.NotEqual(JsonValueKind.Null, arrival.GetProperty("checkIn").ValueKind);
        Assert.Equal(JsonValueKind.Null, arrival.GetProperty("cleaning").ValueKind);

        // A departure of today says where its cleaning stands; it has no check-in and no Alloggiati.
        var departure = ownerDay.GetProperty("departures").GetProperty("items").EnumerateArray()
            .First(s => s.GetProperty("bookingId").GetGuid() == around.LeavesToday);
        Assert.Equal(cleaning, departure.GetProperty("cleaning").GetProperty("serviceRequestId").GetGuid());
        Assert.Equal("Richiesto", departure.GetProperty("cleaning").GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, departure.GetProperty("checkIn").ValueKind);
        Assert.Equal(JsonValueKind.Null, departure.GetProperty("alloggiati").ValueKind);
    }

    [Fact]
    public async Task Today_TheThingsToDoAboutAnIntervention_EachWithItsRequest_ForWhoReachesTheProperty()
    {
        var world = await NewWorldAsync();
        var supplierOrgId = await SupplierOrgIdAsync(world);
        var proposal = await AddServiceRequestAsync(world, supplierOrgId, world.GrantedId, ServiceRequestStatus.Richiesto, proposal: true);
        var amount = await AddServiceRequestAsync(world, supplierOrgId, world.GrantedId, ServiceRequestStatus.Completato, amountToConfirm: true);
        var toPay = await AddServiceRequestAsync(world, supplierOrgId, world.HiddenId, ServiceRequestStatus.Completato);
        using var owner = Owner(world);
        using var collaborator = Collaborator(world);

        var ownerTodo = (await JsonAsync(await owner.GetAsync("/api/dashboard/today"))).GetProperty("todo").GetProperty("items").EnumerateArray().ToList();
        var collaboratorTodo = (await JsonAsync(await collaborator.GetAsync("/api/dashboard/today"))).GetProperty("todo").GetProperty("items").EnumerateArray().ToList();

        static JsonElement Item(IEnumerable<JsonElement> list, string action) =>
            list.Single(i => i.GetProperty("action").GetString() == action && i.GetProperty("serviceRequestId").ValueKind == JsonValueKind.String);
        var respond = Item(ownerTodo, "RespondToServiceRequest");
        var confirm = Item(ownerTodo, "ConfirmFinalAmount");
        var pay = Item(ownerTodo.Where(i => i.GetProperty("propertyId").GetGuid() == world.HiddenId), "PayServiceRequest");
        Assert.Equal(proposal, respond.GetProperty("serviceRequestId").GetGuid());
        Assert.Equal(amount, confirm.GetProperty("serviceRequestId").GetGuid());
        Assert.Equal(toPay, pay.GetProperty("serviceRequestId").GetGuid());
        Assert.Equal((1, 5, 6), (respond.GetProperty("priority").GetInt32(), confirm.GetProperty("priority").GetInt32(), pay.GetProperty("priority").GetInt32()));
        Assert.Contains(respond.GetProperty("missing").EnumerateArray(), m => m.GetProperty("code").GetString() == "proposal_not_answered");
        Assert.Contains(confirm.GetProperty("missing").EnumerateArray(), m => m.GetProperty("code").GetString() == "final_amount_not_confirmed");
        Assert.Contains(pay.GetProperty("missing").EnumerateArray(), m => m.GetProperty("code").GetString() == "service_not_paid");
        Assert.False(respond.TryGetProperty("url", out _) || respond.TryGetProperty("route", out _));
        Assert.False(string.IsNullOrWhiteSpace(respond.GetProperty("label").GetString()));
        var priorities = ownerTodo.Select(i => i.GetProperty("priority").GetInt32()).ToList();
        Assert.Equal(priorities.Order(), priorities);

        var reachable = collaboratorTodo.Where(i => i.GetProperty("serviceRequestId").ValueKind == JsonValueKind.String)
            .Select(i => i.GetProperty("serviceRequestId").GetGuid()).ToList();
        Assert.Contains(proposal, reachable);
        Assert.Contains(amount, reachable);
        Assert.DoesNotContain(toPay, reachable);
    }

    private static readonly string[] Chips = ["all", "toConfirm", "upcoming", "inProgress", "completed", "cancelled"];

    private static int Count(JsonElement counts, string chip) => counts.GetProperty(chip).GetInt32();

    [Fact]
    public async Task BookingCounts_EachChipCountsItsBookings_TheCollaboratorItsPropertysOnly()
    {
        var world = await NewWorldAsync();
        using var owner = Owner(world);
        using var collaborator = Collaborator(world);
        var ownerBefore = await JsonAsync(await owner.GetAsync("/api/bookings/counts"));
        var collaboratorBefore = await JsonAsync(await collaborator.GetAsync("/api/bookings/counts"));

        await AddStaysAroundTodayAsync(world);
        var ownerAfter = await JsonAsync(await owner.GetAsync("/api/bookings/counts"));
        var collaboratorAfter = await JsonAsync(await collaborator.GetAsync("/api/bookings/counts"));

        var gained = Chips.Select(chip => Count(ownerAfter, chip) - Count(ownerBefore, chip)).ToArray();
        Assert.Equal(new[] { 8, 1, 2, 3, 1, 1 }, gained);
        var collaboratorGained = Chips.Select(chip => Count(collaboratorAfter, chip) - Count(collaboratorBefore, chip)).ToArray();
        Assert.Equal(new[] { 7, 1, 1, 3, 1, 1 }, collaboratorGained);
        var search = await JsonAsync(await owner.GetAsync("/api/bookings/search?pageSize=100"));
        Assert.Equal(Count(ownerAfter, "all"), search.GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task BookingSearch_EachChipIsItsBookings_AndTheCounterOfTheChipIsTheTotalOfTheList()
    {
        var world = await NewWorldAsync();
        var around = await AddStaysAroundTodayAsync(world);
        using var owner = Owner(world);
        var counts = await JsonAsync(await owner.GetAsync("/api/bookings/counts"));

        async Task<List<Guid>> IdsOfAsync(string view)
        {
            var page = await JsonAsync(await owner.GetAsync($"/api/bookings/search?view={view}&pageSize=100"));
            Assert.Equal(Count(counts, view), page.GetProperty("totalCount").GetInt32());
            return page.GetProperty("items").EnumerateArray().Select(b => b.GetProperty("id").GetGuid()).ToList();
        }

        var toConfirm = await IdsOfAsync("toConfirm");
        var upcoming = await IdsOfAsync("upcoming");
        var inProgress = await IdsOfAsync("inProgress");
        var completed = await IdsOfAsync("completed");
        var cancelled = await IdsOfAsync("cancelled");

        Assert.Contains(around.Request, toConfirm);
        Assert.Contains(around.Upcoming, upcoming);
        Assert.Contains(around.UpcomingHidden, upcoming);
        Assert.Contains(around.ArrivesToday, inProgress);
        Assert.Contains(around.InHouse, inProgress);
        Assert.Contains(around.LeavesToday, inProgress);
        Assert.Contains(around.Completed, completed);
        Assert.Contains(around.Cancelled, cancelled);
        var all = new[] { toConfirm, upcoming, inProgress, completed, cancelled }.SelectMany(ids => ids).ToList();
        Assert.Equal(all.Count, all.Distinct().Count());
        var airbnbInProgress = await JsonAsync(await owner.GetAsync("/api/bookings/search?view=inProgress&channel=Airbnb"));
        Assert.Equal(around.ArrivesToday, Assert.Single(airbnbInProgress.GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid());
        var airbnbCounts = await JsonAsync(await owner.GetAsync("/api/bookings/counts?channel=Airbnb"));
        Assert.Equal((1, 1), (Count(airbnbCounts, "all"), Count(airbnbCounts, "inProgress")));
    }

    [Fact]
    public async Task BookingSearch_ArrivalDays_Channels_AndTheOrder()
    {
        var world = await NewWorldAsync(limited: false);
        var around = await AddStaysAroundTodayAsync(world);
        using var owner = Owner(world);
        var today = TimeProvider.System.TodayInRomeAsDateOnly();

        var arriving = await JsonAsync(await owner.GetAsync(
            $"/api/bookings/search?arrivalFrom={today:yyyy-MM-dd}&arrivalTo={today.AddDays(6):yyyy-MM-dd}&sort=checkIn:asc&pageSize=100"));
        var others = await JsonAsync(await owner.GetAsync("/api/bookings/search?channel=Other&channel=Manual&channel=Expedia&pageSize=100"));
        var withoutExpedia = await JsonAsync(await owner.GetAsync("/api/bookings/search?channel=Other&channel=Manual&pageSize=100"));
        var byTotal = await JsonAsync(await owner.GetAsync("/api/bookings/search?sort=total:desc&pageSize=100"));
        var byGuest = await JsonAsync(await owner.GetAsync("/api/bookings/search?sort=guest&pageSize=100"));

        var arrivingIds = arriving.GetProperty("items").EnumerateArray().Select(b => b.GetProperty("id").GetGuid()).ToList();
        var expected = new[] { around.ArrivesToday, around.Upcoming, around.UpcomingHidden };
        Assert.Equal(expected, arrivingIds.Where(expected.Contains));
        var checkIns = arriving.GetProperty("items").EnumerateArray().Select(b => b.GetProperty("checkInDate").GetDateTime()).ToList();
        Assert.Equal(checkIns.Order(), checkIns);
        Assert.All(checkIns, day => Assert.InRange(DateOnly.FromDateTime(day), today, today.AddDays(6)));
        Assert.DoesNotContain(around.InHouse, arrivingIds);
        Assert.DoesNotContain(around.Request, arrivingIds);
        var otherIds = others.GetProperty("items").EnumerateArray().Select(b => b.GetProperty("id").GetGuid()).ToList();
        Assert.Contains(around.InHouse, otherIds);
        Assert.Contains(around.Cancelled, otherIds);
        Assert.DoesNotContain(around.ArrivesToday, otherIds);
        Assert.DoesNotContain(around.Completed, otherIds);
        Assert.All(others.GetProperty("items").EnumerateArray(), b => Assert.Contains(b.GetProperty("channel").GetString(), new[] { "Manual", "Other", "Expedia" }));
        var withoutExpediaIds = withoutExpedia.GetProperty("items").EnumerateArray().Select(b => b.GetProperty("id").GetGuid()).ToList();
        Assert.Contains(around.InHouse, withoutExpediaIds);
        Assert.DoesNotContain(around.Cancelled, withoutExpediaIds);
        var totals = byTotal.GetProperty("items").EnumerateArray().Select(b => b.GetProperty("totalPrice").GetDecimal()).ToList();
        Assert.Equal(totals.OrderByDescending(t => t), totals);
        var lastNames = byGuest.GetProperty("items").EnumerateArray().Select(b => b.GetProperty("guest").GetProperty("lastName").GetString()!.ToLowerInvariant()).ToList();
        Assert.Equal(lastNames.Order(StringComparer.Ordinal), lastNames);
    }

    [Fact]
    public async Task BookingSearch_EachRowSaysTheChannel_HowMuchWasPaid_AndTheAlloggiati_TheMoneyOnlyForWhoMayReadIt()
    {
        var world = await NewWorldAsync();
        var around = await AddStaysAroundTodayAsync(world);
        using var owner = Owner(world);
        using var collaborator = Collaborator(world);

        var ownerRows = (await JsonAsync(await owner.GetAsync("/api/bookings/search?view=inProgress&pageSize=100")))
            .GetProperty("items").EnumerateArray().ToDictionary(b => b.GetProperty("id").GetGuid());
        var collaboratorRows = (await JsonAsync(await collaborator.GetAsync("/api/bookings/search?view=inProgress&pageSize=100")))
            .GetProperty("items").EnumerateArray().ToDictionary(b => b.GetProperty("id").GetGuid());

        var arrives = ownerRows[around.ArrivesToday];
        Assert.Equal("Airbnb", arrives.GetProperty("source").GetString());
        Assert.Equal("Airbnb", arrives.GetProperty("channel").GetString());
        Assert.Equal("Partial", arrives.GetProperty("paymentState").GetString());
        Assert.Equal(150m, arrives.GetProperty("paidAmount").GetDecimal());
        Assert.Equal("DaInviareManualmente", arrives.GetProperty("alloggiatiStatus").GetString());
        Assert.Equal(JsonValueKind.Null, arrives.GetProperty("respondBy").ValueKind);
        var inHouse = ownerRows[around.InHouse];
        Assert.Equal("Manual", inHouse.GetProperty("channel").GetString());
        Assert.Equal("None", inHouse.GetProperty("paymentState").GetString());
        Assert.Equal(0m, inHouse.GetProperty("paidAmount").GetDecimal());
        var same = collaboratorRows[around.ArrivesToday];
        Assert.Equal("Partial", same.GetProperty("paymentState").GetString());
        Assert.Equal(JsonValueKind.Null, same.GetProperty("paidAmount").ValueKind);
        // The request waiting for the host says until when it may be answered, and has no Alloggiati communication.
        var requests = await JsonAsync(await owner.GetAsync("/api/bookings/search?view=toConfirm&pageSize=100"));
        var request = requests.GetProperty("items").EnumerateArray().Single(b => b.GetProperty("id").GetGuid() == around.Request);
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}T", request.GetProperty("respondBy").GetString());
        Assert.Equal("AwaitingHostApproval", request.GetProperty("onSiteRequestState").GetString());
        Assert.Equal(JsonValueKind.Null, request.GetProperty("alloggiatiStatus").ValueKind);
    }

    [Fact]
    public async Task BookingSearch_TheNewWordsThatAreNotUnderstood_Are400WithTheirCodes()
    {
        var world = await NewWorldAsync(limited: false);
        using var owner = Owner(world);

        await AssertProblemAsync(await owner.GetAsync("/api/bookings/search?view=done"), HttpStatusCode.BadRequest, "booking_list_invalid_view");
        await AssertProblemAsync(await owner.GetAsync("/api/bookings/search?channel=Foo"), HttpStatusCode.BadRequest, "booking_list_invalid_channel");
        await AssertProblemAsync(
            await owner.GetAsync("/api/bookings/search?checkInStatus=Foo"), HttpStatusCode.BadRequest, "booking_list_invalid_check_in_status");
        await AssertProblemAsync(await owner.GetAsync("/api/bookings/search?sort=price"), HttpStatusCode.BadRequest, "booking_list_invalid_sort");
        await AssertProblemAsync(
            await owner.GetAsync("/api/bookings/search?arrivalFrom=2026-11-01&arrivalTo=2026-10-01"), HttpStatusCode.BadRequest, "booking_list_invalid_range");
        await AssertProblemAsync(await owner.GetAsync("/api/bookings/counts?channel=Foo"), HttpStatusCode.BadRequest, "booking_list_invalid_channel");
        await AssertProblemAsync(await owner.GetAsync("/api/bookings/counts?status=Done"), HttpStatusCode.BadRequest, "booking_list_invalid_status");
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync("/api/bookings/counts?view=upcoming&sort=total&page=3")).StatusCode);
    }

    [Fact]
    public async Task TheLastDayTheCalendarHas_IsNoUpperBound_OnTheBookingListAndOnThePayments()
    {
        var world = await NewWorldAsync(limited: false);
        using var owner = Owner(world);

        foreach (var url in new[]
                 {
                     "/api/bookings/search?arrivalTo=9999-12-31",
                     "/api/bookings/search?to=9999-12-31",
                     "/api/bookings/counts?arrivalTo=9999-12-31",
                     "/api/payments?to=9999-12-31",
                     "/api/payments?to=9999-12-31&page=1",
                 })
        {
            var response = await owner.GetAsync(url);
            Assert.True(response.StatusCode == HttpStatusCode.OK, $"{url}: {(int)response.StatusCode}");
        }
    }

    [Fact]
    public async Task BookingSearch_ThePropertyTheCollaboratorWasNotGiven_IsOutOfTheCountersToo()
    {
        var world = await NewWorldAsync();
        using var collaborator = Collaborator(world);

        Assert.Equal(HttpStatusCode.Forbidden, (await collaborator.GetAsync($"/api/bookings/counts?propertyId={world.HiddenId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await collaborator.GetAsync($"/api/bookings/counts?propertyId={Guid.NewGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await collaborator.GetAsync($"/api/bookings/counts?propertyId={world.GrantedId}")).StatusCode);
    }

    [Fact]
    public async Task Bookings_ThePlainList_IsTheSearchWithoutFilters_SameBookingsSameOrderSameRows()
    {
        var world = await NewWorldAsync();
        await AddStaysAroundTodayAsync(world);
        using var owner = Owner(world);
        using var collaborator = Collaborator(world);

        foreach (var client in new[] { owner, collaborator })
        {
            var plain = await JsonAsync(await client.GetAsync("/api/bookings?pageSize=100"));
            var search = await JsonAsync(await client.GetAsync("/api/bookings/search?pageSize=100"));

            Assert.Equal(
                search.GetProperty("items").EnumerateArray().Select(b => b.GetProperty("id").GetGuid()),
                plain.GetProperty("items").EnumerateArray().Select(b => b.GetProperty("id").GetGuid()));
            Assert.Equal(search.GetProperty("totalCount").GetInt32(), plain.GetProperty("totalCount").GetInt32());
            foreach (var row in plain.GetProperty("items").EnumerateArray())
            {
                Assert.True(row.TryGetProperty("channel", out _));
                Assert.True(row.TryGetProperty("paymentState", out _));
                Assert.True(row.TryGetProperty("alloggiatiStatus", out _));
            }
        }

        var mine = await JsonAsync(await collaborator.GetAsync($"/api/bookings?propertyId={world.GrantedId}&pageSize=2&page=2"));
        Assert.Equal((2, 2), (mine.GetProperty("page").GetInt32(), mine.GetProperty("pageSize").GetInt32()));
        Assert.Equal(2, mine.GetProperty("items").GetArrayLength());
        Assert.All(mine.GetProperty("items").EnumerateArray(), b => Assert.Equal(world.GrantedId, b.GetProperty("propertyId").GetGuid()));
    }

    [Fact]
    public async Task Payments_WithoutAPage_TheArrayOfAlways_WithAPage_AnObjectLikeTheBookings()
    {
        var world = await NewWorldAsync(limited: false);
        using var owner = Owner(world);

        var array = await JsonAsync(await owner.GetAsync("/api/payments"));
        var first = await JsonAsync(await owner.GetAsync("/api/payments?page=1&pageSize=3"));
        var second = await JsonAsync(await owner.GetAsync("/api/payments?page=2&pageSize=3"));
        var bySize = await JsonAsync(await owner.GetAsync("/api/payments?pageSize=1000"));
        var byPage = await JsonAsync(await owner.GetAsync("/api/payments?page=1"));

        Assert.Equal(JsonValueKind.Array, array.ValueKind);
        Assert.Equal(4, array.GetArrayLength());
        Assert.Equal(JsonValueKind.Object, first.ValueKind);
        Assert.Equal((4, 1, 3, 3), (first.GetProperty("totalCount").GetInt32(), first.GetProperty("page").GetInt32(), first.GetProperty("pageSize").GetInt32(), first.GetProperty("items").GetArrayLength()));
        Assert.Equal((4, 2, 3, 1), (second.GetProperty("totalCount").GetInt32(), second.GetProperty("page").GetInt32(), second.GetProperty("pageSize").GetInt32(), second.GetProperty("items").GetArrayLength()));
        Assert.Equal(
            array.EnumerateArray().Select(p => p.GetProperty("id").GetGuid()),
            first.GetProperty("items").EnumerateArray().Concat(second.GetProperty("items").EnumerateArray()).Select(p => p.GetProperty("id").GetGuid()));
        Assert.Equal(100, bySize.GetProperty("pageSize").GetInt32());
        Assert.Equal(20, byPage.GetProperty("pageSize").GetInt32());
        Assert.Equal(4, bySize.GetProperty("items").GetArrayLength());
        Assert.Equal("EUR", first.GetProperty("items")[0].GetProperty("currency").GetString());
    }

    [Fact]
    public async Task Payments_APage_StaysInsideTheScopeAndThePermission()
    {
        var world = await NewWorldAsync();
        using var owner = Owner(world);
        using var collaborator = Collaborator(world);

        Assert.Equal(HttpStatusCode.Forbidden, (await collaborator.GetAsync("/api/payments?page=1&pageSize=3")).StatusCode);
        var ofProperty = await JsonAsync(await owner.GetAsync($"/api/payments?propertyId={world.GrantedId}&page=1&pageSize=1"));
        Assert.Equal(2, ofProperty.GetProperty("totalCount").GetInt32());
        Assert.Single(ofProperty.GetProperty("items").EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync($"/api/payments?propertyId={Guid.NewGuid()}&page=1")).StatusCode);
        await AssertProblemAsync(
            await owner.GetAsync("/api/payments?from=2026-11-01&to=2026-10-01&page=1"), HttpStatusCode.BadRequest, "payment_list_invalid_range");
    }

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
