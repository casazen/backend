using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// PM-02 (D16) over the real pipeline, with <c>Features:PropertyModeChange</c> on: preview, programming and cancellation of
/// the change of rental mode, in Italian and English; the property of another org is invisible; the calendar closes from the
/// day (public availability, iCal export, the host's own bookings and blocks) when the change is to long-term; the hourly
/// application runs from the real service graph. The same endpoints answer 404 with the flag off
/// (<see cref="PropertyModeFlagOffIntegrationTests"/>).
/// </summary>
public class PropertyModeIntegrationTests : IClassFixture<PropertyModeIntegrationTests.EnabledFactory>
{
    private const string HostRole = "PropertyOwner";

    private readonly EnabledFactory _factory;

    public PropertyModeIntegrationTests(EnabledFactory factory) => _factory = factory;

    // ─── Access ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Endpoints_WithoutAToken_Are401()
    {
        using var anonymous = _factory.CreateClient();
        var id = Guid.NewGuid();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"/api/properties/{id}/mode")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"/api/properties/{id}/mode/preview?to=long")).StatusCode);
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await anonymous.PostAsJsonAsync($"/api/properties/{id}/mode/change", new { to = "Long", effectiveDate = Day(20).ToString("yyyy-MM-dd") })).StatusCode);
        Assert.Equal(
            HttpStatusCode.Unauthorized, (await anonymous.DeleteAsync($"/api/properties/{id}/mode/change/{Guid.NewGuid()}")).StatusCode);
    }

    [Fact]
    public async Task Endpoints_PropertyOfAnotherOrg_Are404WithTheCode()
    {
        var mine = await _factory.SeedPropertyAsync($"auth0|pm02-own-{Guid.NewGuid():N}");
        var theirs = await _factory.SeedPropertyAsync($"auth0|pm02-other-{Guid.NewGuid():N}");
        using var client = _factory.CreateAuthenticatedClient(mine.OwnerId, HostRole);

        foreach (var response in new[]
                 {
                     await client.GetAsync($"/api/properties/{theirs.Id}/mode"),
                     await client.GetAsync($"/api/properties/{theirs.Id}/mode/preview?to=long"),
                     await client.PostAsJsonAsync($"/api/properties/{theirs.Id}/mode/change", new { to = "Long", effectiveDate = Day(20).ToString("yyyy-MM-dd") }),
                     await client.DeleteAsync($"/api/properties/{theirs.Id}/mode/change/{Guid.NewGuid()}"),
                 })
        {
            await AssertProblemAsync(response, HttpStatusCode.NotFound, "property_not_found");
        }

        Assert.False(await AnyChangeAsync(theirs.Id));
    }

    [Fact]
    public async Task Endpoints_ACollaboratorWithoutTheWritePermission_CannotPreviewScheduleOrCancel()
    {
        // The org-wide roles see every property; a role with no host permission does not reach the shared property core.
        var property = await _factory.SeedPropertyAsync($"auth0|pm02-nogo-{Guid.NewGuid():N}");
        using var supplier = _factory.CreateAuthenticatedClient($"auth0|pm02-supplier-{Guid.NewGuid():N}", "Supplier");

        HttpStatusCode[] refused = [HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden, HttpStatusCode.NotFound];
        Assert.Contains((await supplier.GetAsync($"/api/properties/{property.Id}/mode/preview?to=long")).StatusCode, refused);
        Assert.Contains(
            (await supplier.PostAsJsonAsync(
                $"/api/properties/{property.Id}/mode/change", new { to = "Long", effectiveDate = Day(20).ToString("yyyy-MM-dd") })).StatusCode,
            refused);
        Assert.Contains((await supplier.GetAsync($"/api/properties/{property.Id}/mode")).StatusCode, refused);
        Assert.False(await AnyChangeAsync(property.Id));
    }

    // ─── State and preview ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task State_NewProperty_IsShortWithNoChange()
    {
        var property = await _factory.SeedPropertyAsync($"auth0|pm02-state-{Guid.NewGuid():N}");
        using var client = _factory.CreateAuthenticatedClient(property.OwnerId, HostRole);

        var state = await client.GetFromJsonAsync<JsonElement>($"/api/properties/{property.Id}/mode");

        Assert.Equal("Short", state.GetProperty("rentalMode").GetString());
        Assert.Equal(JsonValueKind.Null, state.GetProperty("scheduledChange").ValueKind);
        Assert.Equal(JsonValueKind.Null, state.GetProperty("lastChange").ValueKind);
    }

    [Fact]
    public async Task Preview_ToLongWithAStayToCome_ListsTheStayAndGivesTheFirstFreeDayWithoutGuestData()
    {
        var property = await _factory.SeedPropertyAsync($"auth0|pm02-preview-{Guid.NewGuid():N}");
        var stay = await SeedStayAsync(property, Day(5), Day(12));
        using var client = _factory.CreateAuthenticatedClient(property.OwnerId, HostRole);

        var response = await client.GetAsync($"/api/properties/{property.Id}/mode/preview?to=long&date={Day(8):yyyy-MM-dd}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var raw = await response.Content.ReadAsStringAsync();
        using var body = JsonDocument.Parse(raw);
        var root = body.RootElement;
        Assert.Equal("Short", root.GetProperty("currentMode").GetString());
        Assert.Equal("Long", root.GetProperty("targetMode").GetString());
        Assert.False(root.GetProperty("canSchedule").GetBoolean());
        Assert.Equal(["property_mode_blocked_by_bookings"], root.GetProperty("issues").EnumerateArray().Select(i => i.GetString()));
        Assert.Equal(Day(13).ToString("yyyy-MM-dd"), root.GetProperty("earliestDate").GetString());
        Assert.Equal(Day(8).ToString("yyyy-MM-dd"), root.GetProperty("date").GetString());
        Assert.Equal(Day(8).AddYears(2).ToString("yyyy-MM-dd"), root.GetProperty("calendarClosedUntil").GetString());
        var blocker = Assert.Single(root.GetProperty("blockers").EnumerateArray());
        Assert.Equal(stay.Id, blocker.GetProperty("id").GetGuid());
        Assert.Equal("Stay", blocker.GetProperty("kind").GetString());
        Assert.Equal("Confirmed", blocker.GetProperty("status").GetString());
        Assert.Equal(Day(13).ToString("yyyy-MM-dd"), blocker.GetProperty("freeFrom").GetString());
        // Ids, dates and a status: nothing of the guest.
        Assert.DoesNotContain("Mario", raw);
        Assert.DoesNotContain("@example.com", raw);
        Assert.False(await AnyChangeAsync(property.Id));
    }

    [Fact]
    public async Task Preview_WithoutADate_EvaluatesTheFirstDayPossibleAndCanBeScheduled()
    {
        var property = await _factory.SeedPropertyAsync($"auth0|pm02-nodate-{Guid.NewGuid():N}");
        await SeedStayAsync(property, Day(5), Day(12));
        using var client = _factory.CreateAuthenticatedClient(property.OwnerId, HostRole);

        var root = await client.GetFromJsonAsync<JsonElement>($"/api/properties/{property.Id}/mode/preview?to=Long");

        Assert.True(root.GetProperty("canSchedule").GetBoolean());
        Assert.Equal(Day(13).ToString("yyyy-MM-dd"), root.GetProperty("date").GetString());
        Assert.Empty(root.GetProperty("blockers").EnumerateArray());
    }

    [Theory]
    [InlineData("")]
    [InlineData("?to=sideways")]
    [InlineData("?to=1")]
    public async Task Preview_ATargetThatIsNotAMode_Is400(string query)
    {
        var property = await _factory.SeedPropertyAsync($"auth0|pm02-target-{Guid.NewGuid():N}");
        using var client = _factory.CreateAuthenticatedClient(property.OwnerId, HostRole);

        await AssertProblemAsync(
            await client.GetAsync($"/api/properties/{property.Id}/mode/preview{query}"),
            HttpStatusCode.BadRequest,
            "property_mode_target_invalid");
    }

    [Fact]
    public async Task Preview_ADateThatIsNotADate_Is400()
    {
        var property = await _factory.SeedPropertyAsync($"auth0|pm02-baddate-{Guid.NewGuid():N}");
        using var client = _factory.CreateAuthenticatedClient(property.OwnerId, HostRole);

        var response = await client.GetAsync($"/api/properties/{property.Id}/mode/preview?to=long&date=domani");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Preview_AlreadyInThatMode_Is422()
    {
        var property = await _factory.SeedPropertyAsync($"auth0|pm02-same-{Guid.NewGuid():N}");
        using var client = _factory.CreateAuthenticatedClient(property.OwnerId, HostRole);

        await AssertProblemAsync(
            await client.GetAsync($"/api/properties/{property.Id}/mode/preview?to=short"),
            HttpStatusCode.UnprocessableEntity,
            "property_mode_unchanged");
    }

    // ─── Schedule and cancel ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Schedule_ThenAgain_Then_Cancel_AllTheStepsOfTheRoundTrip()
    {
        var property = await _factory.SeedPropertyAsync($"auth0|pm02-round-{Guid.NewGuid():N}");
        using var client = _factory.CreateAuthenticatedClient(property.OwnerId, HostRole);

        var created = await client.PostAsJsonAsync(
            $"/api/properties/{property.Id}/mode/change", new { to = "Long", effectiveDate = Day(20).ToString("yyyy-MM-dd") });

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var change = await created.Content.ReadFromJsonAsync<JsonElement>();
        var changeId = change.GetProperty("id").GetGuid();
        Assert.Equal("Scheduled", change.GetProperty("status").GetString());
        Assert.Equal("Short", change.GetProperty("fromMode").GetString());
        Assert.Equal("Long", change.GetProperty("toMode").GetString());
        Assert.Equal(Day(20).ToString("yyyy-MM-dd"), change.GetProperty("effectiveDate").GetString());
        Assert.False(change.TryGetProperty("createdByUserId", out _));

        // One at a time.
        await AssertProblemAsync(
            await client.PostAsJsonAsync(
                $"/api/properties/{property.Id}/mode/change", new { to = "Long", effectiveDate = Day(30).ToString("yyyy-MM-dd") }),
            HttpStatusCode.Conflict,
            "property_mode_change_exists");
        var state = await client.GetFromJsonAsync<JsonElement>($"/api/properties/{property.Id}/mode");
        Assert.Equal(changeId, state.GetProperty("scheduledChange").GetProperty("id").GetGuid());
        Assert.Equal("Short", state.GetProperty("rentalMode").GetString());
        var preview = await client.GetFromJsonAsync<JsonElement>($"/api/properties/{property.Id}/mode/preview?to=long&date={Day(40):yyyy-MM-dd}");
        Assert.Equal(changeId, preview.GetProperty("scheduledChange").GetProperty("id").GetGuid());
        Assert.Contains(
            "property_mode_change_exists", preview.GetProperty("issues").EnumerateArray().Select(i => i.GetString()));

        var cancelled = await client.DeleteAsync($"/api/properties/{property.Id}/mode/change/{changeId}");
        Assert.Equal(HttpStatusCode.NoContent, cancelled.StatusCode);
        var after = await client.GetFromJsonAsync<JsonElement>($"/api/properties/{property.Id}/mode");
        Assert.Equal(JsonValueKind.Null, after.GetProperty("scheduledChange").ValueKind);
        Assert.Equal("Cancelled", after.GetProperty("lastChange").GetProperty("status").GetString());
        await AssertProblemAsync(
            await client.DeleteAsync($"/api/properties/{property.Id}/mode/change/{changeId}"),
            HttpStatusCode.Conflict,
            "property_mode_change_not_scheduled");
        await AssertProblemAsync(
            await client.DeleteAsync($"/api/properties/{property.Id}/mode/change/{Guid.NewGuid()}"),
            HttpStatusCode.NotFound,
            "property_mode_change_not_found");

        // Free again: a new one can be programmed.
        Assert.Equal(
            HttpStatusCode.Created,
            (await client.PostAsJsonAsync(
                $"/api/properties/{property.Id}/mode/change", new { to = "Long", effectiveDate = Day(25).ToString("yyyy-MM-dd") })).StatusCode);
    }

    [Theory]
    [InlineData("it-IT")]
    [InlineData("en")]
    public async Task Schedule_BeforeTheLastDeparture_Is409WithTheFirstFreeDayInTheLanguageOfTheRequest(string language)
    {
        var property = await _factory.SeedPropertyAsync($"auth0|pm02-blocked-{language}-{Guid.NewGuid():N}");
        await SeedStayAsync(property, Day(5), Day(15));
        using var client = _factory.CreateAuthenticatedClient(property.OwnerId, HostRole);
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd(language);

        var response = await client.PostAsJsonAsync(
            $"/api/properties/{property.Id}/mode/change", new { to = "Long", effectiveDate = Day(10).ToString("yyyy-MM-dd") });

        var problem = await AssertProblemAsync(response, HttpStatusCode.Conflict, "property_mode_blocked_by_bookings");
        // The first free day is the day after the departure, written as the language of the request writes a date.
        Assert.Contains(
            Day(16).ToString("d MMMM yyyy", CultureInfo.GetCultureInfo(language)), problem.GetProperty("detail").GetString());
        Assert.False(await AnyChangeAsync(property.Id));
    }

    [Fact]
    public async Task Schedule_TodayOrThePast_Is422()
    {
        var property = await _factory.SeedPropertyAsync($"auth0|pm02-early-{Guid.NewGuid():N}");
        using var client = _factory.CreateAuthenticatedClient(property.OwnerId, HostRole);

        foreach (var day in new[] { Day(0), Day(-3) })
        {
            await AssertProblemAsync(
                await client.PostAsJsonAsync(
                    $"/api/properties/{property.Id}/mode/change", new { to = "Long", effectiveDate = day.ToString("yyyy-MM-dd") }),
                HttpStatusCode.UnprocessableEntity,
                "property_mode_date_too_early");
        }

        Assert.False(await AnyChangeAsync(property.Id));
    }

    [Fact]
    public async Task Schedule_InvalidBodies_Are400()
    {
        var property = await _factory.SeedPropertyAsync($"auth0|pm02-body-{Guid.NewGuid():N}");
        using var client = _factory.CreateAuthenticatedClient(property.OwnerId, HostRole);
        var url = $"/api/properties/{property.Id}/mode/change";

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(url, new { effectiveDate = Day(20).ToString("yyyy-MM-dd") })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(url, new { to = "Long" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(url, new { to = "Both", effectiveDate = Day(20).ToString("yyyy-MM-dd") })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(url, new { to = 7, effectiveDate = Day(20).ToString("yyyy-MM-dd") })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(url, new { to = "Long", effectiveDate = "domani" })).StatusCode);
        Assert.False(await AnyChangeAsync(property.Id));
    }

    [Fact]
    public async Task Schedule_AnotherUserOfAnotherOrg_CannotWithdrawTheChange()
    {
        var property = await _factory.SeedPropertyAsync($"auth0|pm02-owner-{Guid.NewGuid():N}");
        var intruderProperty = await _factory.SeedPropertyAsync($"auth0|pm02-intruder-{Guid.NewGuid():N}");
        using var owner = _factory.CreateAuthenticatedClient(property.OwnerId, HostRole);
        var created = await owner.PostAsJsonAsync(
            $"/api/properties/{property.Id}/mode/change", new { to = "Long", effectiveDate = Day(20).ToString("yyyy-MM-dd") });
        var changeId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        using var intruder = _factory.CreateAuthenticatedClient(intruderProperty.OwnerId, HostRole);

        // Through the property of the owner: invisible. Through its own: the change is not there.
        await AssertProblemAsync(
            await intruder.DeleteAsync($"/api/properties/{property.Id}/mode/change/{changeId}"), HttpStatusCode.NotFound, "property_not_found");
        await AssertProblemAsync(
            await intruder.DeleteAsync($"/api/properties/{intruderProperty.Id}/mode/change/{changeId}"),
            HttpStatusCode.NotFound,
            "property_mode_change_not_found");

        var state = await owner.GetFromJsonAsync<JsonElement>($"/api/properties/{property.Id}/mode");
        Assert.Equal(changeId, state.GetProperty("scheduledChange").GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Schedule_ToShortWhileALeaseRuns_Is409UntilTheDayAfterItsEnd()
    {
        var property = await SeedLongPropertyAsync($"auth0|pm02-lease-{Guid.NewGuid():N}");
        await SeedLeaseAsync(property, Day(-100), Day(40), LeaseStatus.Registered);
        using var client = _factory.CreateAuthenticatedClient(property.OwnerId, HostRole);
        var url = $"/api/properties/{property.Id}/mode/change";

        await AssertProblemAsync(
            await client.PostAsJsonAsync(url, new { to = "Short", effectiveDate = Day(40).ToString("yyyy-MM-dd") }),
            HttpStatusCode.Conflict,
            "property_mode_blocked_by_lease");
        var preview = await client.GetFromJsonAsync<JsonElement>($"/api/properties/{property.Id}/mode/preview?to=short");
        Assert.Equal(Day(41).ToString("yyyy-MM-dd"), preview.GetProperty("earliestDate").GetString());

        Assert.Equal(
            HttpStatusCode.Created,
            (await client.PostAsJsonAsync(url, new { to = "Short", effectiveDate = Day(41).ToString("yyyy-MM-dd") })).StatusCode);
        // To short stays nothing closes in the calendar.
        Assert.Equal(0, await ModeChangeBlockCountAsync(property.Id));
    }

    [Fact]
    public async Task Schedule_ToShortWithADraftLease_Is409AndTheDraftIsListed()
    {
        var property = await SeedLongPropertyAsync($"auth0|pm02-draft-{Guid.NewGuid():N}");
        var draft = await SeedLeaseAsync(property, Day(100), Day(465), LeaseStatus.Draft);
        using var client = _factory.CreateAuthenticatedClient(property.OwnerId, HostRole);

        await AssertProblemAsync(
            await client.PostAsJsonAsync(
                $"/api/properties/{property.Id}/mode/change", new { to = "Short", effectiveDate = Day(20).ToString("yyyy-MM-dd") }),
            HttpStatusCode.Conflict,
            "property_mode_blocked_by_draft_lease");
        var preview = await client.GetFromJsonAsync<JsonElement>($"/api/properties/{property.Id}/mode/preview?to=short");
        var listed = Assert.Single(preview.GetProperty("blockers").EnumerateArray());
        Assert.Equal(draft.Id, listed.GetProperty("id").GetGuid());
        Assert.Equal("DraftLease", listed.GetProperty("kind").GetString());
        Assert.Equal(JsonValueKind.Null, listed.GetProperty("freeFrom").ValueKind);
    }

    [Fact]
    public async Task Schedule_ALongTermLandlord_CanBringHisOwnPropertyBackToShortStays()
    {
        // The property core is shared by both kinds of landlord (A7-06): the landlord has no short-rent context.
        var property = await SeedLongPropertyAsync($"auth0|pm02-landlord-{Guid.NewGuid():N}");
        using var landlord = _factory.CreateAuthenticatedClient(property.OwnerId, "LongTermLandlord");

        var preview = await landlord.GetAsync($"/api/properties/{property.Id}/mode/preview?to=short");
        var created = await landlord.PostAsJsonAsync(
            $"/api/properties/{property.Id}/mode/change", new { to = "Short", effectiveDate = Day(20).ToString("yyyy-MM-dd") });

        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
    }

    // ─── The calendar closes ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Schedule_ToLong_ClosesTheNightsOnTheSiteTheExportAndTheHost_AndCancelOpensThemAgain()
    {
        var property = await PublishedPropertyTests.SeedAsync(_factory, "pm02-closed");
        using var host = _factory.CreateAuthenticatedClient(property.OwnerId, HostRole);
        using var anonymous = _factory.CreateClient();
        var from = Day(20);

        var created = await host.PostAsJsonAsync(
            $"/api/properties/{property.Id}/mode/change", new { to = "Long", effectiveDate = from.ToString("yyyy-MM-dd") });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var changeId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        // The booking site: the night that checks out on the day of the change is taken, and the nights from the day on.
        // A stay that leaves the morning before stays free.
        var booked = (await anonymous.GetFromJsonAsync<JsonElement>(
                PublicAvailabilityPostgresTests.AvailabilityPath(property.Id, from.AddDays(-2), from.AddDays(3))))
            .GetProperty("bookedDates").EnumerateArray().Select(d => d.GetString()).ToList();
        Assert.Equal(new[] { -1, 0, 1, 2 }.Select(d => from.AddDays(d).ToString("yyyy-MM-dd")), booked);

        // The export read by the portals: an all-day event from the night before the day, for two years, neutral.
        var block = await ModeChangeBlockAsync(property.Id);
        var ics = await ExportAsync(host, property.Id);
        Assert.Contains($"UID:block-{block.Id}", ics);
        Assert.Contains($"DTSTART;VALUE=DATE:{from.AddDays(-1):yyyyMMdd}", ics);
        Assert.Contains($"DTEND;VALUE=DATE:{from.AddYears(2):yyyyMMdd}", ics);
        Assert.DoesNotContain("ModeChange", ics);

        // The host: a booking on a closed night is refused, the blocks list shows the block, and it cannot be touched.
        var booking = await host.PostAsJsonAsync("/api/bookings", new
        {
            propertyId = property.Id,
            checkInDate = from.AddDays(1).ToString("yyyy-MM-dd"),
            checkOutDate = from.AddDays(3).ToString("yyyy-MM-dd"),
            numberOfGuests = 2,
            guest = new { firstName = "Mario", lastName = "Rossi", email = $"mario.{Guid.NewGuid():N}@example.com", phone = "+393331234567", country = "Italia" },
        });
        await AssertProblemAsync(booking, HttpStatusCode.Conflict, "booking_dates_unavailable");
        var list = await host.GetFromJsonAsync<JsonElement>($"/api/properties/{property.Id}/blocks?from={from.AddDays(-1):yyyy-MM-dd}");
        Assert.Equal("ModeChange", list.EnumerateArray().Single(b => b.GetProperty("id").GetGuid() == block.Id).GetProperty("reason").GetString());
        await AssertProblemAsync(
            await host.DeleteAsync($"/api/properties/{property.Id}/blocks/{block.Id}"),
            HttpStatusCode.UnprocessableEntity,
            "calendar_block_held_by_mode_change");
        await AssertProblemAsync(
            await host.PostAsJsonAsync(
                $"/api/properties/{property.Id}/blocks",
                new { startDate = from.AddDays(-9).ToString("yyyy-MM-dd"), endDate = from.AddDays(-5).ToString("yyyy-MM-dd"), reason = "ModeChange" }),
            HttpStatusCode.UnprocessableEntity,
            "calendar_block_invalid_reason");
        Assert.Equal(1, await ModeChangeBlockCountAsync(property.Id));

        // Withdrawn: the nights are free again everywhere.
        Assert.Equal(HttpStatusCode.NoContent, (await host.DeleteAsync($"/api/properties/{property.Id}/mode/change/{changeId}")).StatusCode);
        Assert.Empty(
            (await anonymous.GetFromJsonAsync<JsonElement>(
                PublicAvailabilityPostgresTests.AvailabilityPath(property.Id, from.AddDays(-2), from.AddDays(3))))
            .GetProperty("bookedDates").EnumerateArray());
        Assert.DoesNotContain($"block-{block.Id}", await ExportAsync(host, property.Id));
        Assert.Equal(0, await ModeChangeBlockCountAsync(property.Id));
    }

    // ─── The hourly application, from the real service graph ────────────────────────────────────────

    [Fact]
    public async Task ApplyDue_AChangeToLongOnItsDay_TheModeChangesAndThePublicSiteLetsGo_ThenBackToShort()
    {
        var property = await PublishedPropertyTests.SeedAsync(_factory, "pm02-apply");
        await CompleteComplianceAsync(property);
        using var host = _factory.CreateAuthenticatedClient(property.OwnerId, HostRole);
        using var anonymous = _factory.CreateClient();
        var today = TimeProvider.System.TodayInRome();
        var toLong = await SeedChangeAsync(property, RentalMode.Long, today);
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync($"/api/properties/{property.Id}/public")).StatusCode);

        var first = await RunJobAsync();

        Assert.True(first.Applied >= 1);
        Assert.Equal(RentalMode.Long, (await LoadAsync(property.Id)).RentalMode);
        Assert.Equal(PropertyModeChangeStatus.Applied, (await LoadChangeAsync(toLong)).Status);
        Assert.Equal("Long", (await host.GetFromJsonAsync<JsonElement>($"/api/properties/{property.Id}")).GetProperty("rentalMode").GetString());
        // PM-01: a long-term property is not on the public site any more.
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync($"/api/properties/{property.Id}/public")).StatusCode);
        Assert.Equal(1, await ModeChangeBlockCountAsync(property.Id));
        // The same run again changes nothing.
        var second = await RunJobAsync();
        Assert.Equal(0, second.Applied);
        Assert.Equal(1, await ModeChangeBlockCountAsync(property.Id));

        // And back.
        var toShort = await SeedChangeAsync(property, RentalMode.Short, today);
        Assert.True((await RunJobAsync()).Applied >= 1);
        Assert.Equal(RentalMode.Short, (await LoadAsync(property.Id)).RentalMode);
        Assert.Equal(PropertyModeChangeStatus.Applied, (await LoadChangeAsync(toShort)).Status);
        Assert.Equal(0, await ModeChangeBlockCountAsync(property.Id));
        // Its requirements are complete: the evaluation after the return keeps it active, and it is published again.
        Assert.Equal(PropertyComplianceStatus.Active, (await LoadAsync(property.Id)).ComplianceStatus);
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync($"/api/properties/{property.Id}/public")).StatusCode);
    }

    [Fact]
    public async Task ApplyDue_BackToShortStays_TheComplianceIsEvaluatedAgainAndAPropertyThatLostARequirementIsSuspended()
    {
        // BE-PM01-2: the status was frozen while the property was long-term. This one has no CIN certificate nor checklist.
        var property = await PublishedPropertyTests.SeedAsync(_factory, "pm02-suspend");
        await WithDbAsync(async db =>
        {
            var row = await db.Properties.SingleAsync(p => p.Id == property.Id);
            row.RentalMode = RentalMode.Long;
            await db.SaveChangesAsync();
        });
        await SeedChangeAsync(property, RentalMode.Short, TimeProvider.System.TodayInRome());

        await RunJobAsync();

        var stored = await LoadAsync(property.Id);
        Assert.Equal(RentalMode.Short, stored.RentalMode);
        Assert.Equal(PropertyComplianceStatus.Suspended, stored.ComplianceStatus);
        using var anonymous = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync($"/api/properties/{property.Id}/public")).StatusCode);
    }

    [Fact]
    public async Task ApplyDue_AStayThatArrivedMeanwhile_FailsTheChangeAndTheHostKeepsTheMode()
    {
        var property = await _factory.SeedPropertyAsync($"auth0|pm02-fail-{Guid.NewGuid():N}");
        var today = TimeProvider.System.TodayInRome();
        var change = await SeedChangeAsync(property, RentalMode.Long, today);
        await SeedStayAsync(property, today.AddDays(-2), today.AddDays(3));

        await RunJobAsync();

        var stored = await LoadChangeAsync(change);
        Assert.Equal(PropertyModeChangeStatus.Failed, stored.Status);
        Assert.Equal("property_mode_blocked_by_bookings", stored.FailureReason);
        Assert.Equal(RentalMode.Short, (await LoadAsync(property.Id)).RentalMode);
        using var host = _factory.CreateAuthenticatedClient(property.OwnerId, HostRole);
        var state = await host.GetFromJsonAsync<JsonElement>($"/api/properties/{property.Id}/mode");
        Assert.Equal("Failed", state.GetProperty("lastChange").GetProperty("status").GetString());
        Assert.Equal("property_mode_blocked_by_bookings", state.GetProperty("lastChange").GetProperty("failureReason").GetString());
    }

    [Fact]
    public async Task PublicFeatures_WithTheFlagOn_ExposesIt()
    {
        using var anonymous = _factory.CreateClient();

        var features = await anonymous.GetFromJsonAsync<JsonElement>("/api/public/features");

        Assert.True(features.GetProperty("propertyModeChange").GetBoolean());
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The given day counted from today in Rome (negative: the past).</summary>
    private static DateTime Day(int plusDays) => TimeProvider.System.TodayInRome().AddDays(plusDays);

    private static async Task<JsonElement> AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(code, problem.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("detail").GetString()));
        return problem;
    }

    private async Task<Property> SeedLongPropertyAsync(string ownerId)
    {
        var property = await _factory.SeedPropertyAsync(ownerId);
        await WithDbAsync(async db =>
        {
            var row = await db.Properties.SingleAsync(p => p.Id == property.Id);
            row.RentalMode = RentalMode.Long;
            await db.SaveChangesAsync();
        });
        return property;
    }

    /// <summary>The CIN certificate and the confirmed safety checklist: with them the base data and the CIN, nothing is missing.</summary>
    private async Task CompleteComplianceAsync(Property property)
    {
        await WithDbAsync(async db =>
        {
            var row = await db.Properties.SingleAsync(p => p.Id == property.Id);
            row.Slug = $"pm02-{Guid.NewGuid():N}"[..20];
            db.PropertyDocuments.Add(new PropertyDocument
            {
                PropertyId = property.Id,
                OrgId = property.OrgId,
                FileName = "cin.pdf",
                StorageUrl = "documents/cin.pdf",
                DocumentType = Core.Enums.DocumentType.CinCertificate,
                UploadedBy = property.OwnerId,
            });
            db.PropertySafetyChecklists.Add(Casazen.Tests.Unit.SafetyChecklistTestData.CompleteAllElectric(property.Id, property.OrgId));
            await db.SaveChangesAsync();
        });
    }

    private async Task<Booking> SeedStayAsync(Property property, DateTime checkIn, DateTime checkOut)
    {
        var guest = new Guest
        {
            OrgId = property.OrgId,
            FirstName = "Mario",
            LastName = "Rossi",
            Email = $"mario.{Guid.NewGuid():N}@example.com",
        };
        var booking = new Booking
        {
            PropertyId = property.Id,
            OrgId = property.OrgId,
            Guest = guest,
            GuestId = guest.Id,
            CheckInDate = checkIn,
            CheckOutDate = checkOut,
            NumberOfGuests = 2,
            NumberOfAdults = 2,
            Status = BookingStatus.Confirmed,
            Source = BookingSource.Manual,
        };
        await WithDbAsync(async db =>
        {
            db.Bookings.Add(booking);
            await db.SaveChangesAsync();
        });
        return booking;
    }

    private async Task<LeaseContract> SeedLeaseAsync(Property property, DateTime start, DateTime end, LeaseStatus status)
    {
        var lease = new LeaseContract
        {
            PropertyId = property.Id,
            OrgId = property.OrgId,
            Status = status,
            StartDate = start,
            EndDate = end,
            MonthlyRent = 800m,
        };
        await WithDbAsync(async db =>
        {
            db.LeaseContracts.Add(lease);
            await db.SaveChangesAsync();
        });
        return lease;
    }

    private async Task<Guid> SeedChangeAsync(Property property, RentalMode to, DateTime effectiveDate)
    {
        var change = new PropertyModeChange
        {
            OrgId = property.OrgId,
            PropertyId = property.Id,
            FromMode = PropertyModeRules.Opposite(to),
            ToMode = to,
            EffectiveDate = effectiveDate,
            Status = PropertyModeChangeStatus.Scheduled,
            CreatedByUserId = property.OwnerId,
            CreatedAt = DateTime.UtcNow.AddDays(-3),
        };
        await WithDbAsync(async db =>
        {
            db.PropertyModeChanges.Add(change);
            await db.SaveChangesAsync();
        });
        return change.Id;
    }

    private async Task<PropertyModeRunResult> RunJobAsync()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IPropertyModeService>().ApplyDueAsync();
    }

    private async Task<Property> LoadAsync(Guid propertyId)
    {
        Property? property = null;
        await WithDbAsync(async db => property = await db.Properties.IgnoreQueryFilters().AsNoTracking().SingleAsync(p => p.Id == propertyId));
        return property!;
    }

    private async Task<PropertyModeChange> LoadChangeAsync(Guid changeId)
    {
        PropertyModeChange? change = null;
        await WithDbAsync(async db => change = await db.PropertyModeChanges.IgnoreQueryFilters().AsNoTracking().SingleAsync(c => c.Id == changeId));
        return change!;
    }

    private async Task<bool> AnyChangeAsync(Guid propertyId)
    {
        var any = false;
        await WithDbAsync(async db => any = await db.PropertyModeChanges.IgnoreQueryFilters().AnyAsync(c => c.PropertyId == propertyId));
        return any;
    }

    private async Task<CalendarBlock> ModeChangeBlockAsync(Guid propertyId)
    {
        CalendarBlock? block = null;
        await WithDbAsync(async db => block = await db.CalendarBlocks.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(b => b.PropertyId == propertyId && b.ManualReason == CalendarBlockReason.ModeChange));
        return block!;
    }

    private async Task<int> ModeChangeBlockCountAsync(Guid propertyId)
    {
        var count = 0;
        await WithDbAsync(async db => count = await db.CalendarBlocks.IgnoreQueryFilters()
            .CountAsync(b => b.PropertyId == propertyId && b.ManualReason == CalendarBlockReason.ModeChange));
        return count;
    }

    private async Task<string> ExportAsync(HttpClient host, Guid propertyId)
    {
        var exportUrl = (await host.GetFromJsonAsync<JsonElement>($"/api/properties/{propertyId}/ical/export-url"))
            .GetProperty("exportUrl").GetString()!;
        using var anonymous = _factory.CreateClient();
        return await anonymous.GetStringAsync(new Uri(exportUrl).AbsolutePath);
    }

    private async Task WithDbAsync(Func<AppDbContext, Task> action)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        await action(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    /// <summary>The integration host with <c>Features:PropertyModeChange</c> on.</summary>
    public sealed class EnabledFactory : CasazenWebApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?> { ["Features:PropertyModeChange"] = "true" }));
        }
    }
}

/// <summary>
/// PM-02 with the default configuration (<c>Features:PropertyModeChange</c> off): every endpoint of the change answers 404
/// like a route that does not exist, before authentication, and nothing is written.
/// </summary>
public class PropertyModeFlagOffIntegrationTests : IClassFixture<CasazenWebApplicationFactory>
{
    private readonly CasazenWebApplicationFactory _factory;

    public PropertyModeFlagOffIntegrationTests(CasazenWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task Endpoints_FlagOff_Answer404ToTheOwnerAndToAnonymous()
    {
        var property = await _factory.SeedPropertyAsync($"auth0|pm02-off-{Guid.NewGuid():N}");
        using var owner = _factory.CreateAuthenticatedClient(property.OwnerId, "PropertyOwner");
        using var anonymous = _factory.CreateClient();
        var day = TimeProvider.System.TodayInRome().AddDays(20).ToString("yyyy-MM-dd");

        foreach (var client in new[] { owner, anonymous })
        {
            await OtaPartnerApiFeatureFlagTests.AssertNotFoundProblemAsync(await client.GetAsync($"/api/properties/{property.Id}/mode"));
            await OtaPartnerApiFeatureFlagTests.AssertNotFoundProblemAsync(
                await client.GetAsync($"/api/properties/{property.Id}/mode/preview?to=long"));
            await OtaPartnerApiFeatureFlagTests.AssertNotFoundProblemAsync(
                await client.PostAsJsonAsync($"/api/properties/{property.Id}/mode/change", new { to = "Long", effectiveDate = day }));
            await OtaPartnerApiFeatureFlagTests.AssertNotFoundProblemAsync(
                await client.DeleteAsync($"/api/properties/{property.Id}/mode/change/{Guid.NewGuid()}"));
        }

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await db.PropertyModeChanges.IgnoreQueryFilters().AnyAsync(c => c.PropertyId == property.Id));
    }

    [Fact]
    public async Task PublicFeatures_Default_ReturnsPropertyModeChangeOff()
    {
        using var anonymous = _factory.CreateClient();

        var features = await anonymous.GetFromJsonAsync<JsonElement>("/api/public/features");

        Assert.False(features.GetProperty("propertyModeChange").GetBoolean());
    }
}
