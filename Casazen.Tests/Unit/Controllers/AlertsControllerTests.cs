using System.Security.Claims;
using System.Text.Json;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Infrastructure.Data;
using Casazen.Tests.Unit.Services;
using Casazen.Web.Controllers;
using Casazen.Web.DTOs.Alerts;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Controllers;

/// <summary>
/// ADR-025 (E01/F03/US01/T03): the popup alerts of the caller. The proofs of its rules on EF InMemory, on the world of
/// <see cref="HostScopeScenario"/> (an org with a property given to the collaborator «Solo alcuni», one not given, and another org): who
/// sees which alert and what the answer carries (permission, privacy), and who may answer which alert.
/// </summary>
public class AlertsControllerTests : IAsyncLifetime
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    private readonly AppDbContext _db = HostScopeScenario.NewInMemoryDb();
    private readonly Casazen.Tests.Unit.FakeTimeProvider _clock = new(HostScopeScenario.Now);
    private HostScopeWorld _world = null!;

    public async Task InitializeAsync() => _world = await HostScopeScenario.SeedAsync(_db);

    public async Task DisposeAsync() => await _db.DisposeAsync();

    /// <summary>
    /// Proof (permission, privacy; ADR-025-R4, R5): through <c>GET /api/me/alerts/pending</c> the collaborator «Solo alcuni» sees only its
    /// own pending alerts about a booking or a house it was given, never the ones about a house it was not given or about the org;
    /// the owner sees its own alerts about the whole org and never another org's; nobody sees another user's alerts; dismissed and
    /// snoozed alerts are not listed; the order is urgency first (High, Normal, Low), then the oldest; and the answer is the five
    /// references and nothing else.
    /// </summary>
    [Fact]
    public async Task Alert_HostScopeFiltering_Test()
    {
        var now = HostScopeScenario.Now.UtcDateTime;
        var grantedBooking = await BookingOfAsync(_world.Granted);
        var hiddenBooking = await BookingOfAsync(_world.Hidden);
        var foreignBooking = await BookingOfAsync(_world.OtherOrgProperty);
        var orgLevel = Guid.NewGuid();

        // The collaborator's own alerts.
        var onGrantedBooking = await AddAsync(_world.CollaboratorId, PopupAlert.BookingEntityType, grantedBooking, AlertUrgency.High, now.AddMinutes(-30));
        var onGrantedHouse = await AddAsync(_world.CollaboratorId, PopupAlert.PropertyEntityType, _world.Granted.Id, AlertUrgency.Normal, now.AddMinutes(-40));
        await AddAsync(_world.CollaboratorId, PopupAlert.BookingEntityType, hiddenBooking, AlertUrgency.High, now.AddMinutes(-30));
        await AddAsync(_world.CollaboratorId, PopupAlert.PropertyEntityType, _world.Hidden.Id, AlertUrgency.Low, now.AddMinutes(-30));
        await AddAsync(_world.CollaboratorId, "ChannelConnection", orgLevel, AlertUrgency.High, now.AddMinutes(-30));
        await AddAsync(_world.CollaboratorId, PopupAlert.BookingEntityType, grantedBooking, AlertUrgency.High, now.AddMinutes(-30), dismissedAt: now.AddMinutes(-5));
        await AddAsync(_world.CollaboratorId, PopupAlert.BookingEntityType, grantedBooking, AlertUrgency.Low, now.AddMinutes(-30), remindAt: now.AddHours(1));

        // The owner's own alerts (and one of the same user in another org).
        var ownerOnHiddenBooking = await AddAsync(_world.OwnerUserId, PopupAlert.BookingEntityType, hiddenBooking, AlertUrgency.Low, now.AddMinutes(-60));
        var ownerOrgLevel = await AddAsync(_world.OwnerUserId, "ChannelConnection", orgLevel, AlertUrgency.High, now.AddMinutes(-10));
        var ownerOnGrantedBooking = await AddAsync(_world.OwnerUserId, PopupAlert.BookingEntityType, grantedBooking, AlertUrgency.High, now.AddMinutes(-20));
        await AddAsync(_world.OwnerUserId, PopupAlert.BookingEntityType, foreignBooking, AlertUrgency.High, now.AddMinutes(-20), orgId: _world.OtherOrgId);
        await AddAsync(_world.OwnerUserId, PopupAlert.BookingEntityType, grantedBooking, AlertUrgency.Normal, now.AddMinutes(-20), remindAt: now.AddMinutes(1));

        var collaborator = Controller(_world.CollaboratorId, _world.Restricted);
        var collaboratorAlerts = Items(await collaborator.GetPending(Ct));
        Assert.Equal([onGrantedBooking, onGrantedHouse], collaboratorAlerts.Select(a => a.AlertId));

        var owner = Controller(_world.OwnerUserId, _world.OrgWide);
        var ownerAlerts = Items(await owner.GetPending(Ct));
        Assert.Equal([ownerOnGrantedBooking, ownerOrgLevel, ownerOnHiddenBooking], ownerAlerts.Select(a => a.AlertId));
        Assert.Equal(["High", "High", "Low"], ownerAlerts.Select(a => a.Urgency));

        // Nothing but five references, and nobody's id.
        foreach (var answer in new[] { collaboratorAlerts, ownerAlerts })
        {
            var json = JsonSerializer.Serialize(answer, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            using var document = JsonDocument.Parse(json);
            foreach (var alert in document.RootElement.EnumerateArray())
            {
                Assert.Equal(
                    ["alertId", "alertType", "entityId", "entityType", "urgency"],
                    alert.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
            }

            Assert.DoesNotContain(_world.CollaboratorId, json, StringComparison.Ordinal);
            Assert.DoesNotContain(_world.OwnerUserId, json, StringComparison.Ordinal);
        }

        // A caller with no scope (a deactivated member, another org) gets nothing, never the org.
        var nobody = Controller(_world.CollaboratorId, scope: null);
        Assert.Empty(Items(await nobody.GetPending(Ct)));
    }

    /// <summary>
    /// Proof (permission, trust; ADR-025-R10): the caller dismisses or snoozes its own alerts only (another user's alert and an unknown id
    /// are a 404 and change nothing); a snooze past the legal deadline of the alert is a 400 <c>cannot_snooze_past_deadline</c> and changes
    /// nothing; a snooze before it sets <c>RemindAt</c> and clears <c>DeliveredAt</c>; dismissing twice keeps the first instant.
    /// </summary>
    [Fact]
    public async Task Alert_DismissAndRemind_OnlyOwnAlerts_AndNeverPastTheLegalDeadline_Test()
    {
        var now = HostScopeScenario.Now.UtcDateTime;
        var booking = await BookingOfAsync(_world.Granted);
        var deadline = now.AddHours(2);
        var own = await AddAsync(
            _world.CollaboratorId, PopupAlert.BookingEntityType, booking, AlertUrgency.High, now.AddMinutes(-10), deadline: deadline, deliveredAt: now.AddMinutes(-9));
        var ownerOwn = await AddAsync(_world.OwnerUserId, PopupAlert.BookingEntityType, booking, AlertUrgency.High, now.AddMinutes(-10));
        var collaborator = Controller(_world.CollaboratorId, _world.Restricted);

        // Somebody else's alert and an unknown one: a 404 whatever the call, and nothing changes.
        await Assert.ThrowsAsync<NotFoundException>(() => collaborator.Dismiss(ownerOwn, null, Ct));
        await Assert.ThrowsAsync<NotFoundException>(() => collaborator.Remind(ownerOwn, Request(now.AddHours(1)), Ct));
        await Assert.ThrowsAsync<NotFoundException>(() => collaborator.Dismiss(Guid.NewGuid(), null, Ct));
        var untouched = await ReadAsync(ownerOwn);
        Assert.Null(untouched.DismissedAt);
        Assert.Null(untouched.RemindAt);

        // After the deadline: refused, unchanged.
        var tooLate = Assert.IsType<ObjectResult>(await collaborator.Remind(own, Request(deadline.AddMinutes(1)), Ct));
        Assert.Equal(StatusCodes.Status400BadRequest, tooLate.StatusCode);
        Assert.Equal(AlertsController.CannotSnoozePastDeadlineCode, Assert.IsAssignableFrom<ProblemDetails>(tooLate.Value).Extensions["code"]);
        var refused = await ReadAsync(own);
        Assert.Null(refused.RemindAt);
        Assert.NotNull(refused.DeliveredAt);

        // Up to the deadline: snoozed, and to be pushed again when the snooze ends.
        Assert.IsType<NoContentResult>(await collaborator.Remind(own, Request(deadline), Ct));
        var snoozed = await ReadAsync(own);
        Assert.Equal(deadline, snoozed.RemindAt);
        Assert.Null(snoozed.DeliveredAt);
        Assert.Empty(Items(await collaborator.GetPending(Ct)));

        // Dismissed: never pending again, and a second dismissal keeps the first instant.
        Assert.IsType<NoContentResult>(await collaborator.Dismiss(own, null, Ct));
        _clock.Advance(TimeSpan.FromMinutes(5));
        Assert.IsType<NoContentResult>(await collaborator.Dismiss(own, new DismissRequest(), Ct));
        Assert.Equal(now, (await ReadAsync(own)).DismissedAt);
    }

    private AlertsController Controller(string userId, HostScope? scope)
    {
        var orgContext = new Mock<IOrgContextResolver>();
        orgContext.Setup(o => o.GetOrProvisionOrgIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(_world.OrgId);
        var scopes = new Mock<IHostScopeResolver>();
        scopes.Setup(s => s.ResolveAsync(userId, It.IsAny<IReadOnlySet<string>>(), _world.OrgId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(scope);

        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", userId)], "test")),
            RequestServices = new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider(),
        };
        return new AlertsController(_db, scopes.Object, orgContext.Object, _clock)
        {
            ControllerContext = new ControllerContext { HttpContext = http },
        };
    }

    private static RemindRequest Request(DateTime remindAt) => new() { RemindAt = new DateTimeOffset(remindAt, TimeSpan.Zero) };

    private async Task<Guid> BookingOfAsync(Property property) =>
        await _db.Bookings.AsNoTracking().Where(b => b.PropertyId == property.Id).OrderBy(b => b.CheckInDate).Select(b => b.Id).FirstAsync(Ct);

    private async Task<Guid> AddAsync(
        string userId,
        string entityType,
        Guid entityId,
        AlertUrgency urgency,
        DateTime createdAt,
        DateTime? dismissedAt = null,
        DateTime? remindAt = null,
        DateTime? deadline = null,
        DateTime? deliveredAt = null,
        Guid? orgId = null)
    {
        var alert = new PopupAlert
        {
            OrgId = orgId ?? _world.OrgId,
            UserId = userId,
            AlertType = AlertType.CheckInPending,
            EntityType = entityType,
            EntityId = entityId,
            Urgency = urgency,
            CreatedAt = createdAt,
            DismissedAt = dismissedAt,
            RemindAt = remindAt,
            Deadline = deadline,
            DeliveredAt = deliveredAt,
        };
        _db.PopupAlerts.Add(alert);
        await _db.SaveChangesAsync(Ct);
        _db.ChangeTracker.Clear();
        return alert.Id;
    }

    private async Task<PopupAlert> ReadAsync(Guid id) =>
        await _db.PopupAlerts.AsNoTracking().SingleAsync(a => a.Id == id, Ct);

    private static List<PendingAlertDto> Items(ActionResult<IReadOnlyList<PendingAlertDto>> result)
    {
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        return Assert.IsAssignableFrom<IEnumerable<PendingAlertDto>>(ok.Value).ToList();
    }
}
