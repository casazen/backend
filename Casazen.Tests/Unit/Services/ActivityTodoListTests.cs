using System.Security.Claims;
using System.Text.Json;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Events;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Events;
using Casazen.Infrastructure.Services;
using Casazen.Web.Controllers;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// ADR-026 (E04/F01/US01/T01): the "To do" list, an extension of the internal activities. The proofs of its rules on EF
/// InMemory, on the world of <see cref="HostScopeScenario"/> (an org with a property given to the collaborator «Solo alcuni», one
/// not given, and another org): who sees which item and what the answer carries (permission, privacy), the org of an item
/// (data), and the auto-close that keeps the closed row (data).
/// </summary>
public class ActivityTodoListTests : IAsyncLifetime
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    private readonly AppDbContext _db = HostScopeScenario.NewInMemoryDb();
    private readonly Casazen.Tests.Unit.FakeTimeProvider _clock = new(HostScopeScenario.Now);
    private HostScopeWorld _world = null!;

    public async Task InitializeAsync() => _world = await HostScopeScenario.SeedAsync(_db);

    public async Task DisposeAsync() => await _db.DisposeAsync();

    /// <summary>
    /// Proof (permission, privacy; ADR-026-R2, R3): through <c>GET /api/me/activities</c> the collaborator «Solo alcuni» sees
    /// only the item of the property it was given, never the item of the property it was not given, the organization-level item
    /// or another org's; the owner sees the whole org and never another org's; neither answer carries the person the item is
    /// for, a rate or a note, and the collaborator cannot close an item it does not see.
    /// </summary>
    [Fact]
    public async Task Activity_HostScope_Filtering_Test()
    {
        var service = Service();
        var onGranted = await service.CreateAsync(CheckIn(_world.Granted.Id, Guid.NewGuid()) with { ForUserId = _world.CollaboratorId }, Ct);
        var onHidden = await service.CreateAsync(CheckIn(_world.Hidden.Id, Guid.NewGuid()), Ct);
        var ofTheOtherOrg = await service.CreateAsync(CheckIn(_world.OtherOrgProperty.Id, Guid.NewGuid()), Ct);
        var orgLevel = await service.CreateAsync(
            new CreateTodoRequest
            {
                Type = ActivityType.ReconnectChannelManager,
                Action = ActivityAction.ReconnectChannel,
                EntityType = "ChannelConnection",
                EntityId = Guid.NewGuid(),
                Urgency = ActivityUrgency.High,
                OrgId = _world.OrgId,
                Reason = "channel_disconnected",
                ForUserId = _world.OwnerUserId,
            },
            Ct);

        // The staff work on the granted property, with a rate and a note: never an item of the list.
        await service.CreateActivityAsync(
            _world.Granted.Id, _world.CollaboratorId, "Cleaning", HostScopeScenario.Now.UtcDateTime, 40m, notes: "Chiavi dal vicino");

        var collaborator = Controller(_world.CollaboratorId, _world.Restricted);
        var collaboratorItems = Items(await collaborator.List(Ct));
        Assert.Equal([onGranted], collaboratorItems.Select(i => i.Id));

        var owner = Controller(_world.OwnerUserId, _world.OrgWide);
        var ownerItems = Items(await owner.List(Ct));
        Assert.Equal(new[] { onGranted, onHidden, orgLevel }.Order(), ownerItems.Select(i => i.Id).Order());
        Assert.DoesNotContain(ofTheOtherOrg, ownerItems.Select(i => i.Id));

        foreach (var answer in new[] { collaboratorItems, ownerItems })
        {
            var json = JsonSerializer.Serialize(answer, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            foreach (var staffField in new[] { "staffUserId", "internalRate", "notes", "rechargeToOwner", "createdByUserId", "closedBy" })
                Assert.DoesNotContain($"\"{staffField}\"", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(_world.CollaboratorId, json, StringComparison.Ordinal);
            Assert.DoesNotContain(_world.OwnerUserId, json, StringComparison.Ordinal);
            Assert.DoesNotContain("Chiavi dal vicino", json, StringComparison.Ordinal);
        }

        await Assert.ThrowsAsync<NotFoundException>(() => collaborator.Close(onHidden, Ct));
        await Assert.ThrowsAsync<NotFoundException>(() => collaborator.Close(orgLevel, Ct));
        Assert.IsType<NoContentResult>(await collaborator.Close(onGranted, Ct));
        Assert.Empty(Items(await collaborator.List(Ct)));
        Assert.Equal(2, Items(await owner.List(Ct)).Count);
    }

    /// <summary>Proof (data, tenant): the org of an item is the org of its property, never one the request names.</summary>
    [Fact]
    public async Task CreateAsync_TheOrgOfAnItemIsTheOrgOfItsProperty_NeverTheOneOfTheRequest()
    {
        var service = Service();

        var id = await service.CreateAsync(CheckIn(_world.Granted.Id, Guid.NewGuid()), Ct);
        Assert.Equal(_world.OrgId, (await _db.InternalActivities.AsNoTracking().SingleAsync(a => a.Id == id)).OrgId);

        await Assert.ThrowsAsync<ArgumentException>(
            () => service.CreateAsync(CheckIn(_world.Granted.Id, Guid.NewGuid()) with { OrgId = _world.OtherOrgId }, Ct));
        await Assert.ThrowsAsync<ArgumentException>(
            () => service.CreateAsync(CheckIn(_world.Granted.Id, Guid.NewGuid()) with { PropertyId = null }, Ct));
        Assert.Equal(1, await _db.InternalActivities.CountAsync(a => a.Action != null));
    }

    /// <summary>
    /// Proof (data; ADR-026-R4): the check-in completed closes the check-in item of that booking only, once, by the system; the
    /// closed row is kept (it leaves the list), a second close changes nothing, and while the item was open raising it again
    /// gave back the same item.
    /// </summary>
    [Fact]
    public async Task AutoClose_CheckInCompleted_ClosesTheCheckInItemOfThatBookingOnce_AndKeepsTheClosedRow()
    {
        var service = Service();
        var bookingId = Guid.NewGuid();
        var item = await service.CreateAsync(CheckIn(_world.Granted.Id, bookingId), Ct);
        Assert.Equal(item, await service.CreateAsync(CheckIn(_world.Granted.Id, bookingId), Ct));
        var otherBooking = await service.CreateAsync(CheckIn(_world.Granted.Id, Guid.NewGuid()), Ct);
        var checkOut = await service.CreateAsync(
            CheckIn(_world.Granted.Id, bookingId) with { Type = ActivityType.CheckOutPending, Action = ActivityAction.CompleteCheckOut },
            Ct);

        var handler = new ActivityAutoCloseHandler(_db, service, NullLogger<ActivityAutoCloseHandler>.Instance);
        var checkedIn = new DomainEvent(
            Guid.NewGuid(), _world.OrgId, DomainEventType.CheckInCompleted, bookingId, HostScopeScenario.Now.UtcDateTime, "{}");
        await handler.HandleAsync(checkedIn, Ct);

        _clock.Advance(TimeSpan.FromHours(1));
        await handler.HandleAsync(checkedIn, Ct);
        await service.CloseAsync(item, "auth0|someone-else", Ct);

        _db.ChangeTracker.Clear();
        var closed = await _db.InternalActivities.AsNoTracking().SingleAsync(a => a.Id == item);
        Assert.Equal(InternalActivity.StatusCompleted, closed.Status);
        Assert.Equal(HostScopeScenario.Now.UtcDateTime, closed.CompletedAt);
        Assert.Equal(InternalActivity.ClosedBySystem, closed.ClosedBy);

        var open = await service.GetPendingAsync(_world.OrgWide, Ct);
        Assert.Equal(new[] { otherBooking, checkOut }.Order(), open.Select(a => a.Id).Order());
        Assert.NotEqual(item, await service.CreateAsync(CheckIn(_world.Granted.Id, bookingId), Ct));
    }

    private InternalActivityService Service() => new(_db, NullLogger<InternalActivityService>.Instance, _clock);

    private static CreateTodoRequest CheckIn(Guid propertyId, Guid bookingId) => new()
    {
        Type = ActivityType.CheckInPending,
        Action = ActivityAction.StartCheckIn,
        EntityType = InternalActivity.BookingEntityType,
        EntityId = bookingId,
        Urgency = ActivityUrgency.High,
        PropertyId = propertyId,
        Deadline = HostScopeScenario.Now.UtcDateTime.AddDays(1),
    };

    private ActivitiesController Controller(string userId, HostScope scope)
    {
        var orgContext = new Mock<IOrgContextResolver>();
        orgContext.Setup(o => o.GetOrProvisionOrgIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(_world.OrgId);
        var scopes = new Mock<IHostScopeResolver>();
        scopes.Setup(s => s.ResolveAsync(userId, It.IsAny<IReadOnlySet<string>>(), _world.OrgId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(scope);

        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", userId)], "test")),
        };
        return new ActivitiesController(Service(), scopes.Object, orgContext.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = http },
        };
    }

    private static List<TodoActivityDto> Items(ActionResult<IReadOnlyList<TodoActivityDto>> result)
    {
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        return Assert.IsAssignableFrom<IEnumerable<TodoActivityDto>>(ok.Value).ToList();
    }
}
