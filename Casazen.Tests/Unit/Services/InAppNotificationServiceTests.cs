using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Push;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static Casazen.Tests.Unit.Push.InAppNotificationTestKit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// UI-12a: what the bell reads. Whose rows a user reaches (its own, in the orgs it belongs to), the order, the filter and the
/// pages. The updates and the retention are single SQL statements (<c>ExecuteUpdate</c>, <c>ExecuteDelete</c>) the in-memory
/// provider does not run: they are proved on PostgreSQL in <c>InAppNotificationsPostgresTests</c>.
/// </summary>
public class InAppNotificationServiceTests : IDisposable
{
    private readonly InAppNotificationTestKit _kit = new();

    public void Dispose() => _kit.Dispose();

    private InAppNotificationService Service() =>
        new(_kit.Db, _kit.Clock, NullLogger<InAppNotificationService>.Instance);

    private static DateTime At(int minutesAgo) => Now.AddMinutes(-minutesAgo);

    private async Task AddAsync(params InAppNotification[] rows)
    {
        _kit.Db.InAppNotifications.AddRange(rows);
        await _kit.Db.SaveChangesAsync();
        _kit.Db.ChangeTracker.Clear();
    }

    // ─── List ───

    [Fact]
    public async Task ListAsync_NewestFirst_AndAnIdBreaksATie()
    {
        var world = await _kit.SeedWorldAsync();
        var oldest = Row(World.OwnerId, world.OrgId, At(30));
        var middle = Row(World.OwnerId, world.OrgId, At(10));
        var sameInstantA = Row(World.OwnerId, world.OrgId, At(5));
        var sameInstantB = Row(World.OwnerId, world.OrgId, At(5));
        await AddAsync(oldest, middle, sameInstantA, sameInstantB);

        var page = await Service().ListAsync(World.OwnerId, unreadOnly: false, page: 1, pageSize: 20);

        var tied = new[] { sameInstantA.Id, sameInstantB.Id }.OrderByDescending(id => id).ToList();
        Assert.Equal([tied[0], tied[1], middle.Id, oldest.Id], page.Items.Select(i => i.Id));
        Assert.Equal(4, page.TotalCount);
    }

    [Fact]
    public async Task ListAsync_ReturnsTheKindOfEventAndTheIdsNothingElse()
    {
        var world = await _kit.SeedWorldAsync();
        var row = Row(World.OwnerId, world.OrgId, At(3), PushTypes.ServiceRequestRejected, readAt: At(1));
        await AddAsync(row);

        var item = Assert.Single((await Service().ListAsync(World.OwnerId, false, 1, 20)).Items);

        Assert.Equal(new InAppNotificationItem(row.Id, PushTypes.ServiceRequestRejected, row.EntityId, At(3), At(1)), item);
    }

    [Fact]
    public async Task ListAsync_UnreadOnly_LeavesOutTheReadOnesAndCountsOnlyTheUnread()
    {
        var world = await _kit.SeedWorldAsync();
        var unread1 = Row(World.OwnerId, world.OrgId, At(3));
        var unread2 = Row(World.OwnerId, world.OrgId, At(2));
        var read = Row(World.OwnerId, world.OrgId, At(1), readAt: At(0));
        await AddAsync(unread1, unread2, read);

        var unread = await Service().ListAsync(World.OwnerId, unreadOnly: true, 1, 20);
        var all = await Service().ListAsync(World.OwnerId, unreadOnly: false, 1, 20);

        Assert.Equal([unread2.Id, unread1.Id], unread.Items.Select(i => i.Id));
        Assert.Equal(2, unread.TotalCount);
        Assert.Equal(3, all.TotalCount);
    }

    [Fact]
    public async Task ListAsync_PagesNeverRepeatAndCoverEverything()
    {
        var world = await _kit.SeedWorldAsync();
        var rows = Enumerable.Range(1, 5).Select(i => Row(World.OwnerId, world.OrgId, At(i))).ToArray();
        await AddAsync(rows);

        var first = await Service().ListAsync(World.OwnerId, false, page: 1, pageSize: 2);
        var second = await Service().ListAsync(World.OwnerId, false, page: 2, pageSize: 2);
        var third = await Service().ListAsync(World.OwnerId, false, page: 3, pageSize: 2);
        var fourth = await Service().ListAsync(World.OwnerId, false, page: 4, pageSize: 2);

        Assert.Equal([2, 2, 1, 0], new[] { first, second, third, fourth }.Select(p => p.Items.Count));
        Assert.All(new[] { first, second, third, fourth }, p => Assert.Equal(5, p.TotalCount));
        var ids = first.Items.Concat(second.Items).Concat(third.Items).Select(i => i.Id).ToList();
        Assert.Equal(rows.OrderByDescending(r => r.CreatedAt).Select(r => r.Id), ids);
        Assert.Equal((2, 2), (second.Page, second.PageSize));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(1, 1)]
    [InlineData(20, 20)]
    [InlineData(50, 50)]
    [InlineData(51, 50)]
    [InlineData(100000, 50)]
    public async Task ListAsync_PageSize_IsHeldBetweenOneAndFifty(int asked, int expected)
    {
        var world = await _kit.SeedWorldAsync();
        await AddAsync(Row(World.OwnerId, world.OrgId, At(1)));

        var page = await Service().ListAsync(World.OwnerId, false, 1, asked);

        Assert.Equal(expected, page.PageSize);
        Assert.Equal(50, InAppNotificationLimits.MaxPageSize);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public async Task ListAsync_PageBelowOne_IsThePageOne(int asked)
    {
        var world = await _kit.SeedWorldAsync();
        await AddAsync(Row(World.OwnerId, world.OrgId, At(1)));

        var page = await Service().ListAsync(World.OwnerId, false, asked, 20);

        Assert.Equal(1, page.Page);
        Assert.Single(page.Items);
    }

    [Fact]
    public async Task ListAsync_BlankUser_IsRefused()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Service().ListAsync(" ", false, 1, 20));
    }

    // ─── Whose rows ───

    [Fact]
    public async Task ListAndCount_NeverShowTheRowsOfAnotherUser_EvenOfTheSameOrg()
    {
        var world = await _kit.SeedWorldAsync();
        var mine = Row(World.OwnerId, world.OrgId, At(2));
        var hers = Row(World.ManagerId, world.OrgId, At(1));
        await AddAsync(mine, hers);

        var page = await Service().ListAsync(World.OwnerId, false, 1, 20);

        Assert.Equal([mine.Id], page.Items.Select(i => i.Id));
        Assert.Equal(1, await Service().CountUnreadAsync(World.OwnerId));
        Assert.Equal(1, await Service().CountUnreadAsync(World.ManagerId));
    }

    [Fact]
    public async Task ListAndCount_RowsOfAnOrgTheUserDoesNotBelongTo_AreInvisible()
    {
        var world = await _kit.SeedWorldAsync();
        var inMyOrg = Row(World.OwnerId, world.OrgId, At(2));
        var inAnotherOrg = Row(World.OwnerId, world.OtherHostOrgId, At(1));
        await AddAsync(inMyOrg, inAnotherOrg);

        var page = await Service().ListAsync(World.OwnerId, false, 1, 20);

        Assert.Equal([inMyOrg.Id], page.Items.Select(i => i.Id));
        Assert.Equal(1, await Service().CountUnreadAsync(World.OwnerId));
    }

    [Fact]
    public async Task ListAndCount_APersonWhoLeftTheOrg_NoLongerReadsWhatHappenedThere()
    {
        var world = await _kit.SeedWorldAsync();
        await AddAsync(Row(World.ManagerId, world.OrgId, At(2)), Row(World.ManagerId, world.OrgId, At(1)));
        Assert.Equal(2, await Service().CountUnreadAsync(World.ManagerId));

        // Removed from the org (AM-02): User.OrgId is cleared; the rows stay until the retention, unreachable.
        var manager = await _kit.Db.Users.SingleAsync(u => u.Id == World.ManagerId);
        manager.OrgId = null;
        await _kit.Db.SaveChangesAsync();

        Assert.Equal(0, await Service().CountUnreadAsync(World.ManagerId));
        Assert.Empty((await Service().ListAsync(World.ManagerId, false, 1, 20)).Items);
    }

    [Fact]
    public async Task ListAndCount_ASupplierOnlyAccount_ReadsTheRowsOfItsSupplierOrg()
    {
        // A supplier-only account has no host org: the tenant filter would match nothing for it.
        var world = await _kit.SeedWorldAsync();
        var supplierOnly = NewUser("auth0|supplier-only", orgId: null, UserRole.Supplier, supplierOrgId: world.SupplierOrgId);
        _kit.Db.Users.Add(supplierOnly);
        var row = Row(supplierOnly.Id, world.SupplierOrgId, At(1), PushTypes.ServiceRequestCreated);
        await AddAsync(row);

        var page = await Service().ListAsync(supplierOnly.Id, false, 1, 20);

        Assert.Equal([row.Id], page.Items.Select(i => i.Id));
        Assert.Equal(1, await Service().CountUnreadAsync(supplierOnly.Id));
    }

    [Fact]
    public async Task ListAndCount_ALegacySupplierAccountWhoseOrgIdIsTheSupplierOrg_ReadsItsRows()
    {
        var world = await _kit.SeedWorldAsync();
        var legacy = NewUser("auth0|supplier-legacy", world.SupplierOrgId, UserRole.Supplier, supplierOrgId: null);
        _kit.Db.Users.Add(legacy);
        await AddAsync(Row(legacy.Id, world.SupplierOrgId, At(1), PushTypes.ServiceRequestCreated));

        Assert.Equal(1, await Service().CountUnreadAsync(legacy.Id));
    }

    [Fact]
    public async Task ListAndCount_ADualRoleUser_ReadsBothTheHostAndTheSupplierRowsInOneList()
    {
        var world = await _kit.SeedWorldAsync();
        var asHost = Row(World.SupplierHostTooId, world.OtherHostOrgId, At(3), PushTypes.NewBooking);
        var asSupplier = Row(World.SupplierHostTooId, world.SupplierOrgId, At(2), PushTypes.ServiceRequestCreated);
        var elsewhere = Row(World.SupplierHostTooId, world.OrgId, At(1), PushTypes.NewBooking);
        await AddAsync(asHost, asSupplier, elsewhere);

        var page = await Service().ListAsync(World.SupplierHostTooId, false, 1, 20);

        Assert.Equal([asSupplier.Id, asHost.Id], page.Items.Select(i => i.Id));
        Assert.Equal(2, await Service().CountUnreadAsync(World.SupplierHostTooId));
    }

    [Fact]
    public async Task ListAndCount_AUserWithoutARow_HasNoNotifications()
    {
        var world = await _kit.SeedWorldAsync();
        await AddAsync(Row("auth0|ghost", world.OrgId, At(1)));

        Assert.Empty((await Service().ListAsync("auth0|ghost", false, 1, 20)).Items);
        Assert.Equal(0, await Service().CountUnreadAsync("auth0|ghost"));
    }

    // ─── Count ───

    [Fact]
    public async Task CountUnreadAsync_CountsOnlyTheUnreadOnesOfTheUser()
    {
        var world = await _kit.SeedWorldAsync();
        await AddAsync(
            Row(World.OwnerId, world.OrgId, At(4)),
            Row(World.OwnerId, world.OrgId, At(3)),
            Row(World.OwnerId, world.OrgId, At(2), readAt: At(1)),
            Row(World.ManagerId, world.OrgId, At(1)));

        Assert.Equal(2, await Service().CountUnreadAsync(World.OwnerId));
        Assert.Equal(1, await Service().CountUnreadAsync(World.ManagerId));
        Assert.Equal(0, await Service().CountUnreadAsync(World.BystanderId));
    }

    [Fact]
    public async Task CountUnreadAsync_BlankUser_IsRefused()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Service().CountUnreadAsync(""));
    }

    // ─── The limits ───

    [Fact]
    public void Limits_AreTheOnesOfTheTask()
    {
        Assert.Equal(90, InAppNotificationLimits.RetentionDays);
        Assert.Equal(20, InAppNotificationLimits.DefaultPageSize);
        Assert.Equal(50, InAppNotificationLimits.MaxPageSize);
    }
}
