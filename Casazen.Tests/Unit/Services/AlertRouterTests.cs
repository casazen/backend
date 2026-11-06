using System.Globalization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// ADR-025 (E01/F03/US01/T02): the alert router. The proof of its rule on EF InMemory, with the clock under the test's control: during the
/// quiet hours of the user a legal alert (Alloggiati, ISTAT) goes through at once and every other alert is stored and held back until the
/// quiet hours end on the clock of Rome, and nobody but the alerted user's host org is written to.
/// </summary>
public class AlertRouterTests : IAsyncLifetime
{
    private const string UserId = "auth0|anna";
    private static readonly CancellationToken Ct = CancellationToken.None;

    private readonly string _database = Guid.NewGuid().ToString();
    private readonly RecordingPopupAlertPublisher _publisher = new();
    private Guid _orgId;

    public async Task InitializeAsync()
    {
        await using var db = NewDb();
        var org = HostScopeScenario.NewOrg("Host", "host-alert-router");
        db.Orgs.Add(org);
        db.Users.Add(HostScopeScenario.NewUser(UserId, org.Id, UserRole.PropertyOwner));
        await db.SaveChangesAsync();
        _orgId = org.Id;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>
    /// Proof (trust, permission; ADR-025-R3, R5): at 23:15 in Rome, inside the default quiet hours (22:00-08:00), the Alloggiati and ISTAT
    /// alerts are delivered at once and the others are stored with <c>RemindAt</c> = 08:00 of the morning and nothing sent; every row is
    /// in the host org of the alerted user and carries references only; an alert already open is not written twice, and a user with no
    /// host org is told nothing.
    /// </summary>
    [Fact]
    public async Task AlertRouter_QuietHours_LegalOverride_Test()
    {
        // 22:15 UTC on 5 November 2026 is 23:15 in Rome (CET).
        var now = new DateTimeOffset(2026, 11, 5, 22, 15, 0, TimeSpan.Zero);
        var router = Router(new Casazen.Tests.Unit.FakeTimeProvider(now));
        var booking = Guid.NewGuid();
        var deadline = now.UtcDateTime.AddHours(30);

        await router.RouteAlertAsync(AlertType.CheckInPending, booking, "Booking", UserId, AlertUrgency.High, null, Ct);
        await router.RouteAlertAsync(AlertType.AlloggiatiDeadline, booking, "Booking", UserId, AlertUrgency.High, deadline, Ct);
        await router.RouteAlertAsync(AlertType.IstatDeadline, booking, "Booking", UserId, AlertUrgency.Normal, deadline, Ct);

        var rows = await ReadAlertsAsync();
        Assert.Equal(3, rows.Count);
        Assert.All(rows, row =>
        {
            Assert.Equal(_orgId, row.OrgId);
            Assert.Equal(UserId, row.UserId);
            Assert.Equal("Booking", row.EntityType);
            Assert.Equal(booking, row.EntityId);
            Assert.Null(row.DismissedAt);
        });

        // Not legal: stored, held back until 08:00 in Rome (07:00 UTC), nothing sent.
        var held = Assert.Single(rows, row => row.AlertType == AlertType.CheckInPending);
        Assert.Equal(new DateTime(2026, 11, 6, 7, 0, 0, DateTimeKind.Utc), held.RemindAt);
        Assert.Null(held.DeliveredAt);
        Assert.Null(held.Deadline);
        Assert.DoesNotContain(_publisher.Sent, sent => sent.AlertId == held.Id);

        // Legal: not held back, delivered now with the deadline, pushed to the user.
        var legal = rows.Where(row => PopupAlert.IsLegal(row.AlertType)).ToList();
        Assert.Equal(2, legal.Count);
        foreach (var row in legal)
        {
            Assert.Null(row.RemindAt);
            Assert.Equal(now.UtcDateTime, row.DeliveredAt);
            Assert.Equal(deadline, row.Deadline);
            Assert.Contains(_publisher.Sent, sent => sent == new PublishedAlert(UserId, row.Id, row.AlertType, "Booking", booking, row.Urgency));
        }

        Assert.Equal(2, _publisher.Sent.Count);

        // The preferences were created with the defaults the first time an alert was routed.
        var preferences = Assert.Single(await ReadPreferencesAsync());
        Assert.Equal(_orgId, preferences.OrgId);
        Assert.Equal(UserId, preferences.UserId);
        Assert.Equal(new TimeOnly(22, 0), preferences.QuietHoursStart);
        Assert.Equal(new TimeOnly(8, 0), preferences.QuietHoursEnd);
        Assert.True(preferences.LegalAlertsOverrideQuietHours);

        // The same open alert again, and a user who is in no host org: nothing is written, nothing is sent.
        await router.RouteAlertAsync(AlertType.CheckInPending, booking, "Booking", UserId, AlertUrgency.High, null, Ct);
        await router.RouteAlertAsync(AlertType.AlloggiatiDeadline, booking, "Booking", "auth0|nobody", AlertUrgency.High, deadline, Ct);
        Assert.Equal(3, (await ReadAlertsAsync()).Count);
        Assert.Equal(2, _publisher.Sent.Count);
        Assert.Single(await ReadPreferencesAsync());
    }

    /// <summary>
    /// The end of the quiet hours is the wall-clock time of Rome, so it is 07:00 UTC in winter and 06:00 UTC in summer, also on the nights the
    /// clocks change; outside the quiet hours the alert is delivered at once.
    /// </summary>
    [Theory]
    [InlineData("2026-11-05T22:15:00Z", "2026-11-06T07:00:00Z")] // 23:15 in Rome: the quiet hours end tomorrow morning
    [InlineData("2026-11-06T04:30:00Z", "2026-11-06T07:00:00Z")] // 05:30 in Rome: they end this morning
    [InlineData("2026-10-24T20:30:00Z", "2026-10-25T07:00:00Z")] // 22:30 in Rome (summer time), the clocks go back that night
    [InlineData("2026-03-28T21:30:00Z", "2026-03-29T06:00:00Z")] // 22:30 in Rome (winter time), the clocks go forward that night
    [InlineData("2026-11-05T11:00:00Z", null)] // 12:00 in Rome: no quiet hours
    public async Task RouteAlertAsync_AnAlertThatIsNotLegal_WaitsForTheEndOfTheQuietHoursOnTheClockOfRome(string at, string? remindAt)
    {
        var now = DateTimeOffset.Parse(at, CultureInfo.InvariantCulture);
        var router = Router(new Casazen.Tests.Unit.FakeTimeProvider(now));

        await router.RouteAlertAsync(AlertType.ChannelSyncError, Guid.NewGuid(), "Property", UserId, AlertUrgency.Normal, null, Ct);

        var row = Assert.Single(await ReadAlertsAsync());
        if (remindAt is null)
        {
            Assert.Null(row.RemindAt);
            Assert.Equal(now.UtcDateTime, row.DeliveredAt);
            Assert.Single(_publisher.Sent);
        }
        else
        {
            Assert.Equal(DateTimeOffset.Parse(remindAt, CultureInfo.InvariantCulture).UtcDateTime, row.RemindAt);
            Assert.Null(row.DeliveredAt);
            Assert.Empty(_publisher.Sent);
        }
    }

    private AlertRouter Router(TimeProvider clock) => new(NewDb(), _publisher, clock, NullLogger<AlertRouter>.Instance);

    private AppDbContext NewDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(_database).Options);

    private async Task<List<PopupAlert>> ReadAlertsAsync()
    {
        await using var db = NewDb();
        return await db.PopupAlerts.AsNoTracking().OrderBy(a => a.CreatedAt).ThenBy(a => a.AlertType).ToListAsync();
    }

    private async Task<List<UserNotificationPreferences>> ReadPreferencesAsync()
    {
        await using var db = NewDb();
        return await db.UserNotificationPreferences.AsNoTracking().ToListAsync();
    }
}
