using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Casazen.Tests.Unit.Octorate;

/// <summary>
/// E08/F01/US01/T01: the mirror of a booking on Octorate is a row of the org of the booking. The rule that costs if it fails: the state of the
/// bookings of another org (which of its bookings failed to reach its channel manager) is never read by a caller, because the table carries the
/// global tenant filter like every <see cref="ITenantOwned"/> entity (TN-2).
/// </summary>
public class OctorateBookingSyncTenantTests
{
    [Fact]
    public async Task OctorateBookingSync_OtherOrgRows_AreInvisible_Test()
    {
        var databaseName = $"octorate-booking-sync-{Guid.NewGuid():N}";
        var orgA = Guid.NewGuid();
        var orgB = Guid.NewGuid();
        var mine = NewSync(orgA, OctorateSyncState.Synced);
        var theirs = NewSync(orgB, OctorateSyncState.Failed, "octorate_unavailable");

        await using (var system = NewDb(databaseName, tenant: null))
        {
            system.OctorateBookingSyncs.AddRange(mine, theirs);
            await system.SaveChangesAsync();
        }

        await using var asOrgA = NewDb(databaseName, new FixedTenantContext(orgA));
        var visible = await asOrgA.OctorateBookingSyncs.AsNoTracking().ToListAsync();

        var row = Assert.Single(visible);
        Assert.Equal(mine.Id, row.Id);
        Assert.Equal(orgA, row.OrgId);
        Assert.Equal(OctorateSyncState.Synced, row.State);

        Assert.Null(await asOrgA.OctorateBookingSyncs.AsNoTracking().FirstOrDefaultAsync(s => s.BookingId == theirs.BookingId));
        Assert.False(await asOrgA.OctorateBookingSyncs.AsNoTracking().AnyAsync(s => s.State == OctorateSyncState.Failed));

        await using var nobody = NewDb(databaseName, new FixedTenantContext(null));
        Assert.Empty(await nobody.OctorateBookingSyncs.AsNoTracking().ToListAsync());
    }

    private static OctorateBookingSync NewSync(Guid orgId, OctorateSyncState state, string? errorCode = null) => new()
    {
        OrgId = orgId,
        BookingId = Guid.NewGuid(),
        PropertyId = Guid.NewGuid(),
        State = state,
        LastOperation = OctorateSyncOperation.Create,
        Attempts = 1,
        ErrorCode = errorCode,
    };

    private static AppDbContext NewDb(string databaseName, ITenantContext? tenant) =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(databaseName).Options, tenant);

    private sealed class FixedTenantContext(Guid? orgId) : ITenantContext
    {
        public Guid? OrgId { get; } = orgId;

        public bool FilterEnabled => true;
    }
}
