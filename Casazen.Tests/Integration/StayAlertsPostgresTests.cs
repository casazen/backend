using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.External;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Integration.Postgres;
using Casazen.Tests.Unit;
using Casazen.Tests.Unit.Email;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// CO-10 (A5-11, A6-07, A5-25) on real PostgreSQL: the hourly stay alerts send each stage once (dedup on
/// <see cref="StayAlertState"/>), nothing for a communication sent or a cancelled stay, a specific alert for a failed
/// communication, and the check-out reminder (email + push) of every confirmed booking at 20:00 of its check-out day,
/// moved with the dates and dropped by a cancellation. The clock is a <see cref="FakeTimeProvider"/> moved one hour per
/// run.
/// </summary>
/// <remarks>
/// The tests of this class share one database and run one after the other: each one uses its own month, so the bookings
/// of one test are never inside the window of another test's runs.
/// </remarks>
public class StayAlertsPostgresTests : IClassFixture<CasazenWebApplicationFactory>
{
    private const string HostRole = "PropertyOwner";
    private const int CheckoutReminderHour = 20;

    private static readonly int Year = TimeProvider.System.TodayInRome().Year + 1;

    private readonly CasazenWebApplicationFactory _factory;

    public StayAlertsPostgresTests(CasazenWebApplicationFactory factory) => _factory = factory;

    [PostgresTheory]
    [InlineData(false, 1, new[] { "guest-checkin-incomplete", "alloggiati-deadline", "alloggiati-overdue" })]
    [InlineData(true, 2, new[] { "alloggiati-deadline", "alloggiati-overdue" })]
    public async Task RunAsync_48HourlyRuns_SendsOneMessagePerStage(bool guestDataComplete, int month, string[] expectedTemplates)
    {
        var checkIn = Date(Year, month, 12);
        var booking = await SeedStayAsync(checkIn, checkIn.AddDays(3), guestDataComplete: guestDataComplete);
        var alerts = NewHarness(RomeToUtc(checkIn.AddDays(-1), 6));

        await alerts.RunHourlyAsync(48);

        Assert.Equal(expectedTemplates, alerts.Emails.Queued.Select(e => e.Template));
        Assert.All(alerts.Emails.Queued, e => Assert.Equal("owner@example.com", e.To));
        var pushes = alerts.Push.Sent;
        Assert.Equal(expectedTemplates.Length, pushes.Count);
        Assert.All(pushes, p => Assert.Equal(booking.Id, p.BookingId));
        // Phases: day before at 10:00, arrival day at 12:00, end of the arrival day (Europe/Rome).
        var expectedTimes = new List<DateTime>();
        if (!guestDataComplete)
            expectedTimes.Add(RomeToUtc(checkIn.AddDays(-1), 10));
        expectedTimes.Add(RomeToUtc(checkIn, 12));
        expectedTimes.Add(RomeToUtc(checkIn.AddDays(1), 0));
        Assert.Equal(expectedTimes, pushes.Select(p => p.At));
        var state = await StateAsync(booking.Id, StayAlertType.AlloggiatiDeadline);
        Assert.Equal(AlloggiatiAlertStage.Overdue, state.Stage);
        Assert.Equal(expectedTemplates.Length, state.AlertCount);
        Assert.Equal(checkIn, state.ReferenceDate);
    }

    [PostgresFact]
    public async Task RunAsync_SixDaysOfHourlyRuns_SendsAtMostTheConfiguredMaximumPerStay()
    {
        var checkIn = Date(Year, 3, 10);
        var booking = await SeedStayAsync(checkIn, checkIn.AddDays(10));
        var options = new StayAlertOptions();
        var alerts = NewHarness(RomeToUtc(checkIn.AddDays(-1), 0), options);

        await alerts.RunHourlyAsync(6 * 24);

        Assert.Equal(options.MaxAlloggiatiDeadlineMessages, alerts.Emails.Queued.Count);
        Assert.Equal(
            new[] { "guest-checkin-incomplete", "alloggiati-deadline", "alloggiati-overdue", "alloggiati-overdue", "alloggiati-overdue" },
            alerts.Emails.Queued.Select(e => e.Template));
        Assert.Contains("Promemoria 2 di 2.", alerts.Emails.Queued[^1].Content.HtmlBody, StringComparison.Ordinal);
        Assert.Equal(RomeToUtc(checkIn.AddDays(3), 9), alerts.Push.Sent[^1].At);
        Assert.Equal(5, (await StateAsync(booking.Id, StayAlertType.AlloggiatiDeadline)).AlertCount);
    }

    [PostgresTheory]
    [InlineData(AlloggiatiWebStatus.Inviato, 4)]
    [InlineData(AlloggiatiWebStatus.InviatoManualmente, 5)]
    public async Task RunAsync_CommunicationSentOrDeclaredSent_SendsNoAlert(AlloggiatiWebStatus status, int month)
    {
        var checkIn = Date(Year, month, 12);
        var booking = await SeedStayAsync(checkIn, checkIn.AddDays(10), reportStatus: status);
        var alerts = NewHarness(RomeToUtc(checkIn.AddDays(-1), 0));

        await alerts.RunHourlyAsync(5 * 24);

        Assert.Empty(alerts.Emails.Queued);
        Assert.Empty(alerts.Push.Sent);
        Assert.False(await HasStateAsync(booking.Id));
    }

    [PostgresFact]
    public async Task RunAsync_ReportFailed_SendsTheFailedAlertOnceAndNeverTheGuestDataOne()
    {
        var checkIn = Date(Year, 6, 12);
        var booking = await SeedStayAsync(checkIn, checkIn.AddDays(10), reportStatus: AlloggiatiWebStatus.Errore);
        var alerts = NewHarness(RomeToUtc(checkIn.AddDays(-1), 6));

        await alerts.RunHourlyAsync(48);

        Assert.Equal(
            new[] { "alloggiati-failed", "alloggiati-deadline", "alloggiati-overdue" },
            alerts.Emails.Queued.Select(e => e.Template));
        Assert.Equal(
            new[] { "alloggiati-failed", "alloggiati-deadline", "alloggiati-overdue" },
            alerts.Push.Sent.Select(p => p.Type));
        Assert.DoesNotContain(alerts.Emails.Queued, e => e.Content.Subject.Contains("Check-in incompleto", StringComparison.Ordinal));
        Assert.Equal(1, (await StateAsync(booking.Id, StayAlertType.AlloggiatiFailed)).AlertCount);
    }

    [PostgresTheory]
    [InlineData(BookingStatus.Cancelled, 7)]
    [InlineData(BookingStatus.Pending, 8)]
    public async Task RunAsync_CancelledOrPendingStay_SendsNothing(BookingStatus status, int month)
    {
        var checkIn = Date(Year, month, 12);
        var booking = await SeedStayAsync(checkIn, checkIn.AddDays(2), status: status);
        var alerts = NewHarness(RomeToUtc(checkIn.AddDays(-1), 0));

        await alerts.RunHourlyAsync(5 * 24);

        Assert.Empty(alerts.Emails.Queued);
        Assert.Empty(alerts.Push.Sent);
        Assert.False(await HasStateAsync(booking.Id));
    }

    [PostgresFact]
    public async Task ManualBooking_ConfirmedAtCreationNeverCheckedIn_RemindsOnCheckOutDayAt20ByEmailAndPush()
    {
        var (host, propertyId) = await HostWithPropertyAsync(timeZone: string.Empty);
        var checkIn = Date(Year, 9, 6);
        var bookingId = await CreateManualBookingAsync(host, propertyId, checkIn, checkIn.AddDays(3));
        var alerts = NewHarness(RomeToUtc(checkIn.AddDays(3), 0));

        await alerts.RunHourlyAsync(24);

        var reminder = Assert.Single(alerts.Emails.Queued, e => e.Template == "checkout-reminder");
        Assert.Equal("owner@example.com", reminder.To);
        Assert.StartsWith("Check-out di oggi - ", reminder.Content.Subject, StringComparison.Ordinal);
        var push = Assert.Single(alerts.Push.Sent, p => p.Type == "checkout-reminder");
        Assert.Equal(bookingId, push.BookingId);
        // No time zone on the property: Europe/Rome.
        Assert.Equal(RomeToUtc(checkIn.AddDays(3), CheckoutReminderHour), push.At);
    }

    [PostgresFact]
    public async Task ManualBooking_CheckOutDateChanged_RemindsOnTheNewDayOnly()
    {
        var (host, propertyId) = await HostWithPropertyAsync();
        var checkIn = Date(Year, 10, 5);
        var bookingId = await CreateManualBookingAsync(host, propertyId, checkIn, checkIn.AddDays(3));

        var moved = await host.PutAsJsonAsync($"/api/bookings/{bookingId}", new
        {
            checkInDate = checkIn.ToString("yyyy-MM-dd"),
            checkOutDate = checkIn.AddDays(5).ToString("yyyy-MM-dd"),
            numberOfGuests = 2,
        });
        Assert.Equal(HttpStatusCode.OK, moved.StatusCode);
        var alerts = NewHarness(RomeToUtc(checkIn.AddDays(3), 0));

        await alerts.RunHourlyAsync(3 * 24);

        var push = Assert.Single(alerts.Push.Sent, p => p.Type == "checkout-reminder");
        Assert.Equal(RomeToUtc(checkIn.AddDays(5), CheckoutReminderHour), push.At);
        Assert.Single(alerts.Emails.Queued, e => e.Template == "checkout-reminder");
    }

    [PostgresFact]
    public async Task ManualBooking_CancelledByTheHost_SendsNoReminderNorAlert()
    {
        var (host, propertyId) = await HostWithPropertyAsync();
        var checkIn = Date(Year, 11, 5);
        var bookingId = await CreateManualBookingAsync(host, propertyId, checkIn, checkIn.AddDays(3));

        var cancelled = await host.PostAsync($"/api/bookings/{bookingId}/cancel", null);
        Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);
        var alerts = NewHarness(RomeToUtc(checkIn.AddDays(-1), 0));

        await alerts.RunHourlyAsync(6 * 24);

        Assert.Empty(alerts.Emails.Queued);
        Assert.Empty(alerts.Push.Sent);
        Assert.False(await HasStateAsync(bookingId));
    }

    [PostgresFact]
    public async Task RunAsync_StayExtendedAfterItsReminder_RemindsAgainOnTheNewCheckOutDay()
    {
        var checkIn = Date(Year, 12, 1);
        var checkOut = checkIn.AddDays(3);
        var booking = await SeedStayAsync(checkIn, checkOut, status: BookingStatus.CheckedIn, reportStatus: AlloggiatiWebStatus.InviatoManualmente);
        var alerts = NewHarness(RomeToUtc(checkOut, CheckoutReminderHour));
        await alerts.RunHourlyAsync(2);

        await UpdateBookingAsync(booking.Id, b => b.CheckOutDate = checkOut.AddDays(2));
        alerts.Clock.SetUtcNow(RomeToUtc(checkOut.AddDays(2), CheckoutReminderHour));
        await alerts.RunHourlyAsync(2);

        Assert.Equal(
            new[] { RomeToUtc(checkOut, CheckoutReminderHour), RomeToUtc(checkOut.AddDays(2), CheckoutReminderHour) },
            alerts.Push.Sent.Where(p => p.Type == "checkout-reminder").Select(p => p.At));
        var state = await StateAsync(booking.Id, StayAlertType.CheckoutReminder);
        Assert.Equal(2, state.AlertCount);
        Assert.Equal(checkOut.AddDays(2), state.ReferenceDate);
    }

    [PostgresFact]
    public async Task RunAsync_CommunicationDeclaredSentAfterTheWarning_StopsTheSequence()
    {
        var checkIn = Date(Year + 1, 1, 12);
        var booking = await SeedStayAsync(checkIn, checkIn.AddDays(10));
        var alerts = NewHarness(RomeToUtc(checkIn.AddDays(-1), 6));
        await alerts.RunHourlyAsync(32); // up to 14:00 of the arrival day: guest data, then deadline approaching

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await new AlloggiatiWebService(db, NullLogger<AlloggiatiWebService>.Instance, alerts.Clock)
                .MarkSentManuallyAsync(booking.Id, checkIn);
        }

        await alerts.RunHourlyAsync(4 * 24);

        Assert.Equal(new[] { "guest-checkin-incomplete", "alloggiati-deadline" }, alerts.Emails.Queued.Select(e => e.Template));
    }

    [PostgresFact]
    public async Task RunAsync_TwoRunsAtOnceEveryHour_SendEachStageOnce()
    {
        var checkIn = Date(Year + 1, 2, 12);
        var bookings = new List<Guid>();
        for (var i = 0; i < 4; i++)
            bookings.Add((await SeedStayAsync(checkIn, checkIn.AddDays(10))).Id);
        var alerts = NewHarness(RomeToUtc(checkIn.AddDays(-1), 6));

        for (var run = 0; run < 48; run++)
        {
            await Task.WhenAll(alerts.RunOnceAsync(), alerts.RunOnceAsync());
            alerts.Clock.Advance(TimeSpan.FromHours(1));
        }

        Assert.Equal(bookings.Count * 3, alerts.Emails.Queued.Count);
        foreach (var bookingId in bookings)
        {
            Assert.Equal(
                new[] { "guest-data-missing", "alloggiati-deadline", "alloggiati-overdue" },
                alerts.Push.Sent.Where(p => p.BookingId == bookingId).Select(p => p.Type));
            Assert.Equal(3, (await StateAsync(bookingId, StayAlertType.AlloggiatiDeadline)).AlertCount);
        }
    }

    [PostgresFact]
    public async Task ClaimAsync_TenConcurrentClaimsOfTheSameStage_OnlyOneWins()
    {
        var checkIn = Date(Year + 1, 3, 12);
        var booking = await SeedStayAsync(checkIn, checkIn.AddDays(3));
        var step = new StayAlertStep(
            StayAlertType.AlloggiatiDeadline,
            checkIn,
            AlloggiatiAlertStage.DeadlineApproaching,
            new StayAlert(booking.Id, StayAlertKind.AlloggiatiDeadlineApproaching));
        var now = RomeToUtc(checkIn, 12);
        var scopes = Enumerable.Range(0, 10).Select(_ => _factory.Services.CreateScope()).ToList();
        try
        {
            var claims = await Task.WhenAll(scopes.Select(scope =>
            {
                var service = NewHarness(now).CreateService(scope.ServiceProvider.GetRequiredService<AppDbContext>());
                return service.ClaimAsync(booking, step, now, CancellationToken.None);
            }));

            Assert.Equal(1, claims.Count(claimed => claimed));
        }
        finally
        {
            scopes.ForEach(scope => scope.Dispose());
        }

        var state = await StateAsync(booking.Id, StayAlertType.AlloggiatiDeadline);
        Assert.Equal(AlloggiatiAlertStage.DeadlineApproaching, state.Stage);
        Assert.Equal(1, state.AlertCount);
    }

    [PostgresFact]
    public async Task RunAsync_RunLockHeldByAnotherSession_SkipsWithoutSending()
    {
        var checkIn = Date(Year + 1, 4, 12);
        await SeedStayAsync(checkIn, checkIn.AddDays(3));
        var alerts = NewHarness(RomeToUtc(checkIn, 12));
        string connectionString;
        using (var scope = _factory.Services.CreateScope())
            connectionString = scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.GetConnectionString()!;

        await using (var other = new NpgsqlConnection(connectionString))
        {
            await other.OpenAsync();
            await using (var lockCommand = new NpgsqlCommand("SELECT pg_advisory_lock(@scope, @key)", other))
            {
                lockCommand.Parameters.AddWithValue("scope", (int)PostgresAdvisoryLocks.Scope.StayAlertsRun);
                lockCommand.Parameters.AddWithValue("key", PostgresAdvisoryLocks.Hash("stay-alerts"));
                await lockCommand.ExecuteNonQueryAsync();
            }

            var blocked = await alerts.RunOnceAsync();

            Assert.True(blocked.Skipped);
            Assert.Empty(alerts.Emails.Queued);
            await using var unlock = new NpgsqlCommand("SELECT pg_advisory_unlock_all()", other);
            await unlock.ExecuteNonQueryAsync();
        }

        var afterRelease = await alerts.RunOnceAsync();

        Assert.False(afterRelease.Skipped);
        Assert.Equal(1, afterRelease.Sent);
        Assert.Equal("alloggiati-deadline", Assert.Single(alerts.Emails.Queued).Template);
    }

    private AlertHarness NewHarness(DateTime startUtc, StayAlertOptions? options = null) =>
        new(_factory, new FakeTimeProvider(new DateTimeOffset(startUtc, TimeSpan.Zero)), options ?? new StayAlertOptions());

    private async Task<Booking> SeedStayAsync(
        DateTime checkIn,
        DateTime checkOut,
        BookingStatus status = BookingStatus.Confirmed,
        bool guestDataComplete = false,
        AlloggiatiWebStatus? reportStatus = null)
    {
        var property = await _factory.SeedPropertyAsync($"auth0|co10-{Guid.NewGuid():N}");
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var guest = new Guest
        {
            OrgId = property.OrgId,
            FirstName = "Anna",
            LastName = "Verdi",
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
        };
        db.AddRange(guest, booking);

        if (guestDataComplete)
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
                DocumentType = GuestDocumentType.IdentityCard,
                DocumentNumber = "CA12345AB",
                DocumentIssuePlaceName = "Milano",
            });
        }

        if (reportStatus is { } reported)
        {
            db.AlloggiatiWebReports.Add(new AlloggiatiWebReport
            {
                BookingId = booking.Id,
                GuestId = guest.Id,
                OrgId = property.OrgId,
                Status = reported,
                ConfirmationNumber = reported == AlloggiatiWebStatus.Inviato ? "RIC-CO10-1" : null,
                ErrorMessage = reported == AlloggiatiWebStatus.Errore ? "alloggiati_transmission_error" : null,
                ManuallyCompleted = reported == AlloggiatiWebStatus.InviatoManualmente,
                ReportedAt = AlloggiatiStatusSent(reported) ? checkIn : null,
            });
        }

        await db.SaveChangesAsync();
        return booking;
    }

    private static bool AlloggiatiStatusSent(AlloggiatiWebStatus status) =>
        status is AlloggiatiWebStatus.Inviato or AlloggiatiWebStatus.InviatoManualmente;

    private async Task UpdateBookingAsync(Guid bookingId, Action<Booking> change)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var booking = await db.Bookings.IgnoreQueryFilters().SingleAsync(b => b.Id == bookingId);
        change(booking);
        await db.SaveChangesAsync();
    }

    private async Task<StayAlertState> StateAsync(Guid bookingId, StayAlertType type)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.StayAlertStates.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(s => s.BookingId == bookingId && s.Type == type);
    }

    private async Task<bool> HasStateAsync(Guid bookingId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.StayAlertStates.IgnoreQueryFilters().AnyAsync(s => s.BookingId == bookingId);
    }

    private async Task<(HttpClient Host, Guid PropertyId)> HostWithPropertyAsync(string? timeZone = null)
    {
        var hostId = $"auth0|co10-{Guid.NewGuid():N}";
        var property = await _factory.SeedPropertyAsync(hostId);
        if (timeZone is not null)
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var stored = await db.Properties.IgnoreQueryFilters().SingleAsync(p => p.Id == property.Id);
            stored.Timezone = timeZone;
            await db.SaveChangesAsync();
        }

        return (_factory.CreateAuthenticatedClient(hostId, HostRole), property.Id);
    }

    private static async Task<Guid> CreateManualBookingAsync(HttpClient host, Guid propertyId, DateTime checkIn, DateTime checkOut)
    {
        var response = await host.PostAsJsonAsync("/api/bookings", new
        {
            propertyId,
            checkInDate = checkIn.ToString("yyyy-MM-dd"),
            checkOutDate = checkOut.ToString("yyyy-MM-dd"),
            numberOfGuests = 2,
            guest = new
            {
                firstName = "Mario",
                lastName = "Rossi",
                email = $"mario.{Guid.NewGuid():N}@example.com",
                phone = "+393331234567",
                country = "Italia",
            },
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Confirmed", body.GetProperty("status").GetString());
        return body.GetProperty("id").GetGuid();
    }

    private static DateTime Date(int year, int month, int day) => new(year, month, day, 0, 0, 0, DateTimeKind.Utc);

    private static DateTime RomeToUtc(DateTime date, int hour) =>
        TimeZoneInfo.ConvertTimeToUtc(
            new DateTime(date.Year, date.Month, date.Day, hour, 0, 0, DateTimeKind.Unspecified),
            RomeCalendar.TimeZone);

    /// <summary>
    /// The stay alert service as the hourly job builds it, on the factory's database, with a recording email queue and
    /// push service and a clock the test moves.
    /// </summary>
    private sealed class AlertHarness(CasazenWebApplicationFactory factory, FakeTimeProvider clock, StayAlertOptions options)
    {
        public FakeTimeProvider Clock { get; } = clock;

        public RecordingEmailQueue Emails { get; } = new();

        public RecordingPushService Push { get; } = new(clock);

        public async Task RunHourlyAsync(int runs)
        {
            for (var run = 0; run < runs; run++)
            {
                await RunOnceAsync();
                Clock.Advance(TimeSpan.FromHours(1));
            }
        }

        public async Task<StayAlertRunResult> RunOnceAsync()
        {
            using var scope = factory.Services.CreateScope();
            return await CreateService(scope.ServiceProvider.GetRequiredService<AppDbContext>()).RunAsync();
        }

        public StayAlertService CreateService(AppDbContext db) =>
            new(
                db,
                new AlloggiatiWebService(db, NullLogger<AlloggiatiWebService>.Instance, Clock),
                new NotificationService(db, Emails, Push, EmailTestHelpers.Links(), NullLogger<NotificationService>.Instance),
                Options.Create(options),
                Options.Create(new ComplianceOptions { CheckoutReminderHourLocal = CheckoutReminderHour }),
                NullLogger<StayAlertService>.Instance,
                Clock);
    }

    /// <summary>Records the booking pushes queued, with the time of the (fake) clock.</summary>
    private sealed class RecordingPushService(FakeTimeProvider clock) : IPushNotificationService
    {
        private readonly List<(string Type, Guid BookingId, DateTime At)> _sent = [];

        public IReadOnlyList<(string Type, Guid BookingId, DateTime At)> Sent
        {
            get
            {
                lock (_sent)
                    return _sent.ToList();
            }
        }

        public bool Enqueue(string deliveryKey, PushAudience audience, PushNotificationPayload payload)
        {
            Assert.Equal(PushAudience.BookingHosts(payload.BookingId!.Value), audience);
            Record(payload.Type, payload.BookingId.Value);
            return true;
        }

        private void Record(string type, Guid bookingId)
        {
            lock (_sent)
                _sent.Add((type, bookingId, clock.GetUtcNow().UtcDateTime));
        }
    }
}
