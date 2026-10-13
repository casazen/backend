using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Http;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Integration.Postgres;
using Casazen.Web.BackgroundJobs;
using Hangfire;
using Hangfire.Common;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;
// OrgEntity is a global alias defined in Casazen.Tests.csproj: OrgEntity = global::Casazen.Core.Entities.Org

namespace Casazen.Tests.Integration;

/// <summary>
/// SU-15 (A4-11, A9-14) on PostgreSQL: saving the supplier's iCal URL queues a Hangfire job instead of a
/// <c>Task.Run</c> on the request's scoped services, the job updates the availability; the sync frees only the days of
/// the feed; a failing feed never stops the others; two runs on the same supplier never collide. Downloads go through a
/// scripted <see cref="ISafeExternalHttpClient"/>: no network. Migration: <see cref="SupplierCalendarSyncSourceMigrationPostgresTests"/>.
/// </summary>
public class SupplierCalendarSyncPostgresTests : IClassFixture<SupplierCalendarSyncPostgresTests.Factory>
{
    private readonly Factory _factory;

    public SupplierCalendarSyncPostgresTests(Factory factory) => _factory = factory;

    [PostgresFact]
    public async Task SetIcalFeed_SupplierInActivation_QueuesTheSyncJobWhichThenUpdatesTheAvailability()
    {
        // A supplier still in the activation wizard (Pending): before SU-15 its first sync always failed.
        var supplier = await SeedSupplierAsync(SupplierStatus.Pending, feedUrl: null);
        await SeedDayAsync(supplier.OrgId, new DateOnly(2026, 10, 12), available: false, SupplierAvailabilitySource.Manual);
        var url = $"https://calendar-{Guid.NewGuid():N}.example.com/basic.ics";
        _factory.Feeds[url] = () => Feed(Event("job-1", "20261010", "20261012"));
        using var client = _factory.CreateAuthenticatedClient(supplier.UserId, "Supplier");

        var response = await client.PutAsJsonAsync("/api/supplier/calendar/ical", new { icalFeedUrl = url });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var accepted = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Syncing", accepted.GetProperty("lastSyncStatus").GetString());
        Assert.Equal(JsonValueKind.Null, accepted.GetProperty("calendarLastSyncAt").ValueKind);
        var job = Assert.Single(QueuedSyncs(supplier.OrgId));
        // Nothing was downloaded by the request or behind it: the only sync is the queued job.
        await Task.Delay(TimeSpan.FromMilliseconds(300));
        Assert.Equal(0, _factory.Downloads(url));
        Assert.Equal([(new DateOnly(2026, 10, 12), false, SupplierAvailabilitySource.Manual)], await DaysAsync(supplier.OrgId));

        await PerformAsync(job);

        Assert.Equal(1, _factory.Downloads(url));
        var status = await client.GetFromJsonAsync<JsonElement>("/api/supplier/calendar/status");
        Assert.Equal("Success", status.GetProperty("lastSyncStatus").GetString());
        Assert.Equal(JsonValueKind.Null, status.GetProperty("calendarSyncErrorCode").ValueKind);
        Assert.NotEqual(JsonValueKind.Null, status.GetProperty("calendarLastSyncAt").ValueKind);
        var availability = await client.GetFromJsonAsync<JsonElement>("/api/supplier/availability?from=2026-10-09&to=2026-10-13");
        Assert.Equal(
            [("2026-10-10", false), ("2026-10-11", false), ("2026-10-12", false)],
            availability.GetProperty("dates").EnumerateArray()
                .Select(d => (d.GetProperty("date").GetString()!, d.GetProperty("available").GetBoolean())));
        Assert.Equal(
            [(new DateOnly(2026, 10, 10), false, SupplierAvailabilitySource.ICalFeed),
             (new DateOnly(2026, 10, 11), false, SupplierAvailabilitySource.ICalFeed),
             (new DateOnly(2026, 10, 12), false, SupplierAvailabilitySource.Manual)],
            await DaysAsync(supplier.OrgId));
    }

    [PostgresFact]
    public async Task SyncCalendarNow_FeedConfigured_QueuesOneJobUntilItHasRun()
    {
        var supplier = await SeedSupplierAsync(SupplierStatus.Active, $"https://now-{Guid.NewGuid():N}.example.com/cal.ics");
        _factory.Feeds[supplier.FeedUrl!] = () => Feed();
        using var client = _factory.CreateAuthenticatedClient(supplier.UserId, "Supplier");

        var first = await client.PostAsync("/api/supplier/calendar/sync", content: null);
        var second = await client.PostAsync("/api/supplier/calendar/sync", content: null);

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, second.StatusCode);
        Assert.Equal("Syncing", (await second.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("lastSyncStatus").GetString());
        var job = Assert.Single(QueuedSyncs(supplier.OrgId));

        await PerformAsync(job);
        var third = await client.PostAsync("/api/supplier/calendar/sync", content: null);

        Assert.Equal(HttpStatusCode.Accepted, third.StatusCode);
        Assert.Equal(2, QueuedSyncs(supplier.OrgId).Count);
    }

    [PostgresFact]
    public async Task SyncCalendarNow_NoFeed_Returns422WithCodeAndLocalizedMessage()
    {
        var supplier = await SeedSupplierAsync(SupplierStatus.Active, feedUrl: null);
        using var client = _factory.CreateAuthenticatedClient(supplier.UserId, "Supplier");
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en");

        var response = await client.PostAsync("/api/supplier/calendar/sync", content: null);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(ICalFeedErrorCodes.SupplierNoFeed, problem.GetProperty("code").GetString());
        Assert.StartsWith("You have not linked an iCal calendar yet", problem.GetProperty("detail").GetString());
        Assert.Empty(QueuedSyncs(supplier.OrgId));
    }

    // PC-10 note (A9-13): an empty feed used to leave its busy days for ever. Now it frees the days it had marked and
    // only those: the supplier's own closures and openings stay.
    [PostgresFact]
    public async Task SyncIcalFeedAsync_ValidFeedWithoutEvents_FreesTheFeedDaysAndKeepsTheManualDays()
    {
        var supplier = await SeedSupplierAsync(SupplierStatus.Active, $"https://empty-{Guid.NewGuid():N}.example.com/cal.ics");
        await SeedDayAsync(supplier.OrgId, new DateOnly(2026, 10, 10), available: false, SupplierAvailabilitySource.ICalFeed);
        await SeedDayAsync(supplier.OrgId, new DateOnly(2026, 10, 11), available: false, SupplierAvailabilitySource.ICalFeed);
        await SeedDayAsync(supplier.OrgId, new DateOnly(2026, 10, 12), available: false, SupplierAvailabilitySource.Manual);
        await SeedDayAsync(supplier.OrgId, new DateOnly(2026, 10, 13), available: true, SupplierAvailabilitySource.Manual);
        _factory.Feeds[supplier.FeedUrl!] = () => Feed();

        await SyncAsync(supplier.OrgId);

        Assert.Equal(
            [(new DateOnly(2026, 10, 12), false, SupplierAvailabilitySource.Manual),
             (new DateOnly(2026, 10, 13), true, SupplierAvailabilitySource.Manual)],
            await DaysAsync(supplier.OrgId));
        var profile = await ProfileAsync(supplier.OrgId);
        Assert.Equal(SupplierCalendarSyncStatus.Success, profile.CalendarSyncStatus);
        Assert.Null(profile.CalendarSyncError);
    }

    // A failing feed (unexpected client failure, download refused, not iCal) never stops the others; pending suppliers
    // with a feed are synced too, suspended ones are not. A second run writes nothing new (idempotent).
    [PostgresFact]
    public async Task SyncAllIcalFeedsAsync_FeedsFailing_DoNotStopTheOthersAndARepeatedRunChangesNothing()
    {
        var run = Guid.NewGuid().ToString("N");
        var broken = await SeedSupplierAsync(SupplierStatus.Active, $"https://broken-{run}.example.com/cal.ics", lastSyncAt: null);
        var unreachable = await SeedSupplierAsync(SupplierStatus.Active, $"https://unreachable-{run}.example.com/cal.ics");
        var notICal = await SeedSupplierAsync(SupplierStatus.Active, $"https://html-{run}.example.com/cal.ics");
        var active = await SeedSupplierAsync(SupplierStatus.Active, $"https://active-{run}.example.com/cal.ics");
        var pending = await SeedSupplierAsync(SupplierStatus.Pending, $"https://pending-{run}.example.com/cal.ics");
        var suspended = await SeedSupplierAsync(SupplierStatus.Suspended, $"https://suspended-{run}.example.com/cal.ics");
        await SeedDayAsync(broken.OrgId, new DateOnly(2026, 11, 1), available: false, SupplierAvailabilitySource.ICalFeed);
        _factory.Feeds[broken.FeedUrl!] = () => throw new InvalidOperationException("unexpected client failure");
        _factory.Feeds[notICal.FeedUrl!] = () => "<html>login</html>";
        _factory.Feeds[active.FeedUrl!] = () => Feed(Event("a-1", "20261105", "20261107"));
        _factory.Feeds[pending.FeedUrl!] = () => Feed(Event("p-1", "20261120", "20261121"));
        _factory.Feeds[suspended.FeedUrl!] = () => Feed(Event("s-1", "20261120", "20261121"));

        await RunBatchAsync();
        var afterFirstRun = await DaysAsync(active.OrgId);
        await RunBatchAsync();

        var brokenProfile = await ProfileAsync(broken.OrgId);
        Assert.Equal(SupplierCalendarSyncStatus.Failure, brokenProfile.CalendarSyncStatus);
        Assert.Equal(ICalErrorCodes.SyncFailed, brokenProfile.CalendarSyncError);
        Assert.Equal([(new DateOnly(2026, 11, 1), false, SupplierAvailabilitySource.ICalFeed)], await DaysAsync(broken.OrgId));
        Assert.Equal(ICalErrorCodes.Unreachable, (await ProfileAsync(unreachable.OrgId)).CalendarSyncError);
        Assert.Equal(ICalErrorCodes.InvalidFormat, (await ProfileAsync(notICal.OrgId)).CalendarSyncError);

        Assert.Equal(SupplierCalendarSyncStatus.Success, (await ProfileAsync(active.OrgId)).CalendarSyncStatus);
        Assert.Equal(
            [(new DateOnly(2026, 11, 5), false, SupplierAvailabilitySource.ICalFeed),
             (new DateOnly(2026, 11, 6), false, SupplierAvailabilitySource.ICalFeed)],
            afterFirstRun);
        Assert.Equal(afterFirstRun, await DaysAsync(active.OrgId));

        Assert.Equal(SupplierCalendarSyncStatus.Success, (await ProfileAsync(pending.OrgId)).CalendarSyncStatus);
        Assert.Equal([(new DateOnly(2026, 11, 20), false, SupplierAvailabilitySource.ICalFeed)], await DaysAsync(pending.OrgId));

        Assert.Equal(0, _factory.Downloads(suspended.FeedUrl!));
        Assert.Equal(SupplierCalendarSyncStatus.None, (await ProfileAsync(suspended.OrgId)).CalendarSyncStatus);
        Assert.Empty(await DaysAsync(suspended.OrgId));
    }

    // The 15-minute job and a queued first sync can run on the same supplier at once: the advisory lock makes the
    // second wait and see the days of the first, instead of inserting the same days again (unique OrgId + Date).
    [PostgresFact]
    public async Task SyncIcalFeedAsync_TwoConcurrentRuns_WriteEachDayOnceAndSucceed()
    {
        var supplier = await SeedSupplierAsync(SupplierStatus.Active, $"https://concurrent-{Guid.NewGuid():N}.example.com/cal.ics");
        var bothDownloading = new Barrier(2);
        _factory.Feeds[supplier.FeedUrl!] = () =>
        {
            // Both runs hold the downloaded feed before either writes.
            bothDownloading.SignalAndWait(TimeSpan.FromSeconds(30));
            return Feed(Event("long", "20261201", "20261221"));
        };

        try
        {
            await Task.WhenAll(RunAsync(), RunAsync());
        }
        finally
        {
            _factory.Feeds.TryRemove(supplier.FeedUrl!, out _); // later batch runs of the class must not wait on the barrier
        }

        var days = await DaysAsync(supplier.OrgId);
        Assert.Equal(20, days.Count);
        Assert.All(days, d => Assert.Equal(SupplierAvailabilitySource.ICalFeed, d.Source));
        var profile = await ProfileAsync(supplier.OrgId);
        Assert.Equal(SupplierCalendarSyncStatus.Success, profile.CalendarSyncStatus);
        Assert.Null(profile.CalendarSyncError);

        async Task RunAsync()
        {
            await Task.Yield();
            await SyncAsync(supplier.OrgId);
        }
    }

    private static string Event(string uid, string start, string end) =>
        $"BEGIN:VEVENT\r\nUID:{uid}\r\nDTSTART;VALUE=DATE:{start}\r\nDTEND;VALUE=DATE:{end}\r\nEND:VEVENT\r\n";

    private static string Feed(params string[] events) =>
        "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Test//Test//EN\r\n" + string.Concat(events) + "END:VCALENDAR\r\n";

    private List<Job> QueuedSyncs(Guid orgId) =>
        _factory.BackgroundJobClientMock.Invocations
            .Where(i => i.Method.Name == nameof(IBackgroundJobClient.Create))
            .Select(i => (Job)i.Arguments[0])
            .Where(job => job.Type == typeof(IcalSupplierSyncJob)
                          && job.Method.Name == nameof(IcalSupplierSyncJob.SyncSupplierAsync)
                          && (Guid)job.Args[0] == orgId)
            .ToList();

    // Runs a queued job the way the Hangfire server does: a new DI scope, the job class activated from it, the method
    // invoked with the queued arguments.
    private async Task PerformAsync(Job job)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var instance = ActivatorUtilities.CreateInstance(scope.ServiceProvider, job.Type);
        await (Task)job.Method.Invoke(instance, job.Args.ToArray())!;
    }

    private async Task SyncAsync(Guid orgId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<CalendarSyncService>().SyncIcalFeedAsync(orgId);
    }

    private async Task RunBatchAsync()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        await ActivatorUtilities.CreateInstance<IcalSupplierSyncJob>(scope.ServiceProvider).ExecuteAsync();
    }

    private async Task<SupplierProfile> ProfileAsync(Guid orgId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.SupplierProfiles.AsNoTracking().SingleAsync(sp => sp.OrgId == orgId);
    }

    private async Task<List<(DateOnly Date, bool Available, SupplierAvailabilitySource Source)>> DaysAsync(Guid orgId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return (await db.SupplierAvailability.AsNoTracking().Where(a => a.OrgId == orgId).OrderBy(a => a.Date).ToListAsync())
            .Select(a => (a.Date, a.Available, a.Source))
            .ToList();
    }

    private async Task SeedDayAsync(Guid orgId, DateOnly date, bool available, SupplierAvailabilitySource source)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.SupplierAvailability.Add(new SupplierAvailability { OrgId = orgId, Date = date, Available = available, Source = source });
        await db.SaveChangesAsync();
    }

    private async Task<SeededSupplier> SeedSupplierAsync(SupplierStatus status, string? feedUrl, DateTime? lastSyncAt = null)
    {
        var userId = $"auth0|su15-{Guid.NewGuid():N}";
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var org = new OrgEntity
        {
            Name = "Pulizie SU15 Srl",
            Slug = $"su15-{Guid.NewGuid():N}",
            DisplayName = "Pulizie SU15 Srl",
            ContactEmail = $"{Guid.NewGuid():N}@su15.test",
            OrgType = OrgType.Supplier,
            PlanTier = PlanTier.Starter,
        };
        db.Orgs.Add(org);
        db.Users.Add(new User
        {
            Id = userId,
            Email = org.ContactEmail,
            FirstName = "Test",
            LastName = "Supplier",
            OrgId = org.Id,
            IsActive = true,
        });
        db.SupplierProfiles.Add(new SupplierProfile
        {
            OrgId = org.Id,
            Status = status,
            Email = org.ContactEmail,
            LegalName = "Pulizie SU15 Srl",
            Phone = "+39 06 999999",
            ComuniJson = "[\"H501\"]",
            CalendarSyncType = feedUrl is null ? CalendarSyncType.None : CalendarSyncType.ICalFeed,
            IcalFeedUrl = feedUrl,
            CalendarLastSyncAt = lastSyncAt ?? (feedUrl is null ? null : DateTime.UtcNow.AddHours(-1)),
        });

        await db.SaveChangesAsync();
        return new SeededSupplier(userId, org.Id, feedUrl);
    }

    private sealed record SeededSupplier(string UserId, Guid OrgId, string? FeedUrl);

    /// <summary>The integration host with the download client replaced by <see cref="Feeds"/> (URL → body).</summary>
    public sealed class Factory : CasazenWebApplicationFactory
    {
        private readonly ConcurrentDictionary<string, int> _downloads = new(StringComparer.Ordinal);

        public ConcurrentDictionary<string, Func<string>> Feeds { get; } = new(StringComparer.Ordinal);

        public int Downloads(string url) => _downloads.GetValueOrDefault(url);

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ISafeExternalHttpClient>();
                services.AddSingleton<ISafeExternalHttpClient>(new ScriptedExternalHttpClient(Feeds, _downloads));
            });
        }
    }

    private sealed class ScriptedExternalHttpClient(
        ConcurrentDictionary<string, Func<string>> feeds,
        ConcurrentDictionary<string, int> downloads) : ISafeExternalHttpClient
    {
        public bool TryValidateUrl(string? url, [NotNullWhen(true)] out Uri? uri) =>
            ExternalUrlPolicy.TryParse(url, [443], out uri);

        // Unknown URLs (feeds seeded by other tests of the class) answer like an unreachable host.
        public Task<string> GetStringAsync(string url, CancellationToken cancellationToken = default)
        {
            downloads.AddOrUpdate(url, 1, (_, count) => count + 1);
            return feeds.TryGetValue(url, out var body)
                ? Task.Run(body, cancellationToken)
                : throw new ExternalFetchException(ExternalFetchFailure.Unreachable, "not scripted");
        }
    }
}
