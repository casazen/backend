using Casazen.Core.Entities;
using Casazen.Core.Multitenancy;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Casazen.Tests.Unit.Infrastructure;

/// <summary>
/// AM-01: the one query behind the request tenant reads the caller's org membership status without the tenant filter of
/// <c>OrgMembers</c> (the request has no tenant yet: it is what resolves it), or the filter would match nothing and a
/// deactivated member would never be refused. The SQL is read from the Npgsql provider, no server needed; the same
/// request against a real database is <c>OrgMemberInactivePostgresTests</c>.
/// </summary>
public class CallerTenantQueryTests
{
    private static readonly Guid CallerOrgId = Guid.Parse("5b0e5f6e-1c1b-4d0a-9d3c-0a5b7a6c2e44");

    [Fact]
    public void For_ReadsTheOrgMembershipStatusInTheSameQuery_WithoutTheTenantFilter()
    {
        using var db = NewNpgsqlContext(new FixedTenantContext(CallerOrgId, filterEnabled: true));

        var sql = CallerTenantQuery.For(db, "auth0|caller").ToQueryString();

        Assert.Contains("\"OrgMembers\"", sql);
        Assert.Contains("\"Status\"", sql);
        // No tenant predicate on OrgMembers: the filter reads the request's OrgId, which is being resolved.
        Assert.DoesNotMatch("\"OrgId\" = @", sql);
        // One read: the subquery is part of the statement over Users, no second round trip.
        Assert.Equal(1, CountOccurrences(sql, "FROM \"Users\""));
    }

    [Fact]
    public void For_StillCountsOnlyAHostOrgAsTheTenantOfTheCaller()
    {
        // PL-05 (A1-40): a user whose OrgId points at its supplier org has no tenant.
        using var db = NewNpgsqlContext(new FixedTenantContext(CallerOrgId, filterEnabled: true));

        var sql = CallerTenantQuery.For(db, "auth0|caller").ToQueryString();

        Assert.Contains("\"OrgType\" = 0", sql);
        Assert.Contains("\"IsActive\"", sql);
    }

    [Fact]
    public void OrgMembers_ReadWithoutIgnoringTheFilter_AreScopedToTheCallersOrg()
    {
        // The negative control of the test above: the filter is there for every other read of the table.
        using var db = NewNpgsqlContext(new FixedTenantContext(CallerOrgId, filterEnabled: true));

        var sql = db.OrgMembers.ToQueryString();

        Assert.Matches("\"OrgId\" = @", sql);
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        for (var index = text.IndexOf(value, StringComparison.Ordinal); index >= 0; index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    private static AppDbContext NewNpgsqlContext(ITenantContext tenant) =>
        new(
            new DbContextOptionsBuilder<AppDbContext>()
                .UseNpgsql(
                    "Host=localhost;Database=casazen_design;Username=postgres;Password=postgres",
                    npgsql => npgsql.MigrationsAssembly("Casazen.Infrastructure"))
                .Options,
            tenant);

    private sealed class FixedTenantContext(Guid? orgId, bool filterEnabled) : ITenantContext
    {
        public Guid? OrgId { get; } = orgId;

        public bool FilterEnabled { get; } = filterEnabled;
    }
}
