using System.Data.Common;
using System.Text.Json;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.OrgTeam;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Integration.Postgres;
using Casazen.Tests.Unit.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// AM-02c on a real PostgreSQL database, with the real services (<see cref="OrgInvitationTestKit"/> over a migrated
/// database): what the in-memory tests cannot prove. The cap on the emails of the invitations holds against requests that
/// arrive together (the seats lock serializes the check and the write), the properties an invitation carries are given once
/// with the membership however many times the link is opened, a change of areas against a change of role and a deactivation
/// never leaves a projection that nobody wrote, the overview sends the same number of commands whatever the size of the org,
/// the reach of a collaborator of one mode is decided in the SQL, and the matrix of the roles read from the rows the
/// migrations seeded is the one the analysis expects. Every test states what must hold <b>whichever request wins</b>.
/// </summary>
public class OrgTeamCompletionsPostgresTests : IAsyncLifetime
{
    private const string OwnerId = "auth0|owner";

    private PostgresTestDatabase? _database;
    private OrgInvitationTestKit _kit = null!;

    public async Task InitializeAsync()
    {
        _database = await PostgresTestDatabase.CreateAsync();
        await using (var db = _database.CreateContext())
            await db.Database.MigrateAsync();

        _kit = new OrgInvitationTestKit(() => _database.CreateContext());
    }

    public async Task DisposeAsync()
    {
        if (_database is not null)
            await _database.DisposeAsync();
    }

    // ─── The cap on the emails ──────────────────────────────────────────────────────────────────────────

    [PostgresFact]
    public async Task Cap_EightInvitationsAtOnce_LetExactlyTheLimitThrough_AndTheSendsAddUpToIt()
    {
        _kit.Settings["OrgTeam:InvitationDailyLimit"] = "3";
        var (org, _) = await _kit.SeedOwnerOrgAsync();

        var outcomes = await InParallelAsync(Enumerable.Range(0, 8)
            .Select(i => (Func<Task>)(() => CreateInvitationAsync(Invite(org.Id, $"persona{i}@example.com"))))
            .ToArray());

        Assert.Equal(3, outcomes.Count(o => o.Succeeded));
        Assert.All(outcomes.Where(o => !o.Succeeded), o => Assert.Equal(OrgInvitationErrors.DailyLimitReached, o.Code));
        Assert.Equal(3, _kit.Emails.Snapshot().Count);
        await using var verify = _kit.NewDb();
        var logs = await verify.OrgInvitations.IgnoreQueryFilters().Where(i => i.OrgId == org.Id).Select(i => i.SendLog).ToListAsync();
        Assert.Equal(3, logs.Sum(log => log.Count));
    }

    [PostgresFact]
    public async Task Cap_IsPerOrg_TheLimitOfOneOrgDoesNotUseTheOtherOnesAllowance()
    {
        _kit.Settings["OrgTeam:InvitationDailyLimit"] = "1";
        var (orgA, _) = await _kit.SeedOwnerOrgAsync("auth0|owner-a");
        var (orgB, _) = await _kit.SeedOwnerOrgAsync("auth0|owner-b");

        var outcomes = await InParallelAsync(
            () => CreateInvitationAsync(Invite(orgA.Id, "a1@example.com", "auth0|owner-a")),
            () => CreateInvitationAsync(Invite(orgA.Id, "a2@example.com", "auth0|owner-a")),
            () => CreateInvitationAsync(Invite(orgB.Id, "b1@example.com", "auth0|owner-b")),
            () => CreateInvitationAsync(Invite(orgB.Id, "b2@example.com", "auth0|owner-b")));

        // One for each org, whichever of the two requests of an org wins.
        Assert.Equal(2, outcomes.Count(o => o.Succeeded));
        Assert.All(outcomes.Where(o => !o.Succeeded), o => Assert.Equal(OrgInvitationErrors.DailyLimitReached, o.Code));
        Assert.Single(await ListAsync(orgA.Id));
        Assert.Single(await ListAsync(orgB.Id));
    }

    [PostgresFact]
    public async Task Resend_TheSameInvitationThreeTimesAtOnce_OnlyOneGoesThrough()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync();
        var (invitation, _) = await _kit.SeedInvitationAsync(org.Id, "tardi@example.com");

        var outcomes = await InParallelAsync(
            () => ResendAsync(org.Id, invitation.Id),
            () => ResendAsync(org.Id, invitation.Id),
            () => ResendAsync(org.Id, invitation.Id));

        Assert.Single(outcomes, o => o.Succeeded);
        Assert.All(outcomes.Where(o => !o.Succeeded), o => Assert.Equal(OrgInvitationErrors.ResendTooSoon, o.Code));
        Assert.Single(_kit.Emails.Snapshot());
        var stored = await _kit.ReadInvitationAsync(invitation.Id);
        Assert.Equal(_kit.Now, stored.LastSentAt);
        Assert.Single(stored.SendLog);
    }

    // ─── The properties of the invitation ───────────────────────────────────────────────────────────────

    [PostgresFact]
    public async Task Accept_TheSameLinkFiveTimesAtOnce_GivesTheCarriedPropertiesOnce_AndOnlyTheLiveOnes()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync();
        var first = await _kit.SeedPropertyAsync(org.Id, "Casa Uno");
        var second = await _kit.SeedPropertyAsync(org.Id, "Casa Due");
        var gone = await _kit.SeedPropertyAsync(org.Id, "Casa Tre");
        var notCarried = await _kit.SeedPropertyAsync(org.Id, "Casa Quattro");
        await _kit.SeedNewUserAsync("auth0|anna", "anna@example.com");
        OrgInvitationSent sent;
        await using (var create = _kit.NewDb())
        {
            sent = await _kit.Invitations(create).CreateAsync(
                Invite(org.Id, "anna@example.com", propertyScope: PropertyScope.Selected, propertyIds: [first, second, gone]));
        }

        // (Guids are sorted on the client on both sides: PostgreSQL orders a uuid byte by byte, .NET does not.)
        Assert.Equal(new[] { first, second, gone }.Order(), (await _kit.ReadInvitationPropertiesAsync(sent.Invitation.Id)).Order());
        var token = OrgInvitationTestKit.TokenInEmail(Assert.Single(_kit.Emails.Snapshot()));

        // A property deleted between the invitation and the acceptance is skipped, not an error.
        await using (var admin = _kit.NewDb())
        {
            await admin.Database.ExecuteSqlAsync($"""UPDATE "Properties" SET "IsDeleted" = TRUE WHERE "Id" = {gone}""");
        }

        var accepted = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => AcceptAsync(token, "auth0|anna", "anna@example.com")));

        Assert.All(accepted, a => Assert.Equal((org.Id, OrgRole.Collaborator), (a.OrgId, a.Role)));
        Assert.Equal(new[] { first, second }.Order(), (await _kit.ReadGrantsAsync("auth0|anna")).Order());
        Assert.DoesNotContain(notCarried, await _kit.ReadGrantsAsync("auth0|anna"));
        var member = await _kit.ReadMemberAsync("auth0|anna");
        Assert.Equal((OrgRole.Collaborator, PropertyScope.Selected), (member!.Role, member.PropertyScope));

        // «Who gave what to whom» is one line for the acceptance, whoever of the five calls did the work.
        var lines = await _kit.ReadActivityAsync(org.Id);
        var access = Assert.Single(lines, l => l.Type == OrgActivityType.MemberPropertyAccessChanged);
        using (var details = JsonDocument.Parse(access.DetailsJson))
            Assert.Equal("2", details.RootElement.GetProperty(OrgActivityDetailKeys.Granted).GetString());
        Assert.Single(lines, l => l.Type == OrgActivityType.InvitationAccepted);
        Assert.Equal(OrgInvitationStatus.Accepted, (await _kit.ReadInvitationAsync(sent.Invitation.Id)).Status);
    }

    [PostgresFact]
    public async Task Accept_InsideATransactionThatIsRolledBack_TakesTheMemberTheGrantsAndTheLinesAway()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync();
        var property = await _kit.SeedPropertyAsync(org.Id, "Casa Uno");
        await _kit.SeedNewUserAsync("auth0|anna", "anna@example.com");
        OrgInvitationSent sent;
        await using (var create = _kit.NewDb())
        {
            sent = await _kit.Invitations(create).CreateAsync(
                Invite(org.Id, "anna@example.com", propertyScope: PropertyScope.Selected, propertyIds: [property]));
        }

        var token = OrgInvitationTestKit.TokenInEmail(Assert.Single(_kit.Emails.Snapshot()));

        // The member, its memberships, its grants, the closing of the invitation and the lines of the log are one unit of work: the
        // service joins a transaction that is already open and leaves the commit to its owner, so a rollback takes all of it away.
        await using (var db = _kit.NewDb())
        {
            await using var outer = await db.Database.BeginTransactionAsync();
            await _kit.Invitations(db).AcceptAsync(new AcceptOrgInvitation(
                token, "auth0|anna", "anna@example.com", true, false, OrgInvitationTestKit.Consents(), "203.0.113.7"));
            await outer.RollbackAsync();
        }

        Assert.Empty(await _kit.ReadGrantsAsync("auth0|anna"));
        Assert.Null(await _kit.ReadMemberAsync("auth0|anna"));
        Assert.Equal(OrgInvitationStatus.Pending, (await _kit.ReadInvitationAsync(sent.Invitation.Id)).Status);
        Assert.Equal([OrgActivityType.MemberInvited], (await _kit.ReadActivityAsync(org.Id)).Select(l => l.Type));
    }

    [PostgresFact]
    public async Task Maintenance_ThePurgeOfAnOldInvitation_TakesItsCarriedPropertiesWithIt_ByTheCascadeOfTheDatabase()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync();
        var first = await _kit.SeedPropertyAsync(org.Id, "Casa Uno");
        var second = await _kit.SeedPropertyAsync(org.Id, "Casa Due");
        var (old, _) = await _kit.SeedInvitationAsync(
            org.Id, "vecchio@example.com", status: OrgInvitationStatus.Revoked, expiresAt: _kit.Now.AddDays(-40), closedAt: _kit.Now.AddDays(-31));
        var (recent, _) = await _kit.SeedInvitationAsync(
            org.Id, "recente@example.com", status: OrgInvitationStatus.Revoked, expiresAt: _kit.Now.AddDays(-10), closedAt: _kit.Now.AddDays(-5));
        await using (var seed = _kit.NewDb())
        {
            seed.OrgInvitationProperties.AddRange(
                new OrgInvitationProperty { OrgId = org.Id, InvitationId = old.Id, PropertyId = first },
                new OrgInvitationProperty { OrgId = org.Id, InvitationId = old.Id, PropertyId = second },
                new OrgInvitationProperty { OrgId = org.Id, InvitationId = recent.Id, PropertyId = first });
            await seed.SaveChangesAsync();
        }

        OrgInvitationMaintenanceResult result;
        await using (var db = _kit.NewDb())
            result = await _kit.Maintenance(db).RunAsync();

        // On PostgreSQL the purge does not read the carried rows: the foreign key deletes them with the invitation.
        Assert.Equal(1, result.Purged);
        Assert.Empty(await _kit.ReadInvitationPropertiesAsync(old.Id));
        Assert.Equal([first], await _kit.ReadInvitationPropertiesAsync(recent.Id));
    }

    // ─── The areas of a member ──────────────────────────────────────────────────────────────────────────

    [PostgresFact]
    public async Task SetAreas_AgainstAChangeOfRoleAndADeactivation_EightRounds_NeverLeavesAProjectionNobodyWrote()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync();

        for (var round = 0; round < 8; round++)
        {
            var userId = $"auth0|membro-{round}";
            var (_, member) = await _kit.SeedMemberAsync(org.Id, userId, OrgRole.Collaborator, ["short-rent", "long-rent"]);

            var outcomes = await InParallelAsync(
                () => TeamAsync(team => team.SetAreasAsync(org.Id, member.Id, ["long-rent"], OwnerId)),
                () => TeamAsync(team => team.ChangeRoleAsync(org.Id, member.Id, OrgRole.Accountant, OwnerId)),
                () => TeamAsync(team => team.DeactivateAsync(org.Id, member.Id, OwnerId)));

            // Three independent writes of one member: all of them are allowed, and whatever the order the result is coherent.
            Assert.All(outcomes, o => Assert.True(o.Succeeded, $"round {round}: {o.Code}"));
            await AssertTheProjectionIsTheOneOfTheMemberAsync(userId);
        }
    }

    private async Task AssertTheProjectionIsTheOneOfTheMemberAsync(string userId)
    {
        var member = await _kit.ReadMemberAsync(userId);
        Assert.NotNull(member);
        var memberships = (await _kit.MembershipsAsync(userId)).Order(StringComparer.Ordinal).ToList();
        var areas = memberships
            .Where(m => !m.StartsWith($"{AccountContext.Key}/", StringComparison.Ordinal))
            .Select(m => m.Split('/')[0])
            .ToList();
        Assert.NotEmpty(areas);

        // The rows of the role the member ended up with, in the areas it ended up in, and no other.
        var expected = OrgRoleCatalog.ProjectionOf(member.Role, areas)
            .Select(p => $"{p.ContextKey}/{p.RoleKey}")
            .Order(StringComparer.Ordinal)
            .ToList();
        Assert.Equal(expected, memberships);
    }

    // ─── The reach of a collaborator of one mode ────────────────────────────────────────────────────────

    [PostgresFact]
    public async Task HostScope_ACollaboratorOfOneMode_ReachesOnlyTheGrantedPropertiesOfThatMode_AndTheOverviewCountsInsideIt()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync();
        var shortGranted = await _kit.SeedPropertyAsync(org.Id, "Breve data", RentalMode.Short);
        var longGranted = await _kit.SeedPropertyAsync(org.Id, "Lunga data", RentalMode.Long);
        await _kit.SeedPropertyAsync(org.Id, "Breve non data", RentalMode.Short);
        await _kit.SeedMemberAsync(org.Id, "auth0|anna", OrgRole.Collaborator, ["short-rent", "long-rent"]);
        await using (var grant = _kit.NewDb())
        {
            foreach (var propertyId in new[] { shortGranted, longGranted })
            {
                grant.PropertyMemberAccesses.Add(new PropertyMemberAccess
                {
                    OrgId = org.Id,
                    UserId = "auth0|anna",
                    PropertyId = propertyId,
                    CreatedAt = _kit.Now,
                    CreatedByUserId = OwnerId,
                });
            }

            await grant.SaveChangesAsync();
        }

        var bothAreas = new HostScope(org.Id, GrantedToUserId: "auth0|anna");
        var onlyShort = bothAreas with { GrantedMode = RentalMode.Short };
        var onlyLong = bothAreas with { GrantedMode = RentalMode.Long };

        // The grants of the other mode stay in the table; the area decides which of them the person reaches.
        Assert.Equal(new[] { shortGranted, longGranted }.Order(), (await ReachedAsync(bothAreas)).Order());
        Assert.Equal([shortGranted], await ReachedAsync(onlyShort));
        Assert.Equal([longGranted], await ReachedAsync(onlyLong));

        await using var db = _kit.NewDb();
        var overview = await _kit.Overview(db).GetAsync(org.Id, onlyShort, OrgOverviewSections.All.ToHashSet());
        Assert.Equal((1, 1, 0), (overview.Properties!.Total, overview.Properties.Short, overview.Properties.Long));
    }

    [PostgresFact]
    public async Task Audience_ACollaboratorOfOneArea_IsToldAboutAGrantedPropertyOfItsMode_AndNotOfTheOtherOne()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync();
        var shortProperty = await _kit.SeedPropertyAsync(org.Id, "Breve", RentalMode.Short);
        var longProperty = await _kit.SeedPropertyAsync(org.Id, "Lunga", RentalMode.Long);
        var (_, member) = await _kit.SeedMemberAsync(org.Id, "auth0|anna", OrgRole.Collaborator, ["short-rent"]);
        await using (var grant = _kit.NewDb())
            await _kit.PropertyAccess(grant).SetAsync(org.Id, member.Id, OwnerId, PropertyScope.Selected, [shortProperty, longProperty]);

        await using var db = _kit.NewDb();
        var forShort = await HostNotificationAudience.UsersToTell(db, org.Id, shortProperty, "auth0|anna", OwnerId).Select(u => u.Id).ToListAsync();
        var forLong = await HostNotificationAudience.UsersToTell(db, org.Id, longProperty, "auth0|anna", OwnerId).Select(u => u.Id).ToListAsync();

        // The person in charge is told only about the property it can open: the grant on the long-term one stays in the table and
        // no longer reaches anybody who works in short-term rentals only. The owner hears of both, as always.
        Assert.Contains("auth0|anna", forShort);
        Assert.DoesNotContain("auth0|anna", forLong);
        Assert.Contains(OwnerId, forShort);
        Assert.Contains(OwnerId, forLong);
    }

    private async Task<List<Guid>> ReachedAsync(HostScope scope)
    {
        await using var db = _kit.NewDb();
        return await db.Properties.AsNoTracking()
            .Where(p => p.OrgId == scope.OrgId)
            .InScope(scope)
            .Select(p => p.Id)
            .OrderBy(id => id)
            .ToListAsync();
    }

    // ─── The overview ───────────────────────────────────────────────────────────────────────────────────

    [PostgresFact]
    public async Task Overview_TheNumberOfCommands_DoesNotGrowWithTheSizeOfTheOrg()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync();
        await GrowAsync(org.Id, from: 0, count: 1);
        var small = await CommandsOfTheOverviewAsync(org.Id, expectedProperties: 1);

        await GrowAsync(org.Id, from: 1, count: 6);
        var medium = await CommandsOfTheOverviewAsync(org.Id, expectedProperties: 7);

        await GrowAsync(org.Id, from: 7, count: 24);
        var large = await CommandsOfTheOverviewAsync(org.Id, expectedProperties: 31);

        // A count or a grouped count for each figure, never a query per property or per person.
        Assert.True(small > 0);
        Assert.True(small <= 20, $"{small} commands for the smallest org");
        Assert.Equal(small, medium);
        Assert.Equal(small, large);
    }

    private async Task GrowAsync(Guid orgId, int from, int count)
    {
        for (var i = from; i < from + count; i++)
        {
            await _kit.SeedPropertyAsync(orgId, $"Casa {i}", i % 2 == 0 ? RentalMode.Short : RentalMode.Long);
            await _kit.SeedMemberAsync(orgId, $"auth0|crescita-{i}", OrgRole.Collaborator);
            await _kit.SeedInvitationAsync(orgId, $"crescita{i}@example.com");

            await using var db = _kit.NewDb();
            _kit.Activity(db).Record(OrgActivity.Of(
                orgId, OrgActivityType.MemberInvited, OwnerId, Guid.NewGuid().ToString(), (OrgActivityDetailKeys.Role, "Collaborator")));
            await db.SaveChangesAsync();
        }
    }

    private async Task<int> CommandsOfTheOverviewAsync(Guid orgId, int expectedProperties)
    {
        var counter = new CommandCounter();
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>(_database!.CreateOptions()).AddInterceptors(counter).Options);

        var overview = await _kit.Overview(db).GetAsync(orgId, new HostScope(orgId), OrgOverviewSections.All.ToHashSet());

        Assert.Equal(expectedProperties, overview.Properties!.Total);
        Assert.True(overview.Invitations!.Pending >= 1);
        Assert.NotEmpty(overview.RecentActivity!);
        return counter.Count;
    }

    /// <summary>Counts the commands a context sends to the server.</summary>
    private sealed class CommandCounter : DbCommandInterceptor
    {
        private int _count;

        public int Count => _count;

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
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

    // ─── The matrix of the roles ────────────────────────────────────────────────────────────────────────

    /// <summary>The levels of each role in the order of the screen (booking, prices, guests, money, leases, rents, jobs, people, billing, suppliers, settings, security).</summary>
    private static readonly (OrgRole Role, string Levels)[] ExpectedLevels =
    [
        (OrgRole.Owner, "Y Y Y Y Y Y Y Y Y Y Y Y"),
        (OrgRole.Admin, "Y Y Y Y Y Y Y Y Y Y Y Y"),
        (OrgRole.PropertyManager, "Y Y Y Y Y Y Y N N Y N N"),
        (OrgRole.Collaborator, "R R Y N N N Y N N N N N"),
        (OrgRole.Accountant, "R R N R R R R N R N N N"),
    ];

    [PostgresFact]
    public async Task RoleMatrix_ReadFromTheRowsTheMigrationsSeeded_IsTheOneOfTheAnalysis()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync();

        await using var db = _kit.NewDb();
        var roles = await _kit.RoleMatrix(db).GetAsync(org.Id, OwnerId);

        Assert.Equal(ExpectedLevels.Select(e => e.Role), roles.Select(r => r.Role));
        foreach (var (role, expected) in ExpectedLevels)
        {
            var view = roles.Single(r => r.Role == role);
            var levels = string.Join(' ', view.Capabilities.Select(c => c.Level switch
            {
                OrgCapabilityLevel.Yes => "Y",
                OrgCapabilityLevel.Read => "R",
                _ => "N",
            }));
            Assert.Equal(expected, levels);
        }

        // The restricted membership of the property manager (AM-D12) is in the matrix as the migration seeded it.
        var manager = roles.Single(r => r.Role == OrgRole.PropertyManager);
        Assert.Equal(AccountContext.RoleKeys.Manager, manager.PermissionsByContext[AccountContext.Key].RoleKey);
        Assert.Equal([AccountContext.Permissions.SuppliersManage], manager.PermissionsByContext[AccountContext.Key].Permissions);
        Assert.DoesNotContain(AccountContext.Key, roles.Single(r => r.Role == OrgRole.Collaborator).PermissionsByContext.Keys);
    }

    // ─── Shared ─────────────────────────────────────────────────────────────────────────────────────────

    private static CreateOrgInvitation Invite(
        Guid orgId,
        string email,
        string actor = OwnerId,
        PropertyScope propertyScope = PropertyScope.All,
        IReadOnlyCollection<Guid>? propertyIds = null) =>
        new(orgId, actor, email, "Persona di Prova", OrgRole.Collaborator, ["short-rent"], propertyScope, PropertyIds: propertyIds);

    private async Task CreateInvitationAsync(CreateOrgInvitation request)
    {
        await using var db = _kit.NewDb();
        await _kit.Invitations(db).CreateAsync(request);
    }

    private async Task ResendAsync(Guid orgId, Guid invitationId)
    {
        await using var db = _kit.NewDb();
        await _kit.Invitations(db).ResendAsync(orgId, invitationId, OwnerId);
    }

    private async Task TeamAsync(Func<OrgTeamService, Task> action)
    {
        await using var db = _kit.NewDb();
        await action(_kit.Team(db));
    }

    private async Task<OrgInvitationAccepted> AcceptAsync(string token, string userId, string email)
    {
        await using var db = _kit.NewDb();
        return await _kit.Invitations(db).AcceptAsync(new AcceptOrgInvitation(
            token, userId, email, true, false, OrgInvitationTestKit.Consents(), "203.0.113.7"));
    }

    private async Task<IReadOnlyList<OrgInvitationView>> ListAsync(Guid orgId)
    {
        await using var db = _kit.NewDb();
        return await _kit.Invitations(db).ListAsync(orgId);
    }

    /// <summary>The result of one request that raced the others: it worked, or a rule of the domain refused it (anything else fails the test).</summary>
    private sealed record Outcome(bool Succeeded, string? Code);

    private static async Task<Outcome> OutcomeOfAsync(Func<Task> action)
    {
        try
        {
            await action();
            return new Outcome(true, null);
        }
        catch (DomainException ex)
        {
            return new Outcome(false, ex.Code);
        }
    }

    /// <summary>Starts every action at the same moment (a shared gate) and waits for all of them.</summary>
    private static async Task<Outcome[]> InParallelAsync(params Func<Task>[] actions)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = actions
            .Select(async action =>
            {
                await gate.Task;
                return await OutcomeOfAsync(action);
            })
            .ToList();
        gate.SetResult();
        return await Task.WhenAll(running);
    }
}
