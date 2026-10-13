using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Multitenancy;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Casazen.Tests.Unit.Infrastructure;

/// <summary>
/// AM-03: the SQL that <c>InScope(scope)</c> becomes on the real provider (Npgsql, nothing is executed). The point of the
/// extension is that the reach of a collaborator is decided inside the one query: a single <c>EXISTS</c> on the grants of the
/// person, parameterised, never a list of property ids built beforehand and never a second query per row. The data-level
/// proof is in <c>HostScopeListsTests</c> (InMemory) and <c>HostScopePostgresTests</c> (CI).
/// </summary>
public class HostScopeSqlShapeTests
{
    private const string Collaborator = "auth0|collaboratore";
    private const string Holder = "auth0|titolare";

    private static readonly Guid OrgId = Guid.Parse("7a3c1d52-9f0e-4b8a-8d65-2c4e1f0b9a11");
    private static readonly HostScope Restricted = new(OrgId, GrantedToUserId: Collaborator);
    private static readonly HostScope OrgWide = new(OrgId);
    private static readonly HostScope Legacy = new(OrgId, OwnerId: Holder);

    public static TheoryData<string> Entities =>
    [
        nameof(Property),
        nameof(Booking),
        nameof(LeaseContract),
        nameof(Payment),
        nameof(ServiceRequest),
        nameof(PropertyICalFeed),
        nameof(StayCheckout),
    ];

    private static AppDbContext NewDb(ITenantContext? tenant = null) =>
        new(
            new DbContextOptionsBuilder<AppDbContext>()
                .UseNpgsql(
                    "Host=localhost;Database=casazen_design;Username=postgres;Password=postgres",
                    npgsql => npgsql.MigrationsAssembly("Casazen.Infrastructure"))
                .Options,
            tenant ?? NullTenantContext.Instance);

    private static IQueryable Scoped(AppDbContext db, string entity, HostScope scope) => entity switch
    {
        nameof(Property) => db.Properties.InScope(scope),
        nameof(Booking) => db.Bookings.InScope(scope),
        nameof(LeaseContract) => db.LeaseContracts.InScope(scope),
        nameof(Payment) => db.Payments.InScope(scope),
        nameof(ServiceRequest) => db.ServiceRequests.InScope(scope),
        nameof(PropertyICalFeed) => db.PropertyICalFeeds.InScope(scope),
        nameof(StayCheckout) => db.StayCheckouts.InScope(scope),
        _ => throw new ArgumentOutOfRangeException(nameof(entity), entity, null),
    };

    private static IQueryable Plain(AppDbContext db, string entity) => entity switch
    {
        nameof(Property) => db.Properties,
        nameof(Booking) => db.Bookings,
        nameof(LeaseContract) => db.LeaseContracts,
        nameof(Payment) => db.Payments,
        nameof(ServiceRequest) => db.ServiceRequests,
        nameof(PropertyICalFeed) => db.PropertyICalFeeds,
        nameof(StayCheckout) => db.StayCheckouts,
        _ => throw new ArgumentOutOfRangeException(nameof(entity), entity, null),
    };

    /// <summary>The statement without the header of parameters that <c>ToQueryString</c> prints above it.</summary>
    private static string Body(string sql) => sql[sql.IndexOf("SELECT", StringComparison.Ordinal)..];

    private static int Count(string text, string part) => text.Split(part, StringSplitOptions.None).Length - 1;

    [Theory]
    [MemberData(nameof(Entities))]
    public void InScope_OrgWide_AddsNothingToTheQuery(string entity)
    {
        using var db = NewDb();
        var plain = Plain(db, entity);

        var scoped = Scoped(db, entity, OrgWide);

        Assert.Same(plain, scoped);
        Assert.Equal(plain.ToQueryString(), scoped.ToQueryString());
    }

    [Theory]
    [MemberData(nameof(Entities))]
    public void InScope_ACollaboratorSoloAlcuni_IsOneExistsOnItsGrants_Parameterised(string entity)
    {
        using var db = NewDb();

        var sql = Scoped(db, entity, Restricted).ToQueryString();
        var body = Body(sql);

        Assert.Equal(1, Count(body, "EXISTS ("));
        Assert.Contains("FROM \"PropertyMemberAccesses\"", body, StringComparison.Ordinal);
        Assert.Matches("\"UserId\" = @", body);
        // The person is a parameter of the statement, not text inside it, and there is no list of ids: the grants table is read
        // once, inside the one statement, so the cost does not grow with the number of properties or of rows.
        Assert.Contains(Collaborator, sql[..sql.IndexOf("SELECT", StringComparison.Ordinal)], StringComparison.Ordinal);
        Assert.DoesNotContain(Collaborator, body, StringComparison.Ordinal);
        Assert.DoesNotContain(" IN (", body, StringComparison.Ordinal);
        Assert.Equal(1, Count(body, "\"PropertyMemberAccesses\""));
    }

    [Theory]
    [MemberData(nameof(Entities))]
    public void InScope_AnAccountInNoTeam_IsTheCreatorColumn_WithoutTheGrantTable(string entity)
    {
        using var db = NewDb();

        var body = Body(Scoped(db, entity, Legacy).ToQueryString());

        Assert.Matches("\"OwnerId\" = @", body);
        Assert.DoesNotContain("PropertyMemberAccesses", body, StringComparison.Ordinal);
        Assert.Equal(0, Count(body, "EXISTS ("));
    }

    [Fact]
    public void InScope_KeepsTheTenantPredicate_OnTheRowsAndOnTheGrants()
    {
        using var db = NewDb(new FixedTenant(OrgId));

        var body = Body(db.Bookings.InScope(Restricted).ToQueryString());

        // The org of the caller narrows the bookings and, by the tenant filter of the table, the grants as well.
        Assert.True(Count(body, "\"OrgId\" = @") >= 2, body);
        Assert.Equal(1, Count(body, "EXISTS ("));
    }

    [Fact]
    public void InScope_ThroughTheBookingOfAPayment_JoinsOnceAndAddsNoSecondQuery()
    {
        using var db = NewDb();

        var body = Body(db.Payments.InScope(Restricted).ToQueryString());

        Assert.Equal(1, Count(body, "FROM \"Bookings\""));
        Assert.Equal(1, Count(body, "FROM \"Properties\""));
    }

    [Fact]
    public void QueryFilters_TheGrantIsNotAGlobalFilter_PropertyKeepsItsTwoAndTheGrantTableOnlyTheTenantOne()
    {
        using var db = NewDb();

        var propertyFilters = db.Model.FindEntityType(typeof(Property))!.GetDeclaredQueryFilters().Select(f => f.Key).ToList();
        var grantFilters = db.Model.FindEntityType(typeof(PropertyMemberAccess))!.GetDeclaredQueryFilters().Select(f => f.Key).ToList();

        Assert.Equal([AppDbContext.SoftDeleteQueryFilter, AppDbContext.TenantQueryFilter], propertyFilters.Order().ToList());
        Assert.Equal([AppDbContext.TenantQueryFilter], grantFilters);
    }

    private sealed class FixedTenant(Guid orgId) : ITenantContext
    {
        public Guid? OrgId { get; } = orgId;

        public bool FilterEnabled => true;
    }
}
