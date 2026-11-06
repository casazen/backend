using System.Reflection;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Jobs;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.BackgroundJobs;
using Casazen.Web.BackgroundJobs;
using Hangfire;
using Hangfire.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// ADR-025-R6 (E01/F03/US01/T02): the job that pushes the alerts that fell due. A user who keeps the app open all night must see the
/// alert the quiet hours held back at 08:00, and the alert it snoozed when the snooze ends: once, and not again until it is snoozed again.
/// </summary>
public class PopupAlertDueJobTests : IAsyncLifetime
{
    private const string UserId = "auth0|anna";
    private static readonly CancellationToken Ct = CancellationToken.None;

    private readonly string _database = Guid.NewGuid().ToString();
    private readonly RecordingPopupAlertPublisher _publisher = new();
    private Guid _orgId;

    public async Task InitializeAsync()
    {
        await using var db = NewDb();
        var org = HostScopeScenario.NewOrg("Host", "host-alert-job");
        db.Orgs.Add(org);
        db.Users.Add(HostScopeScenario.NewUser(UserId, org.Id, UserRole.PropertyOwner));
        await db.SaveChangesAsync();
        _orgId = org.Id;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public void Configure_RegistersTheDueJobEveryMinute_NeverTwiceAtOnce()
    {
        var scheduled = new Dictionary<string, (Job Job, string Cron, RecurringJobOptions Options)>();
        var manager = new Mock<IRecurringJobManager>();
        manager
            .Setup(m => m.AddOrUpdate(It.IsAny<string>(), It.IsAny<Job>(), It.IsAny<string>(), It.IsAny<RecurringJobOptions>()))
            .Callback<string, Job, string, RecurringJobOptions>((id, job, cron, options) => scheduled[id] = (job, cron, options));

        RecurringJobsRegistration.Configure(manager.Object, RecurringJobsFeatureFlagTests.Flags(otaPartnerApi: false));

        var registered = scheduled["popup-alert-due"];
        Assert.Equal("*/1 * * * *", registered.Cron);
        Assert.Equal(TimeZoneInfo.Utc, registered.Options.TimeZone);
        Assert.Equal(typeof(PopupAlertDueJob), registered.Job.Type);
        var concurrency = typeof(PopupAlertDueJob).GetCustomAttribute<DisableConcurrentExecutionAttribute>();
        Assert.NotNull(concurrency);
        Assert.InRange(concurrency.TimeoutSec, 1, 59);
    }

    /// <summary>
    /// Proof (trust; ADR-025-R6): an alert held back by the quiet hours is pushed once when 08:00 comes; a snooze (<c>RemindAt</c> moved,
    /// <c>DeliveredAt</c> cleared, as <c>PATCH …/remind</c> does) is pushed once when it ends; a dismissed alert and one that was pushed at
    /// once are never pushed by the job.
    /// </summary>
    [Fact]
    public async Task PopupAlertDueJob_PushesAHeldBackOrSnoozedAlertOnceWhenItFallsDue_AndNotAgainUntilItIsSnoozedAgain()
    {
        var clock = new Casazen.Tests.Unit.FakeTimeProvider(new DateTimeOffset(2026, 11, 5, 22, 15, 0, TimeSpan.Zero));
        var booking = Guid.NewGuid();
        await new AlertRouter(NewDb(), _publisher, clock, NullLogger<AlertRouter>.Instance)
            .RouteAlertAsync(AlertType.CheckInPending, booking, "Booking", UserId, AlertUrgency.High, null, Ct);
        var held = Assert.Single(await ReadAlertsAsync());
        Assert.Equal(new DateTime(2026, 11, 6, 7, 0, 0, DateTimeKind.Utc), held.RemindAt);

        // Two alerts the job must leave alone: one dismissed after it fell due, one that was never held back.
        await AddAsync(new PopupAlert
        {
            OrgId = _orgId,
            UserId = UserId,
            AlertType = AlertType.ComplianceIssue,
            EntityType = "Property",
            EntityId = Guid.NewGuid(),
            CreatedAt = clock.GetUtcNow().UtcDateTime,
            RemindAt = new DateTime(2026, 11, 6, 6, 0, 0, DateTimeKind.Utc),
            DismissedAt = new DateTime(2026, 11, 6, 6, 30, 0, DateTimeKind.Utc),
        });
        await AddAsync(new PopupAlert
        {
            OrgId = _orgId,
            UserId = UserId,
            AlertType = AlertType.BookingNeedsReview,
            EntityType = "Booking",
            EntityId = Guid.NewGuid(),
            CreatedAt = clock.GetUtcNow().UtcDateTime,
            DeliveredAt = clock.GetUtcNow().UtcDateTime,
        });

        // Still night: not due.
        clock.SetUtcNow(new DateTimeOffset(2026, 11, 6, 6, 59, 0, TimeSpan.Zero));
        await RunAsync(clock);
        Assert.Empty(_publisher.Sent);

        // 08:00 in Rome has passed: pushed to the user, once.
        clock.SetUtcNow(new DateTimeOffset(2026, 11, 6, 7, 0, 30, TimeSpan.Zero));
        await RunAsync(clock);
        var pushed = Assert.Single(_publisher.Sent);
        Assert.Equal(new PublishedAlert(UserId, held.Id, AlertType.CheckInPending, "Booking", booking, AlertUrgency.High), pushed);
        Assert.Equal(new DateTime(2026, 11, 6, 7, 0, 30, DateTimeKind.Utc), (await ReadAsync(held.Id)).DeliveredAt);

        clock.SetUtcNow(new DateTimeOffset(2026, 11, 6, 7, 1, 30, TimeSpan.Zero));
        await RunAsync(clock);
        Assert.Single(_publisher.Sent);

        // The user snoozes it until 08:00 UTC (what PATCH …/remind does): not pushed before, pushed once at the end of the snooze.
        await using (var db = NewDb())
        {
            var alert = await db.PopupAlerts.SingleAsync(a => a.Id == held.Id, Ct);
            alert.RemindAt = new DateTime(2026, 11, 6, 8, 0, 0, DateTimeKind.Utc);
            alert.DeliveredAt = null;
            await db.SaveChangesAsync(Ct);
        }

        clock.SetUtcNow(new DateTimeOffset(2026, 11, 6, 7, 59, 0, TimeSpan.Zero));
        await RunAsync(clock);
        Assert.Single(_publisher.Sent);

        clock.SetUtcNow(new DateTimeOffset(2026, 11, 6, 8, 0, 20, TimeSpan.Zero));
        await RunAsync(clock);
        await RunAsync(clock);
        Assert.Equal(2, _publisher.Sent.Count);
        Assert.All(_publisher.Sent, sent => Assert.Equal(pushed, sent));
    }

    private Task RunAsync(TimeProvider clock) =>
        new PopupAlertDueJob(NewDb(), _publisher, clock, NullLogger<PopupAlertDueJob>.Instance).ExecuteAsync(Ct);

    private AppDbContext NewDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(_database).Options);

    private async Task AddAsync(PopupAlert alert)
    {
        await using var db = NewDb();
        db.PopupAlerts.Add(alert);
        await db.SaveChangesAsync(Ct);
    }

    private async Task<List<PopupAlert>> ReadAlertsAsync()
    {
        await using var db = NewDb();
        return await db.PopupAlerts.AsNoTracking().ToListAsync(Ct);
    }

    private async Task<PopupAlert> ReadAsync(Guid id)
    {
        await using var db = NewDb();
        return await db.PopupAlerts.AsNoTracking().SingleAsync(a => a.Id == id, Ct);
    }
}
