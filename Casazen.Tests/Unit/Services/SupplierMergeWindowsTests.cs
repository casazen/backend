using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// SP-05: merging a duplicate supplier profile (<c>fix-orphaned</c>) moves its windows to the keeper, except the engagements of
/// the calendar feed the keeper already has (same UID and start): those are unique per supplier since SP-05, so moving a copy
/// would break the merge. The selection of the windows that move, on an in-memory database; the move itself (an
/// <c>ExecuteUpdate</c>, PostgreSQL only) is in <c>SupplierAgendaRepairPostgresTests</c>.
/// </summary>
public class SupplierMergeWindowsTests
{
    private static readonly Guid Keeper = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
    private static readonly Guid Duplicate = Guid.Parse("00000000-0000-0000-0000-0000000000d2");
    private static readonly Guid Stranger = Guid.Parse("00000000-0000-0000-0000-0000000000f3");

    private static readonly DateTime Nine = new(2026, 10, 10, 9, 0, 0, DateTimeKind.Utc);

    private readonly string _databaseName = Guid.NewGuid().ToString();

    [Fact]
    public async Task EveryWindowOfTheDuplicate_MovesWhenTheKeeperHasNoneInCommon()
    {
        await SeedAsync(
            Feed(Duplicate, "a", Nine),
            Feed(Duplicate, "b", Nine.AddHours(1)),
            Manual(Duplicate, SupplierBusyWindowKind.Block, Nine),
            Manual(Duplicate, SupplierBusyWindowKind.ExtraOpening, Nine.AddHours(5)));

        var ids = await MovingAsync();

        Assert.Equal(4, ids.Count);
    }

    [Fact]
    public async Task AnEngagementTheKeeperAlreadyHas_StaysWithTheKeeper_TheOthersMove()
    {
        var sameUidAndStart = Feed(Duplicate, "shared", Nine);
        var otherStart = Feed(Duplicate, "shared", Nine.AddDays(1));
        var otherUid = Feed(Duplicate, "mine", Nine);
        await SeedAsync(
            Feed(Keeper, "shared", Nine),
            sameUidAndStart,
            otherStart,
            otherUid);

        var ids = await MovingAsync();

        Assert.DoesNotContain(sameUidAndStart.Id, ids);
        Assert.Equal(new[] { otherStart.Id, otherUid.Id }.Order(), ids.Order());
    }

    [Fact]
    public async Task TheWindowsSetByHand_AlwaysMove_EvenAtTheHoursOfAWindowOfTheKeeper()
    {
        // No UID, so they are outside the unique index: two blocks at the same hour are fine, and neither is dropped.
        var block = Manual(Duplicate, SupplierBusyWindowKind.Block, Nine);
        await SeedAsync(Manual(Keeper, SupplierBusyWindowKind.Block, Nine), block, Feed(Keeper, "x", Nine));

        var ids = await MovingAsync();

        Assert.Equal([block.Id], ids);
    }

    [Fact]
    public async Task AnotherSuppliersWindows_NeverMove()
    {
        var mine = Feed(Duplicate, "mine", Nine);
        await SeedAsync(Feed(Stranger, "mine", Nine), Manual(Stranger, SupplierBusyWindowKind.Block, Nine), mine);

        var ids = await MovingAsync();

        Assert.Equal([mine.Id], ids);
    }

    private async Task<List<Guid>> MovingAsync()
    {
        await using var db = CreateDb();
        return await SupplierService.WindowsThatMove(db, Keeper, Duplicate).Select(w => w.Id).ToListAsync();
    }

    private async Task SeedAsync(params SupplierBusyWindow[] windows)
    {
        await using var db = CreateDb();
        db.SupplierBusyWindows.AddRange(windows);
        await db.SaveChangesAsync();
    }

    private AppDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(_databaseName).Options);

    private static SupplierBusyWindow Feed(Guid orgId, string uid, DateTime start) =>
        new()
        {
            OrgId = orgId,
            StartUtc = start,
            EndUtc = start.AddHours(1),
            Kind = SupplierBusyWindowKind.External,
            Source = SupplierBusyWindowSource.ICalFeed,
            ExternalUid = uid,
        };

    private static SupplierBusyWindow Manual(Guid orgId, SupplierBusyWindowKind kind, DateTime start) =>
        new()
        {
            OrgId = orgId,
            StartUtc = start,
            EndUtc = start.AddHours(1),
            Kind = kind,
            Source = SupplierBusyWindowSource.Manual,
        };
}
