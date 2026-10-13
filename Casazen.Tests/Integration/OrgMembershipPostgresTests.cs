using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Multitenancy;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Integration.Postgres;
using Casazen.Tests.Unit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Npgsql;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// AM-01 on a real PostgreSQL database: what the in-memory tests of <see cref="OrgMembershipService"/> cannot prove.
/// One org per user (unique index), the tenant filter of <c>OrgMembers</c>, its foreign keys, the request tenant query that
/// reads a member's status with the filter on, and the concurrency of the owner creation, of two orgs adding the same
/// person and of two reconcile runs.
/// </summary>
public class OrgMembershipPostgresTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 10, 30, 0, TimeSpan.Zero);

    private PostgresTestDatabase? _database;

    public async Task InitializeAsync()
    {
        _database = await PostgresTestDatabase.CreateAsync();
        await using var db = _database.CreateContext();
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (_database is not null)
            await _database.DisposeAsync();
    }

    private AppDbContext NewContext(ITenantContext? tenant = null) =>
        tenant is null ? _database!.CreateContext() : new AppDbContext(_database!.CreateOptions(), tenant);

    private static OrgMembershipService NewService(AppDbContext db, Mock<IUserAuthorizationCache>? cache = null) =>
        new(db, (cache ?? new Mock<IUserAuthorizationCache>()).Object, NullLogger<OrgMembershipService>.Instance, new FixedTimeProvider(Now));

    // ─── Keys and filters ───────────────────────────────────────────────────────────────────────────────

    [PostgresFact]
    public async Task UniqueIndex_TheSameUserInTwoOrgs_IsRejectedByTheDatabase()
    {
        var (orgA, orgB, user) = await SeedTwoOrgsAndAUserAsync();
        await using var db = NewContext();
        db.OrgMembers.Add(new OrgMember { OrgId = orgA, UserId = user, Role = OrgRole.Collaborator });
        await db.SaveChangesAsync();

        db.OrgMembers.Add(new OrgMember { OrgId = orgB, UserId = user, Role = OrgRole.Admin });
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());

        var postgres = Assert.IsType<PostgresException>(error.InnerException);
        Assert.Equal(PostgresErrorCodes.UniqueViolation, postgres.SqlState);
        Assert.Equal("UIX_OrgMembers_UserId", postgres.ConstraintName);
    }

    [PostgresFact]
    public async Task ForeignKeys_AnOrgWithMembersCannotBeDeleted_AndADeletedUserTakesItsMemberRowWithIt()
    {
        var (orgA, _, user) = await SeedTwoOrgsAndAUserAsync();
        await using (var db = NewContext())
        {
            db.OrgMembers.Add(new OrgMember { OrgId = orgA, UserId = user, Role = OrgRole.Collaborator });
            await db.SaveChangesAsync();
        }

        await using var connection = new NpgsqlConnection(_database!.ConnectionString);
        await connection.OpenAsync();
        var deleteOrg = connection.CreateCommand();
        deleteOrg.CommandText = $"DELETE FROM \"Orgs\" WHERE \"Id\" = '{orgA}'";
        var restricted = await Assert.ThrowsAsync<PostgresException>(() => deleteOrg.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, restricted.SqlState);

        var deleteUser = connection.CreateCommand();
        deleteUser.CommandText = $"DELETE FROM \"Users\" WHERE \"Id\" = '{user}'";
        await deleteUser.ExecuteNonQueryAsync();
        await using var verify = NewContext();
        Assert.Empty(await verify.OrgMembers.IgnoreQueryFilters().Where(m => m.UserId == user).ToListAsync());
    }

    [PostgresFact]
    public async Task TenantFilter_OrgMembers_AreScopedToTheCallersOrgAndFailClosed()
    {
        var (orgA, orgB, _) = await SeedTwoOrgsAndAUserAsync();
        var inA = await SeedUserAsync(orgA);
        var inB = await SeedUserAsync(orgB);
        await using (var seed = NewContext())
        {
            var service = NewService(seed);
            await service.EnsureOwnerAsync(inA, orgA);
            await service.EnsureOwnerAsync(inB, orgB);
        }

        await using var asA = NewContext(new FixedTenantContext(orgA, filterEnabled: true));
        await using var asB = NewContext(new FixedTenantContext(orgB, filterEnabled: true));
        await using var noOrg = NewContext(new FixedTenantContext(null, filterEnabled: true));
        await using var system = NewContext(new FixedTenantContext(null, filterEnabled: false));

        Assert.Equal([inA], await asA.OrgMembers.Select(m => m.UserId).ToListAsync());
        Assert.Equal([inB], await asB.OrgMembers.Select(m => m.UserId).ToListAsync());
        Assert.Empty(await noOrg.OrgMembers.ToListAsync());
        Assert.Equal(2, await system.OrgMembers.CountAsync());
        Assert.Equal(2, await asA.OrgMembers.IgnoreQueryFilters().CountAsync());
    }

    [PostgresFact]
    public async Task CallerTenantQuery_FilterOnAndNoTenantYet_ReadsTheMembersStatus()
    {
        // The request tenant resolves with the filter on and no org yet: the status of the member must still be read.
        var (orgA, orgB, _) = await SeedTwoOrgsAndAUserAsync();
        var active = await SeedUserAsync(orgA);
        var deactivated = await SeedUserAsync(orgA);
        var outsider = await SeedUserAsync(orgB);
        await using (var seed = NewContext())
        {
            var service = NewService(seed);
            await service.AddMemberAsync(active, orgA, OrgRole.Collaborator, ["short-rent"], null);
            await service.AddMemberAsync(deactivated, orgA, OrgRole.Collaborator, ["short-rent"], null);
            await service.DeactivateAsync(deactivated);
        }

        await using var db = NewContext(new FixedTenantContext(null, filterEnabled: true));

        var activeRow = await CallerTenantQuery.For(db, active).SingleAsync();
        var deactivatedRow = await CallerTenantQuery.For(db, deactivated).SingleAsync();
        var outsiderRow = await CallerTenantQuery.For(db, outsider).SingleAsync();

        Assert.Equal((orgA, true, OrgMemberStatus.Active), (activeRow.OrgId, activeRow.IsActive, activeRow.MemberStatus));
        Assert.Equal((orgA, true, OrgMemberStatus.Deactivated), (deactivatedRow.OrgId, deactivatedRow.IsActive, deactivatedRow.MemberStatus));
        Assert.Null(outsiderRow.MemberStatus);
        Assert.Equal(orgB, outsiderRow.OrgId);
    }

    // ─── The owner and the members, all or nothing, under concurrency ───────────────────────────────────

    [PostgresFact]
    public async Task EnsureOwnerAsync_EightParallelCalls_WriteOneMemberAndOneAccountMembership()
    {
        var (orgA, _, _) = await SeedTwoOrgsAndAUserAsync();
        var owner = await SeedUserAsync(orgA, UserRole.PropertyOwner);

        await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            await using var db = NewContext();
            await NewService(db).EnsureOwnerAsync(owner, orgA);
        }));

        await using var verify = NewContext();
        var member = await verify.OrgMembers.IgnoreQueryFilters().SingleAsync(m => m.UserId == owner);
        Assert.Equal((orgA, OrgRole.Owner), (member.OrgId, member.Role));
        var memberships = await verify.UserContextMemberships.Where(m => m.UserId == owner).Include(m => m.Role).ToListAsync();
        var account = Assert.Single(memberships);
        Assert.Equal(("account", "org_owner"), (account.ContextKey, account.Role.RoleKey));
    }

    [PostgresFact]
    public async Task AddMemberAsync_TheSamePersonToTwoOrgsAtOnce_OnlyOneOrgGetsItAndNothingIsHalfWritten()
    {
        var (orgA, orgB, _) = await SeedTwoOrgsAndAUserAsync();
        var person = await SeedUserAsync(orgId: null);

        async Task<(bool Added, string? Code)> TryAddAsync(Guid orgId)
        {
            await using var db = NewContext();
            try
            {
                await NewService(db).AddMemberAsync(person, orgId, OrgRole.Admin, ["short-rent", "long-rent"], null);
                return (true, null);
            }
            catch (DomainConflictException ex)
            {
                return (false, ex.Code);
            }
        }

        var results = await Task.WhenAll(TryAddAsync(orgA), TryAddAsync(orgB));

        Assert.Single(results, r => r.Added);
        var loser = Assert.Single(results, r => !r.Added);
        Assert.Contains(loser.Code, new[] { OrgMembershipErrors.AlreadyMember, OrgMembershipErrors.OtherOrg });

        await using var verify = NewContext();
        var member = await verify.OrgMembers.IgnoreQueryFilters().SingleAsync(m => m.UserId == person);
        var user = await verify.Users.SingleAsync(u => u.Id == person);
        Assert.Equal(member.OrgId, user.OrgId);
        var rows = await verify.UserContextMemberships.Where(m => m.UserId == person).Select(m => m.ContextKey).ToListAsync();
        Assert.Equal(["account", "long-rent", "short-rent"], rows.Order());
    }

    [PostgresFact]
    public async Task AddMemberAsync_ARoleRowIsMissing_SavesNothingNotEvenTheMemberOrTheOrgLink()
    {
        var (orgA, _, _) = await SeedTwoOrgsAndAUserAsync();
        var person = await SeedUserAsync(orgId: null);
        await using (var admin = NewContext())
            await admin.Database.ExecuteSqlRawAsync("DELETE FROM \"Roles\" WHERE \"ContextKey\" = 'short-rent' AND \"RoleKey\" = 'staff'");

        await using var db = NewContext();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => NewService(db).AddMemberAsync(person, orgA, OrgRole.Collaborator, ["short-rent"], null));

        await using var verify = NewContext();
        Assert.Empty(await verify.OrgMembers.IgnoreQueryFilters().Where(m => m.UserId == person).ToListAsync());
        Assert.Null((await verify.Users.SingleAsync(u => u.Id == person)).OrgId);
    }

    [PostgresFact]
    public async Task DeactivateAsync_OnPostgres_SetsTheStatusAndTheCacheIsInvalidatedOnce()
    {
        var (orgA, _, _) = await SeedTwoOrgsAndAUserAsync();
        var person = await SeedUserAsync(orgId: null);
        var cache = new Mock<IUserAuthorizationCache>();
        await using (var db = NewContext())
            await NewService(db, cache).AddMemberAsync(person, orgA, OrgRole.Collaborator, ["short-rent"], null);
        cache.Invocations.Clear();

        await using (var db = NewContext())
            await NewService(db, cache).DeactivateAsync(person);

        await using var verify = NewContext();
        var member = await verify.OrgMembers.IgnoreQueryFilters().SingleAsync(m => m.UserId == person);
        Assert.Equal(OrgMemberStatus.Deactivated, member.Status);
        Assert.Equal(Now.UtcDateTime, member.DeactivatedAt);
        cache.Verify(c => c.Invalidate(person), Times.Once);
    }

    // ─── Reconcile ──────────────────────────────────────────────────────────────────────────────────────

    [PostgresFact]
    public async Task ReconcileAsync_TwoRunsAtOnce_CreateEachOwnerOnceWithoutError()
    {
        var owners = new List<string>();
        for (var i = 0; i < 4; i++)
        {
            var org = await SeedOrgAsync();
            var owner = await SeedUserAsync(org, UserRole.PropertyOwner);
            await using var db = NewContext();
            db.UserContextMemberships.Add(new UserContextMembership { UserId = owner, ContextKey = "short-rent", RoleId = 1 });
            await db.SaveChangesAsync();
            owners.Add(owner);
        }

        async Task<OrgMembershipReconcileReport> RunAsync()
        {
            await using var db = NewContext();
            return await NewService(db).ReconcileAsync(dryRun: false);
        }

        var reports = await Task.WhenAll(RunAsync(), RunAsync());

        // Together they created each owner exactly once (the second waited for the first run's lock and found nothing).
        Assert.Equal(4, reports.Sum(r => r.Fixes.Count(f => f.Code == OrgMembershipReconcileCodes.OwnerCreated)));
        await using var verify = NewContext();
        Assert.Equal(owners.Order(), (await verify.OrgMembers.IgnoreQueryFilters().Select(m => m.UserId).ToListAsync()).Order());
        Assert.Equal(4, await verify.UserContextMemberships.CountAsync(m => m.ContextKey == "account"));
    }

    [PostgresFact]
    public async Task ReconcileAsync_DryRun_SavesNothingOnPostgresAndAppliedRunMatchesIt()
    {
        var org = await SeedOrgAsync();
        var owner = await SeedUserAsync(org, UserRole.PropertyOwner);
        var ambiguousOrg = await SeedOrgAsync();
        await SeedUserAsync(ambiguousOrg, UserRole.PropertyOwner);
        await SeedUserAsync(ambiguousOrg, UserRole.LongTermLandlord);

        OrgMembershipReconcileReport dry;
        await using (var db = NewContext())
            dry = await NewService(db).ReconcileAsync(dryRun: true);

        await using (var verify = NewContext())
        {
            Assert.Empty(await verify.OrgMembers.IgnoreQueryFilters().ToListAsync());
            Assert.Empty(await verify.UserContextMemberships.Where(m => m.ContextKey == "account").ToListAsync());
        }

        OrgMembershipReconcileReport applied;
        await using (var db = NewContext())
            applied = await NewService(db).ReconcileAsync(dryRun: false);

        Assert.Equal(dry.Fixes.Select(f => f.Code).Order(), applied.Fixes.Select(f => f.Code).Order());
        Assert.Equal(dry.Issues.Select(i => i.Code).Order(), applied.Issues.Select(i => i.Code).Order());
        Assert.Contains(applied.Issues, i => i.Code == OrgMembershipReconcileCodes.OwnerAmbiguous && i.OrgId == ambiguousOrg);
        await using var after = NewContext();
        Assert.Equal([owner], await after.OrgMembers.IgnoreQueryFilters().Select(m => m.UserId).ToListAsync());
    }

    // ─── Data ───────────────────────────────────────────────────────────────────────────────────────────

    private async Task<(Guid OrgA, Guid OrgB, string User)> SeedTwoOrgsAndAUserAsync()
    {
        var orgA = await SeedOrgAsync();
        var orgB = await SeedOrgAsync();
        var user = await SeedUserAsync(orgA);
        return (orgA, orgB, user);
    }

    private async Task<Guid> SeedOrgAsync()
    {
        await using var db = NewContext();
        var org = new OrgEntity
        {
            Name = "Org AM-01",
            Slug = $"am01-{Guid.NewGuid():N}",
            DisplayName = "Org AM-01",
            ContactEmail = "am01@example.com",
            PlanTier = PlanTier.Starter,
            IsActive = true,
        };
        db.Orgs.Add(org);
        await db.SaveChangesAsync();
        return org.Id;
    }

    private async Task<string> SeedUserAsync(Guid? orgId, UserRole role = UserRole.None)
    {
        await using var db = NewContext();
        var user = new User
        {
            Id = $"auth0|am01-pg-{Guid.NewGuid():N}",
            Email = $"{Guid.NewGuid():N}@example.com",
            FirstName = "Test",
            LastName = "AM01",
            Role = role,
            OrgId = orgId,
            IsActive = true,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    private sealed class FixedTenantContext(Guid? orgId, bool filterEnabled) : ITenantContext
    {
        public Guid? OrgId { get; } = orgId;

        public bool FilterEnabled { get; } = filterEnabled;
    }
}
