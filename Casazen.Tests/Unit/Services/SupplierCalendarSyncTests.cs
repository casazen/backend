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
        var service = new CalendarSyncService(
            db, new FakeExternalHttpClient(null, failure), NullLogger<CalendarSyncService>.Instance);

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
        var service = new CalendarSyncService(
            db, new FakeExternalHttpClient("<html>login</html>"), NullLogger<CalendarSyncService>.Instance);

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
        var service = new CalendarSyncService(db, new FakeExternalHttpClient(ics), NullLogger<CalendarSyncService>.Instance);

        await service.SyncIcalFeedAsync(orgId);

        var profile = await db.SupplierProfiles.SingleAsync();
        Assert.Null(profile.CalendarSyncError);
        Assert.Contains(await db.SupplierAvailability.ToListAsync(), a => a.Date == new DateOnly(2026, 7, 10) && !a.Available);
    }

    private static SupplierService CreateSupplierService(AppDbContext db) =>
        new(db, Mock.Of<IEmailQueue>(), EmailTestHelpers.Links(), new FakeExternalHttpClient(null), NullLogger<SupplierService>.Instance);

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
