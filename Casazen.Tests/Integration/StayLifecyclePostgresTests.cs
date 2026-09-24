using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Tests.Integration.Postgres;
using Casazen.Web.BackgroundJobs;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// CO-08 (A5-08) on real PostgreSQL, through the whole pipeline (auth, TN-3, ProblemDetails): the host registers the
/// arrival of a confirmed booking ("Registra arrivo"), late too, and closes the stay with the check-out wizard or
/// <c>POST /check-out</c>, which follow the same rules; a confirmed booking without arrival is closed with "registra
/// arrivo e procedi" instead of a 409 dead end, and the cockpit counts today's departures.
/// </summary>
public class StayLifecyclePostgresTests : IClassFixture<CasazenWebApplicationFactory>
{
    private const string HostRole = "PropertyOwner";
    private readonly CasazenWebApplicationFactory _factory;

    public StayLifecyclePostgresTests(CasazenWebApplicationFactory factory) => _factory = factory;

    private static DateTime Today => TimeProvider.System.TodayInRome();

    [PostgresFact]
    public async Task CheckIn_DayAfterTheCheckInDay_RegistersTheArrivalWithoutAnInventedArrivalTime()
    {
        var (hostId, property) = await SeedHostPropertyAsync();
        var bookingId = await SeedBookingAsync(property, BookingStatus.Confirmed, Today.AddDays(-1), Today.AddDays(2));
        using var host = _factory.CreateAuthenticatedClient(hostId, HostRole);

        var response = await host.PostAsync($"/api/bookings/{bookingId}/check-in", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("CheckedIn", body.GetProperty("status").GetString());
        Assert.False(body.GetProperty("guestDataComplete").GetBoolean());
        var stored = await LoadAsync(bookingId);
        Assert.Equal(BookingStatus.CheckedIn, stored.Status);
        // Registered a day late: the real arrival time is unknown, the Alloggiati term runs from the check-in day.
        Assert.Null(stored.ArrivedAt);
        Assert.NotNull(stored.CheckoutReminderJobId);
        VerifyReminderScheduled(bookingId, Times.Once());
    }

    [PostgresFact]
    public async Task CheckIn_OnTheCheckInDayWithCompleteGuestData_RecordsTheArrivalTimeAndSaysTheDataAreComplete()
    {
        var (hostId, property) = await SeedHostPropertyAsync();
        var bookingId = await SeedBookingAsync(property, BookingStatus.Confirmed, Today, Today.AddDays(2), completeGuestData: true);
        using var host = _factory.CreateAuthenticatedClient(hostId, HostRole);

        var response = await host.PostAsync($"/api/bookings/{bookingId}/check-in", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("guestDataComplete").GetBoolean());
        Assert.NotNull((await LoadAsync(bookingId)).ArrivedAt);
    }

    [PostgresFact]
    public async Task CheckIn_BeforeTheCheckInDay_Returns422AndKeepsTheBookingConfirmed()
    {
        var (hostId, property) = await SeedHostPropertyAsync();
        var bookingId = await SeedBookingAsync(property, BookingStatus.Confirmed, Today.AddDays(1), Today.AddDays(3));
        using var host = _factory.CreateAuthenticatedClient(hostId, HostRole);

        var response = await host.PostAsync($"/api/bookings/{bookingId}/check-in", null);

        var problem = await ProblemAsync(response, HttpStatusCode.UnprocessableEntity);
        Assert.Equal("booking_arrival_too_early", problem.GetProperty("code").GetString());
        Assert.Contains(Today.AddDays(1).ToString("dd/MM/yyyy"), problem.GetProperty("detail").GetString());
        Assert.Equal(BookingStatus.Confirmed, (await LoadAsync(bookingId)).Status);
        VerifyReminderScheduled(bookingId, Times.Never());
    }

    [PostgresFact]
    public async Task CheckIn_AfterTheCheckOutDay_Returns422PointingToTheCheckOut()
    {
        var (hostId, property) = await SeedHostPropertyAsync();
        var bookingId = await SeedBookingAsync(property, BookingStatus.Confirmed, Today.AddDays(-4), Today.AddDays(-1));
        using var host = _factory.CreateAuthenticatedClient(hostId, HostRole);

        var response = await host.PostAsync($"/api/bookings/{bookingId}/check-in", null);

        var problem = await ProblemAsync(response, HttpStatusCode.UnprocessableEntity);
        Assert.Equal("booking_arrival_after_departure", problem.GetProperty("code").GetString());
        Assert.Equal(BookingStatus.Confirmed, (await LoadAsync(bookingId)).Status);
    }

    [PostgresFact]
    public async Task CheckIn_TwoRequestsTogether_ChecksInOnceAndTheOtherGets409()
    {
        var (hostId, property) = await SeedHostPropertyAsync();
        var bookingId = await SeedBookingAsync(property, BookingStatus.Confirmed, Today, Today.AddDays(2));
        using var web = _factory.CreateAuthenticatedClient(hostId, HostRole);
        using var app = _factory.CreateAuthenticatedClient(hostId, HostRole);

        var responses = await Task.WhenAll(
            web.PostAsync($"/api/bookings/{bookingId}/check-in", null),
            app.PostAsync($"/api/bookings/{bookingId}/check-in", null));

        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.OK);
        var conflict = Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Conflict);
        Assert.Equal("booking_already_checked_in", (await conflict.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        VerifyReminderScheduled(bookingId, Times.Once());
    }

    [PostgresFact]
    public async Task CheckoutWizard_ConfirmedWithoutArrival_RegisterArrivalAndProceedClosesTheStay()
    {
        var (hostId, property) = await SeedHostPropertyAsync();
        var bookingId = await SeedBookingAsync(property, BookingStatus.Confirmed, Today.AddDays(-2), Today);
        using var host = _factory.CreateAuthenticatedClient(hostId, HostRole);

        var deadEnd = await host.PostAsync($"/api/bookings/{bookingId}/checkout-wizard/start", null);
        var start = await host.PostAsJsonAsync($"/api/bookings/{bookingId}/checkout-wizard/start", new { registerArrival = true });
        var afterStart = await LoadAsync(bookingId);
        var complete = await host.PostAsJsonAsync($"/api/bookings/{bookingId}/checkout-wizard/complete", new { confirmDeparture = true });

        // Without the host's confirmation: a code the wizard turns into "registra arrivo e procedi", nothing changed.
        Assert.Equal("booking_arrival_not_registered", (await ProblemAsync(deadEnd, HttpStatusCode.Conflict)).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.OK, start.StatusCode);
        Assert.Equal(BookingStatus.CheckedIn, afterStart.Status);
        Assert.NotNull(afterStart.CheckoutWizardStartedAt);
        Assert.Equal(HttpStatusCode.OK, complete.StatusCode);
        Assert.Equal("CheckedOut", (await complete.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("bookingStatus").GetString());
        var stored = await LoadAsync(bookingId);
        Assert.Equal(BookingStatus.CheckedOut, stored.Status);
        Assert.Null(stored.CheckoutReminderJobId);
    }

    [PostgresFact]
    public async Task CheckOut_ConfirmedWithoutArrivalInOneCall_RegistersTheArrivalAndChecksOut()
    {
        var (hostId, property) = await SeedHostPropertyAsync();
        var bookingId = await SeedBookingAsync(property, BookingStatus.Confirmed, Today.AddDays(-5), Today.AddDays(-2));
        using var host = _factory.CreateAuthenticatedClient(hostId, HostRole);

        var response = await host.PostAsJsonAsync($"/api/bookings/{bookingId}/check-out", new { registerArrival = true });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("CheckedOut", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());
        var stored = await LoadAsync(bookingId);
        Assert.Equal(BookingStatus.CheckedOut, stored.Status);
        Assert.Null(stored.ArrivedAt);
        // A stay closed at once never gets a check-out reminder.
        VerifyReminderScheduled(bookingId, Times.Never());
    }

    [PostgresFact]
    public async Task WizardAndCheckOutEndpoint_SameStays_SameOutcome()
    {
        var (hostId, property) = await SeedHostPropertyAsync();
        using var host = _factory.CreateAuthenticatedClient(hostId, HostRole);
        var cases = new (string Name, BookingStatus Status, DateTime CheckIn, DateTime CheckOut, bool RegisterArrival)[]
        {
            ("checked in, departure today", BookingStatus.CheckedIn, Today.AddDays(-2), Today, false),
            ("checked in, early departure", BookingStatus.CheckedIn, Today.AddDays(-1), Today.AddDays(2), false),
            ("confirmed, arrival not registered", BookingStatus.Confirmed, Today.AddDays(-2), Today, false),
            ("confirmed, registra arrivo e procedi", BookingStatus.Confirmed, Today.AddDays(-2), Today, true),
            ("confirmed, before the check-in day", BookingStatus.Confirmed, Today.AddDays(3), Today.AddDays(5), true),
            ("pending", BookingStatus.Pending, Today.AddDays(-2), Today, true),
            ("already checked out", BookingStatus.CheckedOut, Today.AddDays(-2), Today, false),
        };

        foreach (var (name, status, checkIn, checkOut, registerArrival) in cases)
        {
            // Two identical stays per case (seeded directly: the check-out never checks the dates against other stays).
            var viaWizard = await SeedBookingAsync(property, status, checkIn, checkOut);
            var viaEndpoint = await SeedBookingAsync(property, status, checkIn, checkOut);

            var start = await host.PostAsJsonAsync($"/api/bookings/{viaWizard}/checkout-wizard/start", new { registerArrival });
            var wizard = start.IsSuccessStatusCode
                ? await host.PostAsJsonAsync($"/api/bookings/{viaWizard}/checkout-wizard/complete", new { confirmDeparture = true })
                : start;
            var endpoint = await host.PostAsJsonAsync($"/api/bookings/{viaEndpoint}/check-out", new { registerArrival });

            Assert.True(wizard.StatusCode == endpoint.StatusCode, $"{name}: wizard {wizard.StatusCode}, endpoint {endpoint.StatusCode}");
            Assert.True(await CodeOfAsync(wizard) == await CodeOfAsync(endpoint), $"{name}: different error codes");
            Assert.True((await LoadAsync(viaWizard)).Status == (await LoadAsync(viaEndpoint)).Status, $"{name}: different status");
        }
    }

    [PostgresFact]
    public async Task ComplianceSummary_CheckoutsDue_CountsTodaysDeparturesConfirmedOrCheckedInAndOpenStays()
    {
        var (hostId, property) = await SeedHostPropertyAsync();
        var confirmedToday = await SeedBookingAsync(property, BookingStatus.Confirmed, Today.AddDays(-2), Today);
        var checkedInToday = await SeedBookingAsync(property, BookingStatus.CheckedIn, Today.AddDays(-3), Today);
        var checkedInOverdue = await SeedBookingAsync(property, BookingStatus.CheckedIn, Today.AddDays(-4), Today.AddDays(-1));
        await SeedBookingAsync(property, BookingStatus.Confirmed, Today.AddDays(-5), Today.AddDays(-1));
        await SeedBookingAsync(property, BookingStatus.CheckedIn, Today.AddDays(-1), Today.AddDays(1));
        await SeedBookingAsync(property, BookingStatus.CheckedOut, Today.AddDays(-2), Today);
        await SeedBookingAsync(property, BookingStatus.Cancelled, Today.AddDays(-2), Today);
        var (otherHostId, otherProperty) = await SeedHostPropertyAsync();
        await SeedBookingAsync(otherProperty, BookingStatus.Confirmed, Today.AddDays(-2), Today);
        using var host = _factory.CreateAuthenticatedClient(hostId, HostRole);

        var summary = await host.GetFromJsonAsync<JsonElement>("/api/compliance/summary");

        var due = summary.GetProperty("checkoutsDue");
        Assert.Equal(3, due.GetProperty("count").GetInt32());
        Assert.Equal(
            new[] { confirmedToday, checkedInToday, checkedInOverdue }.Order(),
            due.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).Order());
        Assert.NotEqual(hostId, otherHostId);

        // Once checked out the departure is no longer due.
        using var closing = await host.PostAsJsonAsync($"/api/bookings/{confirmedToday}/check-out", new { registerArrival = true });
        Assert.Equal(HttpStatusCode.OK, closing.StatusCode);
        var after = await host.GetFromJsonAsync<JsonElement>("/api/compliance/summary");
        Assert.Equal(2, after.GetProperty("checkoutsDue").GetProperty("count").GetInt32());
    }

    [PostgresFact]
    public async Task StayActions_BookingOfAnotherOrg_Answer404AndChangeNothing()
    {
        var (_, property) = await SeedHostPropertyAsync();
        var confirmed = await SeedBookingAsync(property, BookingStatus.Confirmed, Today.AddDays(-1), Today);
        var (intruderId, _) = await SeedHostPropertyAsync();
        using var intruder = _factory.CreateAuthenticatedClient(intruderId, HostRole);

        var responses = new[]
        {
            await intruder.PostAsync($"/api/bookings/{confirmed}/check-in", null),
            await intruder.PostAsJsonAsync($"/api/bookings/{confirmed}/checkout-wizard/start", new { registerArrival = true }),
            await intruder.PostAsJsonAsync($"/api/bookings/{confirmed}/checkout-wizard/complete", new { confirmDeparture = true, registerArrival = true }),
            await intruder.PostAsJsonAsync($"/api/bookings/{confirmed}/check-out", new { registerArrival = true }),
        };

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.NotFound, r.StatusCode));
        var stored = await LoadAsync(confirmed);
        Assert.Equal(BookingStatus.Confirmed, stored.Status);
        Assert.Null(stored.CheckoutWizardStartedAt);
    }

    private void VerifyReminderScheduled(Guid bookingId, Times times) =>
        _factory.BackgroundJobClientMock.Verify(
            c => c.Create(
                It.Is<Job>(j => j.Type == typeof(CheckoutReminderJob) && j.Args.Count == 1 && (Guid)j.Args[0] == bookingId),
                It.IsAny<ScheduledState>()),
            times);

    private static async Task<JsonElement> ProblemAsync(HttpResponseMessage response, HttpStatusCode expected)
    {
        Assert.Equal(expected, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<string?> CodeOfAsync(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode)
            return null;
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    /// <summary>A host with its org and an active property in Rome.</summary>
    private async Task<(string HostId, Property Property)> SeedHostPropertyAsync()
    {
        var hostId = $"auth0|co08-{Guid.NewGuid():N}";
        var property = await _factory.SeedPropertyAsync(hostId);
        return (hostId, property);
    }

    private int _stayOffset;

    /// <summary>
    /// A booking of <paramref name="property"/>, inserted directly (not through the booking API, which refuses past
    /// check-ins and overlapping dates). With <paramref name="completeGuestData"/> its single guest has every field of
    /// the Alloggiati record (CO-12).
    /// </summary>
    private async Task<Guid> SeedBookingAsync(
        Property property,
        BookingStatus status,
        DateTime checkIn,
        DateTime checkOut,
        bool completeGuestData = false)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var guest = new Guest
        {
            OrgId = property.OrgId,
            FirstName = "Anna",
            LastName = $"Verdi {Interlocked.Increment(ref _stayOffset)}",
            Email = $"anna.{Guid.NewGuid():N}@example.com",
        };
        var booking = new Booking
        {
            PropertyId = property.Id,
            OrgId = property.OrgId,
            GuestId = guest.Id,
            CheckInDate = checkIn,
            CheckOutDate = checkOut,
            NumberOfGuests = 1,
            Status = status,
            Source = BookingSource.Manual,
            BasePrice = 300m,
            TotalPrice = 300m,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.AddRange(guest, booking);

        if (completeGuestData)
        {
            db.StayGuests.Add(new StayGuest
            {
                BookingId = booking.Id,
                OrgId = property.OrgId,
                GuestId = guest.Id,
                Position = 0,
                Type = StayGuestType.SingleGuest,
                FirstName = "Anna",
                LastName = "Verdi",
                Gender = Gender.Female,
                DateOfBirth = new DateTime(1985, 3, 10, 0, 0, 0, DateTimeKind.Utc),
                BornInItaly = true,
                BirthComuneName = "Milano",
                BirthProvince = "MI",
                CitizenshipName = "Italia",
                DocumentType = GuestDocumentType.Passport,
                DocumentNumber = "AB123456",
                DocumentIssuePlaceName = "Milano",
            });
        }

        await db.SaveChangesAsync();
        return booking.Id;
    }

    private async Task<Booking> LoadAsync(Guid bookingId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Bookings.IgnoreQueryFilters().AsNoTracking().SingleAsync(b => b.Id == bookingId);
    }
}
