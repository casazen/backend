using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Repositories;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Tests.Integration.Postgres;
using Casazen.Tests.Unit;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// PC-09 (A2-25) on PostgreSQL: the host closes dates by hand (owner stay, maintenance, other). The block takes its
/// nights through the single occupancy rule (BK-05): the booking site shows them taken, bookings on them are refused,
/// the export to the OTAs publishes them (neutral SUMMARY, no reason, no note) and the host calendar shows reason and
/// note. Creation runs under the dates lock of the property, so a booking and a block never take the same night. The host
/// clock is a fixed <see cref="FakeTimeProvider"/>.
/// </summary>
public class ManualBlocksPostgresTests : IClassFixture<ManualBlocksPostgresTests.Factory>
{
    private const string HostRole = "PropertyOwner";

    // "Today" for the host: 2 October of next year at 10:00 in Rome (FD-06, fixed clock).
    private static readonly DateTime Today = PublicAvailabilityPostgresTests.NextYear(10, 2);
    private static readonly DateTimeOffset Now = new(Today.AddHours(8), TimeSpan.Zero);

    private readonly Factory _factory;

    public ManualBlocksPostgresTests(Factory factory) => _factory = factory;

    [PostgresFact]
    public async Task Create_FreeNights_TakesNightsOnSiteExportAndHostCalendar()
    {
        var property = await PublishedPropertyTests.SeedAsync(_factory, "pc09-create");
        using var host = Host(property);
        var from = Today.AddDays(5);

        var response = await CreateAsync(host, property.Id, from, from.AddDays(3), "Owner", "  Ferie in famiglia  ");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var block = await response.Content.ReadFromJsonAsync<JsonElement>();
        var blockId = block.GetProperty("id").GetGuid();
        Assert.Equal(from.ToString("yyyy-MM-dd"), block.GetProperty("startDate").GetString()![..10]);
        Assert.Equal(from.AddDays(3).ToString("yyyy-MM-dd"), block.GetProperty("endDate").GetString()![..10]);
        Assert.Equal(3, block.GetProperty("nights").GetInt32());
        Assert.Equal("Owner", block.GetProperty("reason").GetString());
        Assert.Equal("Ferie in famiglia", block.GetProperty("note").GetString());

        await WithDbAsync(async db =>
        {
            var row = await db.CalendarBlocks.SingleAsync(b => b.Id == blockId);
            Assert.Equal(CalendarBlockSource.Manual, row.Source);
            Assert.Null(row.FeedId);
            Assert.Equal(property.OrgId, row.OrgId);
            Assert.Equal(CalendarBlockReason.Owner, row.ManualReason);
        });

        // Booking site (BK-05): the three nights are taken, the departure day is free.
        var booked = await BookedDatesAsync(property.Id, Today, Today.AddDays(15));
        Assert.Equal(new[] { 0, 1, 2 }.Select(d => from.AddDays(d).ToString("yyyy-MM-dd")).ToList(), booked);

        // Export to the OTAs (PC-12): an all-day event, neutral SUMMARY, never the reason or the note.
        var ics = await ExportAsync(host, property.Id);
        Assert.Contains($"UID:block-{blockId}", ics);
        Assert.Contains($"DTSTART;VALUE=DATE:{from:yyyyMMdd}", ics);
        Assert.Contains($"DTEND;VALUE=DATE:{from.AddDays(3):yyyyMMdd}", ics);
        Assert.DoesNotContain("Ferie", ics);
        Assert.DoesNotContain("Owner", ics);

        // Host calendar: a block entered by hand, with its reason and note.
        var calendar = await host.GetFromJsonAsync<JsonElement>(
            $"/api/bookings/calendar?propertyId={property.Id}&startDate={from:yyyy-MM-dd}&endDate={from.AddDays(1):yyyy-MM-dd}");
        var item = calendar.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("id").GetGuid() == blockId);
        Assert.Equal("ical-block", item.GetProperty("type").GetString());
        Assert.Equal("Manual", item.GetProperty("blockSource").GetString());
        Assert.Equal("Owner", item.GetProperty("blockReason").GetString());
        Assert.Equal("Ferie in famiglia", item.GetProperty("summary").GetString());
        Assert.Equal(JsonValueKind.Null, item.GetProperty("channel").ValueKind);

        // List of the manual blocks (read policy).
        var list = await host.GetFromJsonAsync<JsonElement>($"/api/properties/{property.Id}/blocks");
        Assert.Equal(blockId, list.EnumerateArray().Single().GetProperty("id").GetGuid());
    }

    [PostgresFact]
    public async Task Delete_ManualBlock_FreesNightsEverywhere()
    {
        var property = await PublishedPropertyTests.SeedAsync(_factory, "pc09-delete");
        using var host = Host(property);
        var from = Today.AddDays(2);
        var blockId = await CreatedAsync(host, property.Id, from, from.AddDays(2), "Maintenance");
        Assert.Equal(2, (await BookedDatesAsync(property.Id, Today, Today.AddDays(10))).Count);

        var response = await host.DeleteAsync($"/api/properties/{property.Id}/blocks/{blockId}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(await BookedDatesAsync(property.Id, Today, Today.AddDays(10)));
        Assert.DoesNotContain($"block-{blockId}", await ExportAsync(host, property.Id));
        await WithDbAsync(async db => Assert.False(await db.CalendarBlocks.AnyAsync(b => b.Id == blockId)));
        await AssertProblemAsync(
            await host.DeleteAsync($"/api/properties/{property.Id}/blocks/{blockId}"), HttpStatusCode.NotFound, "calendar_block_not_found");
    }

    [PostgresFact]
    public async Task Create_NightTakenByBooking_Returns409AndCreatesNothing()
    {
        var property = await PublishedPropertyTests.SeedAsync(_factory, "pc09-overlap");
        await SeedBookingAsync(property, Today.AddDays(3), Today.AddDays(6));
        using var host = Host(property);

        await AssertProblemAsync(
            await CreateAsync(host, property.Id, Today.AddDays(5), Today.AddDays(8), "Maintenance"),
            HttpStatusCode.Conflict,
            "calendar_block_overlaps_booking");

        // Same-day turnover: the check-out day of the booking is free for a block.
        Assert.Equal(
            HttpStatusCode.Created,
            (await CreateAsync(host, property.Id, Today.AddDays(6), Today.AddDays(8), "Maintenance")).StatusCode);
        await AssertProblemAsync(
            await CreateAsync(host, property.Id, Today.AddDays(7), Today.AddDays(9), "Other"),
            HttpStatusCode.Conflict,
            "calendar_block_overlaps_block");
        await WithDbAsync(async db => Assert.Equal(1, await db.CalendarBlocks.CountAsync(b => b.PropertyId == property.Id)));
    }

    [PostgresFact]
    public async Task AddBooking_OnManuallyBlockedNights_IsRefusedUnderThePropertyLock()
    {
        var property = await PublishedPropertyTests.SeedAsync(_factory, "pc09-booking-refused");
        using var host = Host(property);
        await CreatedAsync(host, property.Id, Today.AddDays(4), Today.AddDays(6), "Owner");

        // The final check of the repository (under the property lock) reads the blocks too, not only the callers' pre-check.
        await using var scope = _factory.Services.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IBookingRepository>();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => repository.AddAsync(NewBooking(property, Today.AddDays(5), Today.AddDays(7), BookingSource.Manual)));
        Assert.Contains("Property not available", error.Message);

        // The nights around the block stay bookable.
        await repository.AddAsync(NewBooking(property, Today.AddDays(6), Today.AddDays(8), BookingSource.Manual));
    }

    [PostgresFact]
    public async Task CreateBlockAndAddBooking_SameNightsAtTheSameTime_OnlyOneTakesThem()
    {
        var property = await PublishedPropertyTests.SeedAsync(_factory, "pc09-race");

        for (var round = 0; round < 8; round++)
        {
            var from = Today.AddDays(10 + (round * 4));
            var to = from.AddDays(2);
            using var start = new ManualResetEventSlim(false);

            var blockTask = Task.Run(async () =>
            {
                start.Wait();
                await using var scope = _factory.Services.CreateAsyncScope();
                var blocks = scope.ServiceProvider.GetRequiredService<ICalendarBlockService>();
                try
                {
                    await blocks.CreateAsync(new ManualBlockRequest(property.Id, from, to, CalendarBlockReason.Maintenance));
                    return true;
                }
                catch (DomainConflictException)
                {
                    return false;
                }
            });
            var bookingTask = Task.Run(async () =>
            {
                start.Wait();
                await using var scope = _factory.Services.CreateAsyncScope();
                var repository = scope.ServiceProvider.GetRequiredService<IBookingRepository>();
                try
                {
                    await repository.AddAsync(NewBooking(property, from, to, BookingSource.Direct));
                    return true;
                }
                catch (InvalidOperationException)
                {
                    return false;
                }
            });

            start.Set();
            var outcomes = await Task.WhenAll(blockTask, bookingTask);

            Assert.Equal(1, outcomes.Count(won => won));
        }
    }

    [PostgresFact]
    public async Task Create_InvalidRequests_ReturnLocalizedProblems()
    {
        var property = await PublishedPropertyTests.SeedAsync(_factory, "pc09-invalid");
        using var host = Host(property);

        await AssertProblemAsync(
            await CreateAsync(host, property.Id, Today.AddDays(-1), Today.AddDays(2), "Owner"),
            HttpStatusCode.UnprocessableEntity,
            "calendar_block_in_past");
        await AssertProblemAsync(
            await CreateAsync(host, property.Id, Today.AddDays(3), Today.AddDays(3), "Owner"),
            HttpStatusCode.UnprocessableEntity,
            "calendar_block_invalid_range");
        await AssertProblemAsync(
            await CreateAsync(host, property.Id, Today, Today.AddDays(ManualBlocks.MaxNights + 1), "Owner"),
            HttpStatusCode.UnprocessableEntity,
            "calendar_block_too_long");

        var noReason = await host.PostAsJsonAsync(
            $"/api/properties/{property.Id}/blocks",
            new { startDate = Today.ToString("yyyy-MM-dd"), endDate = Today.AddDays(1).ToString("yyyy-MM-dd") });
        Assert.Equal(HttpStatusCode.BadRequest, noReason.StatusCode);
        var unknownReason = await CreateAsync(host, property.Id, Today, Today.AddDays(1), "Holiday");
        Assert.Equal(HttpStatusCode.BadRequest, unknownReason.StatusCode);
        var longNote = await CreateAsync(host, property.Id, Today, Today.AddDays(1), "Other", new string('x', 201));
        Assert.Equal(HttpStatusCode.BadRequest, longNote.StatusCode);

        // Today (Europe/Rome) is a valid first night.
        Assert.Equal(HttpStatusCode.Created, (await CreateAsync(host, property.Id, Today, Today.AddDays(1), "Other")).StatusCode);
    }

    [PostgresFact]
    public async Task Delete_ImportedBlock_Returns422AndKeepsIt()
    {
        var property = await PublishedPropertyTests.SeedAsync(_factory, "pc09-imported");
        var importedId = Guid.Empty;
        await WithDbAsync(async db =>
        {
            var block = new CalendarBlock
            {
                PropertyId = property.Id,
                OrgId = property.OrgId,
                Source = CalendarBlockSource.ICalImport,
                ExternalUid = $"{Guid.NewGuid():N}@airbnb.com",
                StartUtc = Today.AddDays(1),
                EndUtc = Today.AddDays(3),
            };
            db.CalendarBlocks.Add(block);
            await db.SaveChangesAsync();
            importedId = block.Id;
        });
        using var host = Host(property);

        await AssertProblemAsync(
            await host.DeleteAsync($"/api/properties/{property.Id}/blocks/{importedId}"),
            HttpStatusCode.UnprocessableEntity,
            "calendar_block_not_manual");
        await WithDbAsync(async db => Assert.True(await db.CalendarBlocks.AnyAsync(b => b.Id == importedId)));

        // A night already closed by a channel may be closed by hand too: it stays closed if the channel frees it.
        Assert.Equal(HttpStatusCode.Created, (await CreateAsync(host, property.Id, Today.AddDays(2), Today.AddDays(4), "Owner")).StatusCode);
        // An imported block is not in the list of the manual blocks.
        var list = await host.GetFromJsonAsync<JsonElement>($"/api/properties/{property.Id}/blocks");
        Assert.DoesNotContain(list.EnumerateArray(), b => b.GetProperty("id").GetGuid() == importedId);
    }

    [PostgresFact]
    public async Task Blocks_OtherOrgOrNoShortRentPermission_AreRefused()
    {
        var property = await PublishedPropertyTests.SeedAsync(_factory, "pc09-tenant");
        using var owner = Host(property);
        var blockId = await CreatedAsync(owner, property.Id, Today.AddDays(1), Today.AddDays(2), "Owner");

        var other = await _factory.SeedPropertyAsync($"auth0|pc09-other-{Guid.NewGuid():N}");
        using var intruder = _factory.CreateAuthenticatedClient(other.OwnerId, HostRole);
        await AssertProblemAsync(
            await CreateAsync(intruder, property.Id, Today.AddDays(5), Today.AddDays(6), "Owner"),
            HttpStatusCode.NotFound,
            "property_not_found");
        Assert.Equal(HttpStatusCode.NotFound, (await intruder.GetAsync($"/api/properties/{property.Id}/blocks")).StatusCode);
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await intruder.DeleteAsync($"/api/properties/{property.Id}/blocks/{blockId}")).StatusCode);
        // A block id of another property of the caller's org is not found either.
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await intruder.DeleteAsync($"/api/properties/{other.Id}/blocks/{blockId}")).StatusCode);

        // A long-term landlord has no short-rent property permission: 403 on the same property.
        using var landlord = _factory.CreateAuthenticatedClient(property.OwnerId, "LongTermLandlord");
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await CreateAsync(landlord, property.Id, Today.AddDays(5), Today.AddDays(6), "Owner")).StatusCode);

        await WithDbAsync(async db => Assert.Equal(1, await db.CalendarBlocks.CountAsync(b => b.PropertyId == property.Id)));
    }

    private HttpClient Host(Property property) => _factory.CreateAuthenticatedClient(property.OwnerId, HostRole);

    private static Task<HttpResponseMessage> CreateAsync(
        HttpClient client, Guid propertyId, DateTime from, DateTime to, string reason, string? note = null) =>
        client.PostAsJsonAsync(
            $"/api/properties/{propertyId}/blocks",
            new { startDate = from.ToString("yyyy-MM-dd"), endDate = to.ToString("yyyy-MM-dd"), reason, note });

    private static async Task<Guid> CreatedAsync(HttpClient client, Guid propertyId, DateTime from, DateTime to, string reason)
    {
        var response = await CreateAsync(client, propertyId, from, to, reason);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(code, problem.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("detail").GetString()));
    }

    private async Task<List<string>> BookedDatesAsync(Guid propertyId, DateTime from, DateTime to)
    {
        using var anonymous = _factory.CreateClient();
        var body = await anonymous.GetFromJsonAsync<JsonElement>(PublicAvailabilityPostgresTests.AvailabilityPath(propertyId, from, to));
        return body.GetProperty("bookedDates").EnumerateArray().Select(d => d.GetString()!).ToList();
    }

    private async Task<string> ExportAsync(HttpClient host, Guid propertyId)
    {
        var exportUrl = (await host.GetFromJsonAsync<JsonElement>($"/api/properties/{propertyId}/ical/export-url"))
            .GetProperty("exportUrl").GetString()!;
        using var anonymous = _factory.CreateClient();
        return await anonymous.GetStringAsync(new Uri(exportUrl).AbsolutePath);
    }

    private static Booking NewBooking(Property property, DateTime checkIn, DateTime checkOut, BookingSource source)
    {
        var guest = new Guest
        {
            OrgId = property.OrgId,
            FirstName = "Giulia",
            LastName = "Bianchi",
            Email = $"giulia.{Guid.NewGuid():N}@example.com",
        };
        return new Booking
        {
            PropertyId = property.Id,
            OrgId = property.OrgId,
            Guest = guest,
            GuestId = guest.Id,
            CheckInDate = checkIn,
            CheckOutDate = checkOut,
            NumberOfGuests = 1,
            NumberOfAdults = 1,
            Status = BookingStatus.Confirmed,
            Source = source,
        };
    }

    private async Task SeedBookingAsync(Property property, DateTime checkIn, DateTime checkOut)
    {
        await WithDbAsync(async db =>
        {
            db.Bookings.Add(NewBooking(property, checkIn, checkOut, BookingSource.Manual));
            await db.SaveChangesAsync();
        });
    }

    private async Task WithDbAsync(Func<AppDbContext, Task> action)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        await action(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    /// <summary>The integration host with a fixed clock (<see cref="Now"/>).</summary>
    public sealed class Factory : CasazenWebApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(new FakeTimeProvider(Now));
            });
        }
    }
}
