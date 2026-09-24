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
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// FD-16 (A4-10, A9-32): the supplier iCal URL is validated when saved and downloaded only through the anti-SSRF
/// client; a failed sync stores a stable code, never the exception message.
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
        Assert.Contains(await db.SupplierAvailability.ToListAsync(), a => a.Date == new DateOnly(2026, 7, 10) && !a.Available);
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

    private static CalendarSyncService CreateCalendarSyncService(AppDbContext db, ISafeExternalHttpClient externalHttpClient) =>
        new(db, externalHttpClient, ICalTestServices.ImportService(), NullLogger<CalendarSyncService>.Instance);

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
        public bool TryValidateUrl(string? url, [NotNullWhen(true)] out Uri? uri) =>
            ExternalUrlPolicy.TryParse(url, [443], out uri);

        public Task<string> GetStringAsync(string url, CancellationToken cancellationToken = default) =>
            failure is { } f
                ? throw new ExternalFetchException(f, "Connection refused (169.254.169.254:80)")
                : Task.FromResult(content ?? string.Empty);
    }
}
