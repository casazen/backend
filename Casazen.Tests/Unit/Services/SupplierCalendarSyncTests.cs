using System.Diagnostics.CodeAnalysis;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Http;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Email;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// FD-16 (A4-10, A9-32): the supplier iCal URL is validated when saved and downloaded only through the anti-SSRF
/// client; a failed sync stores a stable code, never the exception message. SU-15: the sync manages only the days of
/// the feed (source column), its state is Syncing until the queued job has run. PostgreSQL cases (queued job, empty
/// feed, batch isolation, migration): <c>SupplierCalendarSyncPostgresTests</c>.
/// </summary>
public class SupplierCalendarSyncTests
{
    [Theory]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("https://169.254.169.254/latest/meta-data/")]
    [InlineData("https://127.0.0.1/cal.ics")]
    [InlineData("https://10.0.0.1/cal.ics")]
    [InlineData("https://[::1]/cal.ics")]
    [InlineData("https://postgres.railway.internal/cal.ics")]
    [InlineData("ftp://example.com/cal.ics")]
    public async Task UpdateCalendarSyncAsync_NotAnExternalHttpsUrl_ThrowsInvalidUrlAndKeepsProfile(string url)
    {
        await using var db = CreateDb();
        var orgId = await SeedProfileAsync(db);
        var service = CreateSupplierService(db);

        var ex = await Assert.ThrowsAsync<DomainRuleException>(
            () => service.UpdateCalendarSyncAsync(orgId, CalendarSyncType.ICalFeed, url, calendarSyncError: null));

        Assert.Equal(ICalErrorCodes.InvalidUrl, ex.Code);
        var profile = await db.SupplierProfiles.SingleAsync();
        Assert.Null(profile.IcalFeedUrl);
        Assert.Equal(CalendarSyncType.None, profile.CalendarSyncType);
    }

    [Fact]
    public async Task UpdateCalendarSyncAsync_PublicHttpsUrl_SavesTrimmedUrl()
    {
        await using var db = CreateDb();
        var orgId = await SeedProfileAsync(db);
        var service = CreateSupplierService(db);

        var profile = await service.UpdateCalendarSyncAsync(
            orgId, CalendarSyncType.ICalFeed, " https://calendar.google.com/calendar/ical/x/basic.ics ", calendarSyncError: null);

        Assert.NotNull(profile);
        Assert.Equal("https://calendar.google.com/calendar/ical/x/basic.ics", profile.IcalFeedUrl);
        Assert.Equal(CalendarSyncType.ICalFeed, profile.CalendarSyncType);
        Assert.Equal(SupplierCalendarSyncStatus.Syncing, profile.CalendarSyncStatus);
    }

    [Theory]
    [InlineData(ExternalFetchFailure.BlockedDestination, ICalErrorCodes.Unreachable)]
    [InlineData(ExternalFetchFailure.Unreachable, ICalErrorCodes.Unreachable)]
    [InlineData(ExternalFetchFailure.TooLarge, ICalErrorCodes.TooLarge)]
    [InlineData(ExternalFetchFailure.InvalidUrl, ICalErrorCodes.InvalidUrl)]
    public async Task SyncIcalFeedAsync_DownloadFails_StoresStableCodeNotExceptionMessage(
        ExternalFetchFailure failure,
        string expectedCode)
    {
        await using var db = CreateDb();
        var orgId = await SeedProfileAsync(db, "http://169.254.169.254/latest/meta-data/");
        var service = CreateCalendarSyncService(db, new FakeExternalHttpClient(null, failure));

        await service.SyncIcalFeedAsync(orgId);

        var profile = await db.SupplierProfiles.SingleAsync();
        Assert.Equal(expectedCode, profile.CalendarSyncError);
        Assert.Equal(SupplierCalendarSyncStatus.Failure, profile.CalendarSyncStatus);
        Assert.NotNull(profile.CalendarLastSyncAt);
    }

    [Fact]
    public async Task SyncIcalFeedAsync_ContentIsNotICal_StoresInvalidFormat()
    {
        await using var db = CreateDb();
        var orgId = await SeedProfileAsync(db, "https://feeds.example.com/cal.ics");
        var service = CreateCalendarSyncService(db, new FakeExternalHttpClient("<html>login</html>"));

        await service.SyncIcalFeedAsync(orgId);

        var profile = await db.SupplierProfiles.SingleAsync();
        Assert.Equal(ICalErrorCodes.InvalidFormat, profile.CalendarSyncError);
    }

    [Fact]
    public async Task SyncIcalFeedAsync_ValidFeed_MarksBusyDatesAndClearsError()
    {
        const string ics = """
            BEGIN:VCALENDAR
            VERSION:2.0
            BEGIN:VEVENT
            UID:busy-1
            DTSTART:20260710T100000Z
            DTEND:20260711T100000Z
            SUMMARY:Busy
            END:VEVENT
            END:VCALENDAR
            """;
        await using var db = CreateDb();
        var orgId = await SeedProfileAsync(db, "https://feeds.example.com/cal.ics", previousError: ICalErrorCodes.Unreachable);
        var service = CreateCalendarSyncService(db, new FakeExternalHttpClient(ics));

        await service.SyncIcalFeedAsync(orgId);

        var profile = await db.SupplierProfiles.SingleAsync();
        Assert.Null(profile.CalendarSyncError);
        Assert.Equal(SupplierCalendarSyncStatus.Success, profile.CalendarSyncStatus);
        // 10 July 10:00Z → 11 July 10:00Z: the supplier is busy on both days it touches.
        Assert.Equal(
            [(new DateOnly(2026, 7, 10), false, SupplierAvailabilitySource.ICalFeed),
             (new DateOnly(2026, 7, 11), false, SupplierAvailabilitySource.ICalFeed)],
            await DaysAsync(db));
    }

    // PC-10 (A9-13): a valid calendar without events is a successful sync, not "invalid feed".
    [Fact]
    public async Task SyncIcalFeedAsync_ValidFeedWithoutEvents_ClearsErrorAndSetsLastSync()
    {
        const string ics = """
            BEGIN:VCALENDAR
            VERSION:2.0
            PRODID:-//Google Inc//Google Calendar 70.9054//EN
            END:VCALENDAR
            """;
        await using var db = CreateDb();
        var orgId = await SeedProfileAsync(db, "https://feeds.example.com/cal.ics", previousError: ICalErrorCodes.InvalidFormat);
        var service = CreateCalendarSyncService(db, new FakeExternalHttpClient(ics));

        await service.SyncIcalFeedAsync(orgId);

        var profile = await db.SupplierProfiles.SingleAsync();
        Assert.Null(profile.CalendarSyncError);
        Assert.NotNull(profile.CalendarLastSyncAt);
        Assert.Empty(await db.SupplierAvailability.ToListAsync());
    }

    // PC-10 (A2-23): an all-day event blocks its days only (DTEND is exclusive), a cancelled one blocks nothing.
    [Fact]
    public async Task SyncIcalFeedAsync_AllDayAndCancelledEvents_MarksOnlyTheActiveDays()
    {
        const string ics = """
            BEGIN:VCALENDAR
            VERSION:2.0
            BEGIN:VEVENT
            UID:busy-all-day
            DTSTART;VALUE=DATE:20261010
            DTEND;VALUE=DATE:20261012
            END:VEVENT
            BEGIN:VEVENT
            UID:cancelled
            DTSTART;VALUE=DATE:20261020
            DTEND;VALUE=DATE:20261021
            STATUS:CANCELLED
            END:VEVENT
            END:VCALENDAR
            """;
        await using var db = CreateDb();
        var orgId = await SeedProfileAsync(db, "https://feeds.example.com/cal.ics");
        var service = CreateCalendarSyncService(db, new FakeExternalHttpClient(ics));

        await service.SyncIcalFeedAsync(orgId);

        var busy = (await db.SupplierAvailability.Where(a => !a.Available).ToListAsync()).Select(a => a.Date).Order().ToList();
        Assert.Equal([new DateOnly(2026, 10, 10), new DateOnly(2026, 10, 11)], busy);
    }

    // SU-15: a day the supplier left open but busy in the feed becomes a feed day; a manual closure stays manual.
    [Fact]
    public async Task SyncIcalFeedAsync_ManualDaysOnBusyDates_OpenDayBecomesFeedDayClosedDayStaysManual()
    {
        await using var db = CreateDb();
        var orgId = await SeedProfileAsync(db, "https://feeds.example.com/cal.ics");
        await SeedDayAsync(db, orgId, new DateOnly(2026, 10, 10), available: true, SupplierAvailabilitySource.Manual);
        await SeedDayAsync(db, orgId, new DateOnly(2026, 10, 11), available: false, SupplierAvailabilitySource.Manual);
        var service = CreateCalendarSyncService(db, new FakeExternalHttpClient(Feed(AllDayEvent("busy", "20261010", "20261012"))));

        await service.SyncIcalFeedAsync(orgId);

        Assert.Equal(
            [(new DateOnly(2026, 10, 10), false, SupplierAvailabilitySource.ICalFeed),
             (new DateOnly(2026, 10, 11), false, SupplierAvailabilitySource.Manual)],
            await DaysAsync(db));
    }

    // An unreadable event is not proof that the commitment is gone: no feed day is freed at that run.
    [Fact]
    public async Task SyncIcalFeedAsync_FeedWithUnreadableEvent_KeepsTheFeedDays()
    {
        const string ics = """
            BEGIN:VCALENDAR
            VERSION:2.0
            BEGIN:VEVENT
            UID:broken
            SUMMARY:No start
            END:VEVENT
            END:VCALENDAR
            """;
        await using var db = CreateDb();
        var orgId = await SeedProfileAsync(db, "https://feeds.example.com/cal.ics");
        await SeedDayAsync(db, orgId, new DateOnly(2026, 10, 10), available: false, SupplierAvailabilitySource.ICalFeed);
        var service = CreateCalendarSyncService(db, new FakeExternalHttpClient(ics));

        await service.SyncIcalFeedAsync(orgId);

        Assert.Equal([(new DateOnly(2026, 10, 10), false, SupplierAvailabilitySource.ICalFeed)], await DaysAsync(db));
        Assert.Equal(SupplierCalendarSyncStatus.Success, (await db.SupplierProfiles.SingleAsync()).CalendarSyncStatus);
    }

    // The URL was replaced while the old one was downloading: the old result is discarded (the new URL has its own run).
    [Fact]
    public async Task SyncIcalFeedAsync_UrlChangedDuringDownload_WritesNothing()
    {
        await using var db = CreateDb();
        var orgId = await SeedProfileAsync(db, "https://feeds.example.com/old.ics");
        var client = new FakeExternalHttpClient(Feed(AllDayEvent("busy", "20261010", "20261011")))
        {
            OnDownload = () =>
            {
                var profile = db.SupplierProfiles.Single();
                profile.IcalFeedUrl = "https://feeds.example.com/new.ics";
                db.SaveChanges();
            },
        };
        var service = CreateCalendarSyncService(db, client);

        await service.SyncIcalFeedAsync(orgId);

        Assert.Empty(await db.SupplierAvailability.ToListAsync());
        Assert.Null((await db.SupplierProfiles.SingleAsync()).CalendarLastSyncAt);
    }

    [Fact]
    public async Task RequestSyncAsync_FeedConfigured_MarksSyncingAndAsksToQueueOnce()
    {
        await using var db = CreateDb();
        var orgId = await SeedProfileAsync(db, "https://feeds.example.com/cal.ics");
        var service = CreateCalendarSyncService(db, new FakeExternalHttpClient(null));

        var first = await service.RequestSyncAsync(orgId);
        var second = await service.RequestSyncAsync(orgId);

        Assert.True(first.Queue);
        Assert.False(second.Queue);
        Assert.Equal(SupplierCalendarSyncStatus.Syncing, second.Profile!.CalendarSyncStatus);
    }

    [Fact]
    public async Task RequestSyncAsync_NoFeed_ThrowsSupplierNoFeed()
    {
        await using var db = CreateDb();
        var orgId = await SeedProfileAsync(db);
        var service = CreateCalendarSyncService(db, new FakeExternalHttpClient(null));

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() => service.RequestSyncAsync(orgId));

        Assert.Equal(ICalFeedErrorCodes.SupplierNoFeed, ex.Code);
        Assert.Equal(SupplierCalendarSyncStatus.None, (await db.SupplierProfiles.SingleAsync()).CalendarSyncStatus);
    }

    // The availability page saves every visible day: a feed day saved with its value stays a feed day, a changed day
    // becomes manual.
    [Fact]
    public async Task UpdateAvailabilityAsync_FeedDaySavedUnchangedOrChanged_KeepsOrTakesOverTheSource()
    {
        await using var db = CreateDb();
        var orgId = await SeedProfileAsync(db, "https://feeds.example.com/cal.ics");
        await SeedDayAsync(db, orgId, new DateOnly(2026, 10, 10), available: false, SupplierAvailabilitySource.ICalFeed);
        await SeedDayAsync(db, orgId, new DateOnly(2026, 10, 11), available: false, SupplierAvailabilitySource.ICalFeed);
        var service = CreateSupplierService(db);

        var updated = await service.UpdateAvailabilityAsync(
            orgId,
            [(new DateOnly(2026, 10, 10), false), (new DateOnly(2026, 10, 11), true), (new DateOnly(2026, 10, 12), true)]);

        Assert.Equal(3, updated);
        Assert.Equal(
            [(new DateOnly(2026, 10, 10), false, SupplierAvailabilitySource.ICalFeed),
             (new DateOnly(2026, 10, 11), true, SupplierAvailabilitySource.Manual),
             (new DateOnly(2026, 10, 12), true, SupplierAvailabilitySource.Manual)],
            await DaysAsync(db));
    }

    private static readonly TimeProvider Clock = new FakeTimeProvider(new DateTimeOffset(2026, 9, 25, 10, 0, 0, TimeSpan.Zero));

    private static CalendarSyncService CreateCalendarSyncService(AppDbContext db, ISafeExternalHttpClient externalHttpClient) =>
        new(
            db,
            externalHttpClient,
            ICalTestServices.ImportService(Clock),
            Mock.Of<IServiceScopeFactory>(),
            NullLogger<CalendarSyncService>.Instance);

    private static string AllDayEvent(string uid, string start, string end) =>
        $"BEGIN:VEVENT\r\nUID:{uid}\r\nDTSTART;VALUE=DATE:{start}\r\nDTEND;VALUE=DATE:{end}\r\nEND:VEVENT\r\n";

    private static string Feed(params string[] events) =>
        "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Test//Test//EN\r\n" + string.Concat(events) + "END:VCALENDAR\r\n";

    private static async Task SeedDayAsync(AppDbContext db, Guid orgId, DateOnly date, bool available, SupplierAvailabilitySource source)
    {
        db.SupplierAvailability.Add(new SupplierAvailability { OrgId = orgId, Date = date, Available = available, Source = source });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    private static async Task<List<(DateOnly Date, bool Available, SupplierAvailabilitySource Source)>> DaysAsync(AppDbContext db) =>
        (await db.SupplierAvailability.AsNoTracking().OrderBy(a => a.Date).ToListAsync())
            .Select(a => (a.Date, a.Available, a.Source))
            .ToList();

    private static SupplierService CreateSupplierService(AppDbContext db) =>
        new(
            db,
            Mock.Of<IEmailQueue>(),
            EmailTestHelpers.Links(),
            new FakeExternalHttpClient(null),
            Options.Create(new SupplierRegistrationOptions()),
            NullLogger<SupplierService>.Instance);

    private static AppDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static async Task<Guid> SeedProfileAsync(AppDbContext db, string? feedUrl = null, string? previousError = null)
    {
        var profile = new SupplierProfile
        {
            OrgId = Guid.NewGuid(),
            LegalName = "Pulizie Test Srl",
            Phone = "+39 06 000000",
            Email = "supplier@test.com",
            IcalFeedUrl = feedUrl,
            CalendarSyncType = feedUrl is null ? CalendarSyncType.None : CalendarSyncType.ICalFeed,
            CalendarSyncError = previousError,
        };
        db.SupplierProfiles.Add(profile);
        await db.SaveChangesAsync();
        return profile.OrgId;
    }

    private sealed class FakeExternalHttpClient(string? content, ExternalFetchFailure? failure = null) : ISafeExternalHttpClient
    {
        /// <summary>Runs while the feed is "downloading" (e.g. the supplier replaces the URL meanwhile).</summary>
        public Action? OnDownload { get; init; }

        public bool TryValidateUrl(string? url, [NotNullWhen(true)] out Uri? uri) =>
            ExternalUrlPolicy.TryParse(url, [443], out uri);

        public Task<string> GetStringAsync(string url, CancellationToken cancellationToken = default)
        {
            if (failure is { } f)
                throw new ExternalFetchException(f, "Connection refused (169.254.169.254:80)");

            OnDownload?.Invoke();
            return Task.FromResult(content ?? string.Empty);
        }
    }
}
