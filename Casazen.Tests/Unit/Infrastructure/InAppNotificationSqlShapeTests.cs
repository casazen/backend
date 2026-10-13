using Casazen.Core.Entities;
using Casazen.Core.Multitenancy;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Casazen.Tests.Unit.Infrastructure;

/// <summary>
/// UI-12a: the SQL the Npgsql provider writes for the reads of the bell, without a database (<c>ToQueryString</c>): the rows of
/// the user are found with the user's own orgs in the same statement, the tenant filter is replaced on purpose, and the page is
/// ordered and cut in SQL. The single-statement updates and the retention run on PostgreSQL in
/// <c>InAppNotificationsPostgresTests</c>.
/// </summary>
public class InAppNotificationSqlShapeTests
{
    private static readonly Guid CallerOrgId = Guid.Parse("7a3c1d52-9f0e-4b8a-8d65-2c4e1f0b9a11");

    private static AppDbContext NewNpgsqlContext() =>
        new(
            new DbContextOptionsBuilder<AppDbContext>()
                .UseNpgsql(
                    "Host=localhost;Database=casazen_design;Username=postgres;Password=postgres",
                    npgsql => npgsql.MigrationsAssembly("Casazen.Infrastructure"))
                .Options,
            new FixedTenantContext(CallerOrgId, filterEnabled: true));

    private static InAppNotificationService Service(AppDbContext db) =>
        new(db, TimeProvider.System, NullLogger<InAppNotificationService>.Instance);

    [Fact]
    public void OwnNotifications_AreTheRowsOfTheUserInAnOrgOfTheUser_InOneStatement_WithoutTheTenantFilter()
    {
        using var db = NewNpgsqlContext();

        var sql = Service(db).OwnNotifications("auth0|anna").ToQueryString();

        Assert.Contains("FROM \"InAppNotifications\"", sql);
        Assert.Matches("\"UserId\" = @", sql);
        Assert.Contains("EXISTS", sql);
        Assert.Contains("FROM \"Users\"", sql);
        // The host org and the supplier org of the user, compared with the org of the row.
        Assert.Contains("\"OrgId\"", sql);
        Assert.Contains("\"SupplierOrgId\"", sql);
        // The tenant filter would add "OrgId" = @__tenant_OrgId: it is replaced by the user's own orgs (see the class remarks).
        Assert.DoesNotMatch("\"OrgId\" = @", sql);
    }

    [Fact]
    public void TheTenantFilterOfTheModel_IsStillThere_ForEveryOtherReaderOfTheTable()
    {
        using var db = NewNpgsqlContext();

        var sql = db.InAppNotifications.ToQueryString();

        Assert.Matches("\"OrgId\" = @", sql);
    }

    [Fact]
    public void ThePageOfTheList_IsOrderedNewestFirstCutAndProjectedInSql()
    {
        using var db = NewNpgsqlContext();

        var sql = Service(db).OwnNotifications("auth0|anna")
            .Where(n => n.ReadAt == null)
            .OrderByDescending(n => n.CreatedAt)
            .ThenByDescending(n => n.Id)
            .Skip(20)
            .Take(20)
            .Select(n => new InAppNotificationItem(n.Id, n.Type, n.EntityId, n.CreatedAt, n.ReadAt))
            .ToQueryString();

        Assert.Contains("\"ReadAt\" IS NULL", sql);
        Assert.Matches("ORDER BY .*\"CreatedAt\" DESC.*\"Id\" DESC", sql.ReplaceLineEndings(" "));
        Assert.Contains("LIMIT", sql);
        Assert.Contains("OFFSET", sql);
        Assert.DoesNotContain("DeliveryKey", sql);
        Assert.DoesNotContain("\"UserId\",", sql);
    }

    private sealed class FixedTenantContext(Guid? orgId, bool filterEnabled) : ITenantContext
    {
        public Guid? OrgId { get; } = orgId;

        public bool FilterEnabled { get; } = filterEnabled;
    }
}
