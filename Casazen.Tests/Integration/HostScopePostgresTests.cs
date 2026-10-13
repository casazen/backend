using System.Data.Common;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Repositories;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Integration.Postgres;
using Casazen.Tests.Unit.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// AM-03 on a real PostgreSQL database: what EF InMemory cannot prove. Every list of the host shows a collaborator "Solo alcuni"
/// only the properties it was given (the same assertions as <c>HostScopeListsTests</c>, on the SQL the code really produces,
/// with the foreign keys and the unique indexes of the schema enforced); a list is one command whatever the number of
/// properties (no query per row, no list of ids built beforehand); the grants follow their property and their person in the
/// schema (cascade, set null) and never twice; two writers on the same member leave one consistent set; the authorization
/// snapshot carries the grants and sees a change after the write.
/// </summary>
public class HostScopePostgresTests : IAsyncLifetime
{
    private PostgresTestDatabase? _database;
    private HostScopeWorld _world = null!;

    public async Task InitializeAsync()
    {
        _database = await PostgresTestDatabase.CreateAsync();
        await using var db = _database.CreateContext();
        await db.Database.MigrateAsync();
        _world = await HostScopeScenario.SeedAsync(db);
    }

    public async Task DisposeAsync()
    {
        if (_database is not null)
            await _database.DisposeAsync();
    }

    // --- The lists --------------------------------------------------------------------------------------

    [PostgresFact]
    public async Task Lists_EveryPoint_TheCollaboratorSeesOnlyItsPropertiesOnPostgreSql()
    {
        // All the points are run and reported together, so one run of CI tells everything that is wrong.
        var failures = new List<string>();
        foreach (var (name, run) in HostScopePoints.All)
        {
            try
            {
                await using var db = _database!.CreateContext();
                await run(db, _world);
            }
            catch (Exception exception)
            {
                failures.Add($"{name}: {exception.GetType().Name}: {exception.Message}");
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [PostgresFact]
    public async Task Lists_TheCockpitOfTheCollaborator_HoldsOnlyItsProperties()
    {
        await using var db = _database!.CreateContext();
        var service = Casazen.Tests.Unit.Services.ComplianceWizardServiceTests.CreateService(
            db, new Casazen.Tests.Unit.FixedTimeProvider(HostScopeScenario.Now));

        var restricted = await service.GetSummaryAsync(_world.Restricted);
        var orgWide = await service.GetSummaryAsync(_world.OrgWide);

        // The scenario has one stay to complete per property: the collaborator sees the one of its property.
        var restrictedIds = restricted.GuestCheckInsIncomplete.Items.Select(i => i.Id).ToHashSet();
        var orgWideIds = orgWide.GuestCheckInsIncomplete.Items.Select(i => i.Id).ToHashSet();
        Assert.True(restrictedIds.IsSubsetOf(orgWideIds));
        Assert.True(restrictedIds.Count <= orgWideIds.Count);
        var bookingsOfHidden = await db.Bookings.Where(b => b.PropertyId == _world.Hidden.Id).Select(b => b.Id).ToListAsync();
        Assert.Empty(restrictedIds.Intersect(bookingsOfHidden));
        Assert.Empty(restricted.AlloggiatiManualRequired.Items.Select(i => i.Id).Intersect(bookingsOfHidden));
        Assert.Empty(restricted.CheckoutsDue.Items.Select(i => i.Id).Intersect(bookingsOfHidden));
        Assert.Empty(restricted.TurnoversPending.Items.Select(i => i.Id).Intersect(bookingsOfHidden));
    }

    // --- One command, whatever the number of properties ------------------------------------------------

    /// <summary>Counts the commands a context sends to the server.</summary>
    private sealed class CommandCounter : DbCommandInterceptor
    {
        private int _count;

        public int Count => _count;

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _count);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            Interlocked.Increment(ref _count);
            return base.ReaderExecuting(command, eventData, result);
        }
    }

    private AppDbContext NewCountingContext(CommandCounter counter) => new(
        new DbContextOptionsBuilder<AppDbContext>(_database!.CreateOptions()).AddInterceptors(counter).Options);

    /// <summary>The commands each list sends for the collaborator, by name.</summary>
    private async Task<Dictionary<string, int>> CommandsPerListAsync()
    {
        var calls = new Dictionary<string, Func<AppDbContext, Task>>
        {
            ["bookings"] = db => new BookingRepository(db).GetByScopeAsync(_world.Restricted),
            ["payments"] = db => new PaymentRepository(db).GetByScopeAsync(_world.Restricted),
            ["leases"] = db => new LeaseContractRepository(db).GetSummariesAsync(_world.Restricted),
            ["properties"] = db => new PropertyRepository(db).GetByScopeAsync(_world.Restricted),
            ["cin-summary"] = db => new PropertyRepository(db).GetByScopeForComplianceAsync(_world.Restricted),
            ["fiscal-annual"] = db => HostScopePoints.Fiscal(db).GetAnnualReportAsync(_world.Restricted, 2026),
            ["dashboard-kpis"] = db => HostScopePoints.Dashboard(db).GetKpisAsync(_world.Restricted, HostDashboardPeriodKind.Month, null),
        };

        var counts = new Dictionary<string, int>();
        foreach (var (name, call) in calls)
        {
            var counter = new CommandCounter();
            await using var db = NewCountingContext(counter);
            await call(db);
            counts[name] = counter.Count;
        }

        return counts;
    }

    [PostgresFact]
    public async Task Lists_TheNumberOfCommands_DoesNotGrowWithTheNumberOfPropertiesTheCollaboratorReaches()
    {
        var before = await CommandsPerListAsync();
        Assert.All(before, entry => Assert.True(entry.Value > 0, $"{entry.Key}: no command counted"));
        // The plain lists are a few statements at most: the reach is part of the query (an EXISTS), not a list of ids or a query per row.
        foreach (var plain in new[] { "bookings", "payments", "leases", "properties", "cin-summary" })
            Assert.True(before[plain] <= 3, $"{plain}: {before[plain]} commands");

        // Thirty more properties, all of them reached by the collaborator, each with its stays, payments and feeds.
        await using (var db = _database!.CreateContext())
        {
            for (var i = 0; i < 30; i++)
            {
                var property = HostScopeScenario.NewProperty(_world.OrgId, _world.OwnerUserId, $"Casa {i}");
                db.Properties.Add(property);
                HostScopeScenario.AddDataOf(db, property, _world.SupplierOrgId);
                db.PropertyMemberAccesses.Add(new PropertyMemberAccess
                {
                    OrgId = _world.OrgId,
                    UserId = _world.CollaboratorId,
                    PropertyId = property.Id,
                });
            }

            await db.SaveChangesAsync();
        }

        var after = await CommandsPerListAsync();

        Assert.Equal(before, after);
    }

    // --- The schema of the grants ----------------------------------------------------------------------

    [PostgresFact]
    public async Task Schema_ThePairPersonAndPropertyIsUnique_TheDatabaseRefusesAGrantTwice()
    {
        await using var db = _database!.CreateContext();
        db.PropertyMemberAccesses.Add(new PropertyMemberAccess
        {
            OrgId = _world.OrgId,
            UserId = _world.CollaboratorId,
            PropertyId = _world.Granted.Id,
        });

        var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());

        var postgres = Assert.IsType<PostgresException>(error.InnerException);
        Assert.Equal(PostgresErrorCodes.UniqueViolation, postgres.SqlState);
        Assert.Equal("UIX_PropertyMemberAccesses_UserId_PropertyId", postgres.ConstraintName);
    }

    [PostgresFact]
    public async Task Schema_AGrantGoesWithItsProperty_AndAGrantToAnUnknownPropertyIsRefused()
    {
        await using var db = _database!.CreateContext();
        var property = HostScopeScenario.NewProperty(_world.OrgId, _world.OwnerUserId, "Da demolire");
        db.Properties.Add(property);
        db.PropertyMemberAccesses.Add(new PropertyMemberAccess { OrgId = _world.OrgId, UserId = _world.CollaboratorId, PropertyId = property.Id });
        await db.SaveChangesAsync();

        await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Properties\" WHERE \"Id\" = {property.Id}");

        Assert.Equal(0, await db.PropertyMemberAccesses.IgnoreQueryFilters().CountAsync(a => a.PropertyId == property.Id));
        // The collaborator keeps the grant of the property that still exists.
        Assert.Equal(1, await db.PropertyMemberAccesses.IgnoreQueryFilters().CountAsync(a => a.UserId == _world.CollaboratorId));

        db.ChangeTracker.Clear();
        db.PropertyMemberAccesses.Add(new PropertyMemberAccess { OrgId = _world.OrgId, UserId = _world.CollaboratorId, PropertyId = Guid.NewGuid() });
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, Assert.IsType<PostgresException>(error.InnerException).SqlState);
    }

    [PostgresFact]
    public async Task Schema_APersonWhoIsErased_TakesItsGrantsAndIsNoLongerInCharge()
    {
        await using var db = _database!.CreateContext();
        var user = new User { Id = $"auth0|cancellato-{Guid.NewGuid():N}", Email = $"{Guid.NewGuid():N}@example.com", FirstName = "Ex", LastName = "Membro", OrgId = _world.OrgId, IsActive = true };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        db.PropertyMemberAccesses.Add(new PropertyMemberAccess { OrgId = _world.OrgId, UserId = user.Id, PropertyId = _world.Granted.Id });
        var property = await db.Properties.SingleAsync(p => p.Id == _world.Granted.Id);
        property.ResponsibleUserId = user.Id;
        await db.SaveChangesAsync();

        await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Users\" WHERE \"Id\" = {user.Id}");

        db.ChangeTracker.Clear();
        Assert.Equal(0, await db.PropertyMemberAccesses.IgnoreQueryFilters().CountAsync(a => a.UserId == user.Id));
        Assert.Null((await db.Properties.SingleAsync(p => p.Id == _world.Granted.Id)).ResponsibleUserId);
    }

    // --- Two writers, one member -----------------------------------------------------------------------

    [PostgresFact]
    public async Task SetAsync_SixRequestsForTheSameMemberAtOnce_LeaveOneOfTheSetsWholeAndFailNone()
    {
        var kit = new OrgInvitationTestKit(() => _database!.CreateContext());
        var (org, _) = await kit.SeedOwnerOrgAsync();
        var (_, member) = await kit.SeedMemberAsync(org.Id, "auth0|anna", OrgRole.Collaborator);
        var properties = new List<Guid>();
        await using (var db = _database!.CreateContext())
        {
            for (var i = 0; i < 6; i++)
            {
                var property = HostScopeScenario.NewProperty(org.Id, "auth0|owner", $"Casa {i}");
                db.Properties.Add(property);
                properties.Add(property.Id);
            }

            await db.SaveChangesAsync();
        }

        // Overlapping sets of two properties each: every pair of neighbours shares one.
        var sets = Enumerable.Range(0, 6).Select(i => new[] { properties[i], properties[(i + 1) % 6] }).ToList();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = sets.Select(async set =>
        {
            await gate.Task;
            await using var db = kit.NewDb();
            var service = kit.PropertyAccess(db);
            await service.SetAsync(org.Id, member.Id, "auth0|owner", PropertyScope.Selected, set);
        }).ToList();
        gate.SetResult();

        await Task.WhenAll(running);

        await using var verify = _database.CreateContext();
        var granted = await verify.PropertyMemberAccesses.IgnoreQueryFilters()
            .Where(a => a.UserId == "auth0|anna").Select(a => a.PropertyId).ToListAsync();
        Assert.Equal(2, granted.Count);
        Assert.Equal(2, granted.Distinct().Count());
        Assert.Contains(sets, set => set.Order().SequenceEqual(granted.Order()));
    }

    // --- The authorization snapshot --------------------------------------------------------------------

    [PostgresFact]
    public async Task Snapshot_CarriesTheGrantsOfTheCollaborator_AndSeesAChangeOnceTheWriterInvalidatesIt()
    {
        var kit = new OrgInvitationTestKit(() => _database!.CreateContext());
        var (org, _) = await kit.SeedOwnerOrgAsync();
        var (_, member) = await kit.SeedMemberAsync(org.Id, "auth0|anna", OrgRole.Collaborator);
        Guid first, second;
        await using (var db = _database!.CreateContext())
        {
            var a = HostScopeScenario.NewProperty(org.Id, "auth0|owner", "Primo");
            var b = HostScopeScenario.NewProperty(org.Id, "auth0|owner", "Secondo");
            db.Properties.AddRange(a, b);
            await db.SaveChangesAsync();
            (first, second) = (a.Id, b.Id);
        }

        using var memoryCache = new MemoryCache(new MemoryCacheOptions());
        var configuration = new ConfigurationBuilder().Build();
        async Task<UserAuthorizationSnapshot> SnapshotAsync()
        {
            await using var db = _database!.CreateContext();
            return await new UserAuthorizationSnapshotStore(db, memoryCache, new HttpContextAccessor(), configuration).GetAsync("auth0|anna");
        }

        async Task SetAsync(PropertyScope scope, params Guid[] ids)
        {
            await using var db = _database!.CreateContext();
            var service = kit.PropertyAccess(db);
            await service.SetAsync(org.Id, member.Id, "auth0|owner", scope, ids);
        }

        var everything = await SnapshotAsync();
        await SetAsync(PropertyScope.Selected, first, second);
        var stale = await SnapshotAsync();
        memoryCache.Remove("casazen:authz:user:auth0|anna");
        var restricted = await SnapshotAsync();
        await SetAsync(PropertyScope.Selected, second);
        memoryCache.Remove("casazen:authz:user:auth0|anna");
        var narrowed = await SnapshotAsync();

        Assert.Equal(PropertyScope.All, everything.OrgMember!.PropertyScope);
        Assert.Null(everything.OrgMember.GrantedPropertyIds);
        // The cache of this process keeps the old copy until the writer invalidates it (60 s for the other instances).
        Assert.Equal(PropertyScope.All, stale.OrgMember!.PropertyScope);
        Assert.Equal(PropertyScope.Selected, restricted.OrgMember!.PropertyScope);
        Assert.True(restricted.OrgMember.GrantedPropertyIds!.SetEquals([first, second]));
        Assert.True(narrowed.OrgMember!.GrantedPropertyIds!.SetEquals([second]));

        // The decision on the snapshot: the collaborator reaches the property it was given and not the other.
        var scope = HostScopeResolver.Decide(narrowed, "auth0|anna", new HashSet<string>(), org.Id)!;
        Assert.True(HostScopeResolver.Reaches(narrowed, scope, second, "auth0|owner"));
        Assert.False(HostScopeResolver.Reaches(narrowed, scope, first, "auth0|owner"));
    }
}
