using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Casazen.Tests.Unit.Infrastructure;

/// <summary>
/// E08/F05/US01/T01: the price and the rules of the nights of a property, the changes of the calendar and their chunks are rows of the org of the
/// property. The rule that costs if it fails: the prices a host set, the days it closed and the changes it sent to its channel manager are never
/// read by a caller of another org, because the three tables carry the global tenant filter like every <see cref="ITenantOwned"/> entity (TN-2).
/// </summary>
public class PropertyDayRuleTenantTests
{
    /// <summary>
    /// PROOF TEST: permission rule (tenant isolation). Two orgs have a day rule, a change and a chunk each. The caller of org A reads its own rows
    /// only: the rules, the processes and the chunks of org B are neither in the lists nor found by the id of their property, and a caller with no
    /// org matches nothing.
    /// </summary>
    [Fact]
    public async Task PropertyDayRule_OtherOrgRows_AreInvisible_Test()
    {
        var databaseName = $"property-day-rules-{Guid.NewGuid():N}";
        var orgA = Guid.NewGuid();
        var orgB = Guid.NewGuid();
        var mine = NewRows(orgA, price: 150m);
        var theirs = NewRows(orgB, price: 900m);

        // The system writes both sets (no caller, so no filter), as the services of each org would.
        await using (var system = NewDb(databaseName, tenant: null))
        {
            system.PropertyDayRules.AddRange(mine.Rule, theirs.Rule);
            system.CalendarChangeProcesses.AddRange(mine.Process, theirs.Process);
            system.CalendarChangeChunks.AddRange(mine.Chunk, theirs.Chunk);
            await system.SaveChangesAsync();
        }

        await using var asOrgA = NewDb(databaseName, new FixedTenantContext(orgA));

        var rule = Assert.Single(await asOrgA.PropertyDayRules.AsNoTracking().ToListAsync());
        Assert.Equal(mine.Rule.Id, rule.Id);
        Assert.Equal(150m, rule.Price);
        var process = Assert.Single(await asOrgA.CalendarChangeProcesses.AsNoTracking().ToListAsync());
        Assert.Equal(mine.Process.Id, process.Id);
        var chunk = Assert.Single(await asOrgA.CalendarChangeChunks.AsNoTracking().ToListAsync());
        Assert.Equal(mine.Chunk.Id, chunk.Id);

        // By the property of the other org, by the price and by the state: nothing of theirs is there.
        Assert.False(await asOrgA.PropertyDayRules.AsNoTracking().AnyAsync(r => r.PropertyId == theirs.Rule.PropertyId));
        Assert.False(await asOrgA.PropertyDayRules.AsNoTracking().AnyAsync(r => r.Price == 900m));
        Assert.False(await asOrgA.CalendarChangeProcesses.AsNoTracking().AnyAsync(p => p.State == CalendarChangeState.Failed));
        Assert.False(await asOrgA.CalendarChangeChunks.AsNoTracking().AnyAsync(c => c.PropertyId == theirs.Chunk.PropertyId));

        // A caller with no org is fail-closed: it matches nothing.
        await using var nobody = NewDb(databaseName, new FixedTenantContext(null));
        Assert.Empty(await nobody.PropertyDayRules.AsNoTracking().ToListAsync());
        Assert.Empty(await nobody.CalendarChangeProcesses.AsNoTracking().ToListAsync());
        Assert.Empty(await nobody.CalendarChangeChunks.AsNoTracking().ToListAsync());
    }

    private static (PropertyDayRule Rule, CalendarChangeProcess Process, CalendarChangeChunk Chunk) NewRows(Guid orgId, decimal price)
    {
        var propertyId = Guid.NewGuid();
        var process = new CalendarChangeProcess
        {
            OrgId = orgId,
            Kind = CalendarChangeKind.DayRules,
            State = price > 500m ? CalendarChangeState.Failed : CalendarChangeState.Queued,
            PropertyCount = 1,
            DayCount = 2,
        };

        return (
            new PropertyDayRule { OrgId = orgId, PropertyId = propertyId, StayDate = new DateOnly(2026, 12, 24), Price = price },
            process,
            new CalendarChangeChunk
            {
                OrgId = orgId,
                ProcessId = process.Id,
                PropertyId = propertyId,
                FromDate = new DateOnly(2026, 12, 24),
                ToDate = new DateOnly(2026, 12, 25),
            });
    }

    private static AppDbContext NewDb(string databaseName, ITenantContext? tenant) =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(databaseName).Options, tenant);

    private sealed class FixedTenantContext(Guid? orgId) : ITenantContext
    {
        public Guid? OrgId { get; } = orgId;

        public bool FilterEnabled => true;
    }
}
