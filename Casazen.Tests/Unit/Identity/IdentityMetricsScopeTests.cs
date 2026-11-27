using Casazen.Core.Authorization;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Identity;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace Casazen.Tests.Unit.Identity;

/// <summary>
/// E73/F01/US03/T01 (NF-196, AC-NF24-3): the counters are the ones of the caller's reach and of nobody else's. An identity step of another org, of a property the caller was not
/// given, or planted on a stay of the scope with the id of another org, is never counted, never paired and never timed, whatever the tenant filter of the context says; and the
/// same holds for the export of the pilot (<c>IdentityPilotExportTests</c> proves it on the file).
/// </summary>
public sealed class IdentityMetricsScopeTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    private static async Task<IdentityMetricsWorld> SeededAsync()
    {
        var world = new IdentityMetricsWorld();
        foreach (var property in new[] { world.GrantedPropertyId, world.HiddenPropertyId, world.ForeignPropertyId })
        {
            var reviewed = world.AddStay(property);
            world.AddSession(reviewed, IdentityMetricsWorld.At(10), IdentityMetricsWorld.At(10, 10, 5), IdentityOutcomeCodes.Review);
            world.AddDecision(reviewed, IdentityMetricsWorld.At(10, 10, 5), BiometricOutcome.Escalate, BiometricOutcome.Accept, durationMs: 1200);
            world.AddOperator(reviewed, IdentityMetricsWorld.At(10, 10, 35), BiometricOutcome.Reject);

            var automatic = world.AddStay(property);
            world.AddSession(automatic, IdentityMetricsWorld.At(11), IdentityMetricsWorld.At(11, 10, 5), IdentityOutcomeCodes.AutoApproved);
            world.AddDecision(automatic, IdentityMetricsWorld.At(11, 10, 5), BiometricOutcome.Accept, BiometricOutcome.Accept, IdentityDecisionMode.Automatic, durationMs: 800);

            world.AddSession(world.AddStay(property), IdentityMetricsWorld.Now.UtcDateTime.AddHours(-1));
        }

        await world.SaveAsync();
        return world;
    }

    private static async Task<IdentityVerificationMetrics> MetricsAsync(IdentityMetricsWorld world, HostScope scope, Guid? tenantOrgId = null)
    {
        await using var db = world.NewDb(tenantOrgId);
        var service = new IdentityVerificationMetricsService(db, new FakeTimeProvider(IdentityMetricsWorld.Now), Options.Create(new IdentityVerificationOptions()));
        return await service.GetAsync(scope, IdentityMetricsWorld.From, IdentityMetricsWorld.To, Ct);
    }

    private static void AssertOnlyTheStepsOf(IdentityVerificationMetrics metrics, int properties)
    {
        Assert.Equal(3 * properties, metrics.Opened);
        Assert.Equal(metrics.Opened, metrics.Outcomes.Total);
        Assert.Equal((properties, properties, properties), (metrics.Outcomes.AutoApproved, metrics.Outcomes.Review, metrics.Outcomes.InProgress));
        Assert.Equal(new IdentityShadowVsHuman(0, properties, 0, 0), metrics.ShadowVsHuman);
        Assert.Equal(new IdentityPercentiles(800, 1200, 2 * properties), metrics.LatencyMs);
        Assert.Equal(new IdentityPercentiles(1800, 1800, properties), metrics.ReviewSeconds);
        Assert.Equal(3 * properties, metrics.Coverage.Sum(cell => cell.Opened));
    }

    [Fact]
    public async Task IdentityMetricsScope_OtherOrgAndUnreachableProperty_NotCounted()
    {
        var world = await SeededAsync();

        foreach (var tenant in new Guid?[] { null, world.OrgId })
        {
            AssertOnlyTheStepsOf(await MetricsAsync(world, world.OrgWide, tenant), properties: 2);
            AssertOnlyTheStepsOf(await MetricsAsync(world, world.Restricted, tenant), properties: 1);
            AssertOnlyTheStepsOf(await MetricsAsync(world, world.OwnedByTheOwnerOfTheHiddenProperty, tenant), properties: 1);
        }

        AssertOnlyTheStepsOf(await MetricsAsync(world, new HostScope(world.OtherOrgId), world.OtherOrgId), properties: 1);
        Assert.Equal(0, (await MetricsAsync(world, new HostScope(world.OtherOrgId), world.OrgId)).Opened);

        Assert.Equal(0, (await MetricsAsync(world, new HostScope(world.OrgId, GrantedToUserId: "auth0|nessuna-proprieta"))).Opened);
        Assert.Equal(0, (await MetricsAsync(world, new HostScope(world.OrgId, OwnerId: "auth0|non-ha-creato-nulla"))).Opened);
    }

    [Fact]
    public async Task IdentityMetricsScope_ARowOrASessionOfAnotherOrgOnAStayOfTheScope_IsNeverCounted()
    {
        var world = new IdentityMetricsWorld();
        var stay = world.AddStay(world.GrantedPropertyId);
        world.AddSession(stay, IdentityMetricsWorld.At(10), IdentityMetricsWorld.At(10, 10, 5), IdentityOutcomeCodes.Review);
        world.AddDecision(stay, IdentityMetricsWorld.At(10, 10, 5), BiometricOutcome.Escalate, BiometricOutcome.Accept);

        world.AddOperator(stay, IdentityMetricsWorld.At(10, 10, 35), BiometricOutcome.Reject, orgId: world.OtherOrgId);
        var other = world.AddStay(world.GrantedPropertyId);
        world.AddSession(other, IdentityMetricsWorld.At(11), IdentityMetricsWorld.At(11, 10, 5), IdentityOutcomeCodes.AutoApproved, orgId: world.OtherOrgId);
        world.AddDecision(other, IdentityMetricsWorld.At(11, 10, 5), BiometricOutcome.Accept, BiometricOutcome.Accept, IdentityDecisionMode.Automatic, orgId: world.OtherOrgId);
        await world.SaveAsync();

        var theOrg = await MetricsAsync(world, world.OrgWide);
        var theOtherOrg = await MetricsAsync(world, new HostScope(world.OtherOrgId));

        Assert.Equal(1, theOrg.Opened);
        Assert.Equal(new IdentityShadowVsHuman(0, 0, 0, 0), theOrg.ShadowVsHuman);
        Assert.Equal(new IdentityPercentiles(900, 900, 1), theOrg.LatencyMs);
        Assert.Equal(0, theOrg.ReviewSeconds.N);
        Assert.Equal(0, theOtherOrg.Opened);
        Assert.Equal(0, theOtherOrg.LatencyMs.N);
    }

    [Fact]
    public async Task IdentityMetricsScope_IsTheScopeOfTheQueueOfTheOperators()
    {
        var world = await SeededAsync();
        await using var db = world.NewDb();

        foreach (var scope in new[] { world.OrgWide, world.Restricted, world.OwnedByTheOwnerOfTheHiddenProperty })
        {
            var reachable = db.Bookings.InScope(scope).Where(booking => booking.OrgId == world.OrgId).Select(booking => booking.Id).ToList();
            var counted = db.GuestCheckInSessions.Where(session => session.IdentityOpenedAt != null && reachable.Contains(session.BookingId)).Count();
            var service = new IdentityVerificationMetricsService(db, new FakeTimeProvider(IdentityMetricsWorld.Now), Options.Create(new IdentityVerificationOptions()));

            Assert.Equal(counted, (await service.GetAsync(scope, IdentityMetricsWorld.From, IdentityMetricsWorld.To, Ct)).Opened);
        }
    }

    /// <summary>
    /// Proof (data, privacy) on the SQL the database gets (nothing is run: the SQL is only generated, as the schema tests do, so it is the translation of PostgreSQL and not the one of the
    /// in-memory provider): both queries of the ledger translate, name the org and the period in SQL, take the reach of the scope in SQL (the grants of a collaborator «Solo alcuni», the
    /// properties an account in no team created, nothing for the whole org), and select the reasons of a decision (the numbers a person was compared with) only for the export, never for
    /// the counters.
    /// </summary>
    [Fact]
    public void IdentityVerificationLedger_TheQueriesTranslateForPostgres_NameTheOrgAndTheScope_AndSelectTheReasonsOnlyForTheExport()
    {
        var orgId = Guid.NewGuid();
        using var db = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>()
                .UseNpgsql("Host=localhost;Database=casazen_design;Username=postgres;Password=postgres", npgsql => npgsql.MigrationsAssembly("Casazen.Infrastructure"))
                .Options);

        var orgWide = IdentityVerificationLedger.SessionsQuery(db, new HostScope(orgId), IdentityMetricsWorld.From, IdentityMetricsWorld.To).ToQueryString();
        var granted = IdentityVerificationLedger
            .SessionsQuery(db, new HostScope(orgId, GrantedToUserId: IdentityMetricsWorld.Member), IdentityMetricsWorld.From, IdentityMetricsWorld.To)
            .ToQueryString();
        var owned = IdentityVerificationLedger
            .SessionsQuery(db, new HostScope(orgId, OwnerId: IdentityMetricsWorld.OwnerOfHidden), IdentityMetricsWorld.From, IdentityMetricsWorld.To)
            .ToQueryString();

        foreach (var sql in new[] { orgWide, granted, owned })
        {
            Assert.Matches("\"OrgId\" = @", sql);
            Assert.Matches("\"IdentityOpenedAt\" >= @", sql);
            Assert.Matches("\"IdentityOpenedAt\" < @", sql);
        }

        Assert.DoesNotContain("PropertyMemberAccess", orgWide, StringComparison.Ordinal);
        Assert.Contains("PropertyMemberAccess", granted, StringComparison.Ordinal);
        Assert.Matches("\"OwnerId\" = @", owned);

        var bookings = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var counters = IdentityVerificationLedger.LogsQuery(db, orgId, bookings, IdentityMetricsWorld.From, withReasons: false).ToQueryString();
        var export = IdentityVerificationLedger.LogsQuery(db, orgId, bookings, IdentityMetricsWorld.From, withReasons: true).ToQueryString();

        foreach (var sql in new[] { counters, export })
        {
            Assert.Matches("\"OrgId\" = @", sql);
            Assert.Matches("\"CreatedAt\" >= @", sql);
            Assert.Contains("ANY", sql, StringComparison.Ordinal);
            Assert.Contains("\"DecisionMode\"", sql, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("ReasonsJson", counters, StringComparison.Ordinal);
        Assert.Contains("ReasonsJson", export, StringComparison.Ordinal);
    }
}
