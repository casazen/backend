using System.Reflection;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Push;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Xunit;
using static Casazen.Tests.Unit.Push.InAppNotificationTestKit;

namespace Casazen.Tests.Unit.Push;

/// <summary>
/// UI-12a: the job queued next to every push writes one notification per user of the push's audience, once per event, with
/// nothing but the type and the id of what happened. These run on the in-memory provider (no unique index, no foreign key): the
/// race on the index is proved on PostgreSQL in <c>InAppNotificationsPostgresTests</c>.
/// </summary>
public class InAppNotificationJobTests : IDisposable
{
    private readonly InAppNotificationTestKit _kit = new();

    public void Dispose() => _kit.Dispose();

    [Fact]
    public async Task CreateAsync_BookingHosts_WritesOneRowForTheMemberInChargeAndOneForTheOwner_WithNoDevice()
    {
        var world = await _kit.SeedWorldAsync();
        var bookingId = world.BookingId;

        await _kit.Job().CreateAsync(
            "booking:abc:new",
            Queued(PushAudience.BookingHosts(world.BookingId), PushTypes.NewBooking, entityId: bookingId),
            CancellationToken.None);

        var rows = await _kit.RowsAsync();
        Assert.Equal([World.ManagerId, World.OwnerId], rows.Select(r => r.UserId));
        Assert.All(rows, row =>
        {
            Assert.Equal(world.OrgId, row.OrgId);
            Assert.Equal(PushTypes.NewBooking, row.Type);
            Assert.Equal(bookingId, row.EntityId);
            Assert.Equal("booking:abc:new", row.DeliveryKey);
            Assert.Equal(Now, row.CreatedAt);
            Assert.Null(row.ReadAt);
        });
        Assert.Empty(_kit.Db.DeviceRegistrations);
    }

    [Fact]
    public async Task CreateAsync_PropertyHosts_WritesTheSameTwoRows()
    {
        var world = await _kit.SeedWorldAsync();

        await _kit.Job().CreateAsync(
            "service-request:xyz:Completato",
            Queued(PushAudience.PropertyHosts(world.PropertyId), PushTypes.ServiceRequestCompleted, Guid.NewGuid()),
            CancellationToken.None);

        var rows = await _kit.RowsAsync();
        Assert.Equal([World.ManagerId, World.OwnerId], rows.Select(r => r.UserId));
        Assert.All(rows, row => Assert.Equal(world.OrgId, row.OrgId));
    }

    [Fact]
    public async Task CreateAsync_SupplierOrg_WritesAnOwnRowForEachActiveUserLinkedToTheSupplier_UnderTheSupplierOrg()
    {
        var world = await _kit.SeedWorldAsync();

        await _kit.Job().CreateAsync(
            "service-request:xyz:created",
            Queued(PushAudience.SupplierOrg(world.SupplierOrgId), PushTypes.ServiceRequestCreated, Guid.NewGuid()),
            CancellationToken.None);

        var rows = await _kit.RowsAsync();
        Assert.Equal([World.SupplierHostTooId, World.SupplierMemberId], rows.Select(r => r.UserId));
        Assert.All(rows, row => Assert.Equal(world.SupplierOrgId, row.OrgId));
    }

    [Fact]
    public async Task CreateAsync_ADeactivatedMemberIsNeverTold_NobodyLeftIsNotAnError()
    {
        var world = await _kit.SeedWorldAsync();
        foreach (var member in await _kit.Db.OrgMembers.ToListAsync())
            member.Status = OrgMemberStatus.Deactivated;
        await _kit.Db.SaveChangesAsync();
        _kit.Db.ChangeTracker.Clear();

        await _kit.Job().CreateAsync("booking:abc:new", Queued(PushAudience.BookingHosts(world.BookingId)), CancellationToken.None);

        Assert.Empty(_kit.Db.InAppNotifications);
    }

    [Fact]
    public async Task CreateAsync_APersonInChargeOfAnotherOrg_IsNotToldAboutThisOnes_TheAdministratorsStillAre()
    {
        var world = await _kit.SeedWorldAsync();
        var manager = await _kit.Db.Users.SingleAsync(u => u.Id == World.ManagerId);
        manager.OrgId = world.OtherHostOrgId;
        await _kit.Db.SaveChangesAsync();
        _kit.Db.ChangeTracker.Clear();

        await _kit.Job().CreateAsync("booking:abc:new", Queued(PushAudience.BookingHosts(world.BookingId)), CancellationToken.None);

        Assert.Equal([World.OwnerId], (await _kit.RowsAsync()).Select(r => r.UserId));
    }

    [Fact]
    public async Task CreateAsync_ACollaboratorInChargeWhoDoesNotReachTheProperty_IsNotTold_UntilItIsGivenTheProperty()
    {
        var world = await _kit.SeedWorldAsync();
        var member = await _kit.Db.OrgMembers.SingleAsync(m => m.UserId == World.ManagerId);
        member.Role = OrgRole.Collaborator;
        member.PropertyScope = PropertyScope.Selected;
        await _kit.Db.SaveChangesAsync();
        _kit.Db.ChangeTracker.Clear();
        var audience = Queued(PushAudience.PropertyHosts(world.PropertyId));

        await _kit.Job().CreateAsync("property:abc:before", audience, CancellationToken.None);

        Assert.Equal([World.OwnerId], (await _kit.RowsAsync()).Select(r => r.UserId));

        _kit.Db.PropertyMemberAccesses.Add(new PropertyMemberAccess { OrgId = world.OrgId, UserId = World.ManagerId, PropertyId = world.PropertyId });
        await _kit.Db.SaveChangesAsync();
        _kit.Db.ChangeTracker.Clear();

        await _kit.Job().CreateAsync("property:abc:after", audience, CancellationToken.None);

        Assert.Equal(
            [World.ManagerId, World.OwnerId],
            (await _kit.RowsAsync()).Where(r => r.DeliveryKey == "property:abc:after").Select(r => r.UserId));
    }

    [Fact]
    public async Task CreateAsync_PlatformStaff_TellsTheAdministratorsByRoleAndByMembership_EachInItsOwnOrg_AndTheirBellShowsIt()
    {
        var world = await _kit.SeedWorldAsync();
        _kit.Db.Users.AddRange(
            NewUser("auth0|staff-by-role", world.OtherHostOrgId, UserRole.Admin),
            NewUser("auth0|staff-by-membership", null, UserRole.PropertyOwner, supplierOrgId: world.SupplierOrgId),
            NewUser("auth0|staff-inactive", world.OtherHostOrgId, UserRole.Admin, active: false),
            NewUser("auth0|staff-without-org", null, UserRole.Admin));
        _kit.Db.UserContextMemberships.Add(new UserContextMembership { UserId = "auth0|staff-by-membership", ContextKey = "admin", RoleId = 3 });
        await _kit.Db.SaveChangesAsync();
        var requestId = Guid.NewGuid();

        var notification = QueuedInAppNotification.From(
            PushAudience.PlatformStaff(requestId),
            new PushNotificationPayload(string.Empty, string.Empty, PushTypes.AssistRequested, null, string.Empty),
            Now);
        await _kit.Job().CreateAsync(PushDeliveryKeys.AssistRequested(requestId), notification, CancellationToken.None);

        var rows = await _kit.RowsAsync();
        Assert.Equal(
            [("auth0|staff-by-membership", world.SupplierOrgId), ("auth0|staff-by-role", world.OtherHostOrgId)],
            rows.Select(r => (r.UserId, r.OrgId)));
        Assert.All(rows, row => Assert.Equal((PushTypes.AssistRequested, (Guid?)requestId), (row.Type, row.EntityId)));

        var bell = new Casazen.Infrastructure.Services.InAppNotificationService(
            _kit.Db, _kit.Clock, Microsoft.Extensions.Logging.Abstractions.NullLogger<Casazen.Infrastructure.Services.InAppNotificationService>.Instance);
        foreach (var staff in new[] { "auth0|staff-by-role", "auth0|staff-by-membership" })
            Assert.Equal(1, await bell.CountUnreadAsync(staff));
    }

    [Fact]
    public async Task CreateAsync_UnknownBookingOrProperty_WritesNothingAndDoesNotThrow()
    {
        await _kit.SeedWorldAsync();

        await _kit.Job().CreateAsync("booking:gone:new", Queued(PushAudience.BookingHosts(Guid.NewGuid())), CancellationToken.None);
        await _kit.Job().CreateAsync("property:gone:new", Queued(PushAudience.PropertyHosts(Guid.NewGuid())), CancellationToken.None);
        await _kit.Job().CreateAsync("supplier:gone:new", Queued(PushAudience.SupplierOrg(Guid.NewGuid())), CancellationToken.None);

        Assert.Empty(_kit.Db.InAppNotifications);
    }

    [Fact]
    public async Task CreateAsync_AnAudienceTheJobDoesNotKnow_WritesNothingAndDoesNotThrow()
    {
        await _kit.SeedWorldAsync();
        var unknown = new QueuedInAppNotification
        {
            AudienceKind = (PushAudienceKind)99,
            AudienceId = Guid.NewGuid(),
            Type = PushTypes.NewBooking,
            OccurredAt = Now,
        };

        await _kit.Job().CreateAsync("booking:abc:new", unknown, CancellationToken.None);

        Assert.Empty(_kit.Db.InAppNotifications);
    }

    [Fact]
    public async Task CreateAsync_EveryKindOfAudienceOfThePush_HasARuleHere()
    {
        var world = await _kit.SeedWorldAsync();
        _kit.Db.RolePermissions.Add(new RolePermission { RoleId = 7, PermissionKey = HostPermissions.VerificationDecision });
        _kit.Db.UserContextMemberships.Add(new UserContextMembership
        {
            UserId = InAppNotificationTestKit.World.ManagerId,
            ContextKey = OrgRoleCatalog.ShortRent,
            RoleId = 7,
        });
        await _kit.Db.SaveChangesAsync();
        _kit.Db.Users.Add(NewUser("auth0|staff", world.OtherHostOrgId, UserRole.Admin));
        await _kit.Db.SaveChangesAsync();
        var audiences = new Dictionary<PushAudienceKind, PushAudience>
        {
            [PushAudienceKind.BookingHosts] = PushAudience.BookingHosts(world.BookingId),
            [PushAudienceKind.PropertyHosts] = PushAudience.PropertyHosts(world.PropertyId),
            [PushAudienceKind.SupplierOrg] = PushAudience.SupplierOrg(world.SupplierOrgId),
            [PushAudienceKind.VerificationOperators] = PushAudience.VerificationOperators(world.BookingId),
            [PushAudienceKind.PlatformStaff] = PushAudience.PlatformStaff(Guid.NewGuid()),
            [PushAudienceKind.OrgAdmins] = PushAudience.OrgAdmins(world.OrgId),
        };

        var withoutNotificationByDesign = new HashSet<PushAudienceKind> { PushAudienceKind.User };

        foreach (var kind in Enum.GetValues<PushAudienceKind>().Where(k => !withoutNotificationByDesign.Contains(k)))
        {
            Assert.True(audiences.ContainsKey(kind), $"PushAudienceKind.{kind} has no case in this test: add its rule to InAppNotificationJob.");
            await _kit.Job().CreateAsync($"key:{kind}", Queued(audiences[kind]), CancellationToken.None);
            Assert.NotEmpty(await _kit.Db.InAppNotifications.Where(n => n.DeliveryKey == $"key:{kind}").ToListAsync());
        }
    }

    [Fact]
    public async Task CreateAsync_TheSameEventTwice_GivesEachUserOneRow()
    {
        var world = await _kit.SeedWorldAsync();
        var queued = Queued(PushAudience.BookingHosts(world.BookingId));

        await _kit.Job().CreateAsync("booking:abc:new", queued, CancellationToken.None);
        await _kit.Job().CreateAsync("booking:abc:new", queued, CancellationToken.None);
        await _kit.Job().CreateAsync("booking:abc:new", queued, CancellationToken.None);

        Assert.Equal(2, await _kit.Db.InAppNotifications.CountAsync());
    }

    [Fact]
    public async Task CreateAsync_ARetryAfterSomeUsersWereWritten_WritesOnlyTheOthers()
    {
        var world = await _kit.SeedWorldAsync();
        _kit.Db.InAppNotifications.Add(Row(World.OwnerId, world.OrgId, Now.AddMinutes(-5), deliveryKey: "booking:abc:new"));
        await _kit.Db.SaveChangesAsync();
        _kit.Db.ChangeTracker.Clear();

        await _kit.Job().CreateAsync("booking:abc:new", Queued(PushAudience.BookingHosts(world.BookingId)), CancellationToken.None);

        var rows = await _kit.RowsAsync();
        Assert.Equal(2, rows.Count);
        Assert.Equal(Now.AddMinutes(-5), rows.Single(r => r.UserId == World.OwnerId).CreatedAt);
        Assert.Equal(Now, rows.Single(r => r.UserId == World.ManagerId).CreatedAt);
    }

    [Fact]
    public async Task CreateAsync_APersonWhoJoinedAfterTheFirstRun_IsToldByTheNextRunOfTheSameKey()
    {
        var world = await _kit.SeedWorldAsync();
        var queued = Queued(PushAudience.BookingHosts(world.BookingId));
        await _kit.Job().CreateAsync("booking:abc:new", queued, CancellationToken.None);
        _kit.Db.Users.Add(NewUser("auth0|new-admin", world.OrgId, UserRole.None));
        _kit.Db.OrgMembers.Add(NewMember("auth0|new-admin", world.OrgId, OrgRole.Admin));
        await _kit.Db.SaveChangesAsync();

        await _kit.Job().CreateAsync("booking:abc:new", queued, CancellationToken.None);

        Assert.Equal(
            [World.ManagerId, "auth0|new-admin", World.OwnerId],
            (await _kit.RowsAsync()).Select(r => r.UserId).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task CreateAsync_AnotherEventForTheSameUsers_IsAnotherRow()
    {
        var world = await _kit.SeedWorldAsync();
        var queued = Queued(PushAudience.BookingHosts(world.BookingId));

        await _kit.Job().CreateAsync("booking:abc:new", queued, CancellationToken.None);
        await _kit.Job().CreateAsync("booking:abc:other-event", queued, CancellationToken.None);

        Assert.Equal(4, await _kit.Db.InAppNotifications.CountAsync());
    }

    [Fact]
    public async Task CreateAsync_TheTimeOfTheRow_IsTheTimeOfTheEvent_NotTheTimeOfTheRun()
    {
        var world = await _kit.SeedWorldAsync();
        var eventTime = Now.AddHours(-3).AddTicks(7);
        _kit.Clock.SetUtcNow(new DateTimeOffset(Now.AddHours(2)));

        await _kit.Job().CreateAsync(
            "booking:abc:new",
            Queued(PushAudience.BookingHosts(world.BookingId), occurredAt: eventTime),
            CancellationToken.None);

        Assert.All(await _kit.RowsAsync(), row => Assert.Equal(Now.AddHours(-3), row.CreatedAt));
    }

    [Fact]
    public async Task CreateAsync_AnEventWithoutATime_IsStampedWithTheClockOfTheRun()
    {
        var world = await _kit.SeedWorldAsync();
        var queued = Queued(PushAudience.BookingHosts(world.BookingId));
        queued.OccurredAt = default;

        await _kit.Job().CreateAsync("booking:abc:new", queued, CancellationToken.None);

        Assert.All(await _kit.RowsAsync(), row => Assert.Equal(Now, row.CreatedAt));
    }

    [Fact]
    public async Task CreateAsync_AnEventWithoutAnEntity_IsAValidRow()
    {
        var world = await _kit.SeedWorldAsync();

        await _kit.Job().CreateAsync(
            "booking:abc:new", Queued(PushAudience.BookingHosts(world.BookingId), entityId: null), CancellationToken.None);

        Assert.All(await _kit.RowsAsync(), row => Assert.Null(row.EntityId));
    }

    [Fact]
    public void Row_HasNoFreeText_OnlyTheKindOfEventAndIds()
    {
        var columns = typeof(InAppNotification).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(
            ["CreatedAt", "DeliveryKey", "EntityId", "Id", "OrgId", "ReadAt", "Type", "UserId"],
            columns);
    }

    [Fact]
    public void CreateAsync_IsRetriedFiveTimesAndNeverRunsTwiceAtOnceForTheSameKey()
    {
        var method = typeof(InAppNotificationJob).GetMethod(nameof(InAppNotificationJob.CreateAsync))!;

        var retry = method.GetCustomAttribute<AutomaticRetryAttribute>();
        Assert.NotNull(retry);
        Assert.Equal(InAppNotificationJob.MaxAttempts, retry.Attempts);
        Assert.Equal(AttemptsExceededAction.Delete, retry.OnAttemptsExceeded);

        var concurrency = method.GetCustomAttribute<DisableConcurrentExecutionAttribute>();
        Assert.NotNull(concurrency);
        Assert.Equal("deliveryKey", method.GetParameters()[0].Name);
        Assert.Contains("{0}", concurrency.Resource);
    }

    [Fact]
    public async Task CreateAsync_BlankKey_IsRefused()
    {
        await _kit.SeedWorldAsync();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            _kit.Job().CreateAsync(" ", Queued(PushAudience.SupplierOrg(Guid.NewGuid())), CancellationToken.None));
    }
}
