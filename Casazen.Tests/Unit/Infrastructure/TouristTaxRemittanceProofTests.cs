using Casazen.Core.Entities;
using Casazen.Core.Multitenancy;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Casazen.Tests.Unit.Infrastructure;

/// <summary>
/// NF-100, E07/F03/US04/T01: the payments of the tourist tax a host made to a comune are fiscal records of one org. The data rule that
/// costs if it fails: the payment of another org is never returned to a caller, whatever the query, because the table carries the global
/// tenant filter like every <see cref="ITenantOwned"/> entity (TN-2).
/// </summary>
public class TouristTaxRemittanceProofTests
{
    /// <summary>
    /// PROOF TEST: data rule (tenant isolation). Two orgs paid the same comune. The caller of org A reads its own payment only: the one of
    /// org B is neither in the list nor found by its id (the 404 of the endpoints), and a caller with no org matches nothing.
    /// </summary>
    [Fact]
    public async Task TouristTaxRemittance_OtherOrgRows_Invisible_Test()
    {
        var databaseName = $"tourist-tax-remittance-{Guid.NewGuid()}";
        var orgA = Guid.NewGuid();
        var orgB = Guid.NewGuid();
        var mine = NewRemittance(orgA, 10.00m);
        var theirs = NewRemittance(orgB, 99.00m);

        // The system writes both rows (no caller, so no filter), as the seed or a job would.
        await using (var system = NewDb(databaseName, tenant: null))
        {
            system.TouristTaxRemittances.AddRange(mine, theirs);
            await system.SaveChangesAsync();
        }

        await using var asOrgA = NewDb(databaseName, new FixedTenantContext(orgA));
        var visible = await asOrgA.TouristTaxRemittances.AsNoTracking().ToListAsync();

        var row = Assert.Single(visible);
        Assert.Equal(mine.Id, row.Id);
        Assert.Equal(orgA, row.OrgId);
        Assert.Equal(10.00m, row.PaidAmount);

        // By id too: the payment of the other org is not there.
        Assert.Null(await asOrgA.TouristTaxRemittances.AsNoTracking().FirstOrDefaultAsync(r => r.Id == theirs.Id));
        Assert.False(await asOrgA.TouristTaxRemittances.AsNoTracking().AnyAsync(r => r.OrgId == orgB));

        // A caller with no org is fail-closed: it matches nothing.
        await using var nobody = NewDb(databaseName, new FixedTenantContext(null));
        Assert.Empty(await nobody.TouristTaxRemittances.AsNoTracking().ToListAsync());
    }

    private static TouristTaxRemittance NewRemittance(Guid orgId, decimal amount) => new()
    {
        OrgId = orgId,
        Comune = "Milano",
        ComuneKey = "milano",
        PeriodFrom = new DateOnly(2026, 1, 1),
        PeriodTo = new DateOnly(2026, 3, 31),
        PaidAmount = amount,
        PaidOn = new DateOnly(2026, 4, 15),
        RecordedByUserId = "auth0|host",
    };

    private static AppDbContext NewDb(string databaseName, ITenantContext? tenant) =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(databaseName).Options, tenant);

    private sealed class FixedTenantContext(Guid? orgId) : ITenantContext
    {
        public Guid? OrgId { get; } = orgId;

        public bool FilterEnabled => true;
    }
}
