using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Multitenancy;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email.Templates;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Integration.Postgres;
using Casazen.Tests.Unit.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// AM-02 on a real PostgreSQL database, with the real services (<see cref="OrgInvitationTestKit"/> over a migrated database):
/// what the in-memory tests cannot prove. The last seat goes to one request only, however many arrive together; the same
/// person is invited once; a token accepted twice at once adds one member and four consents; a person invited by two orgs
/// joins one; an acceptance that leaves an empty org is all or nothing; the maintenance never runs twice at once and does
/// its work (reminder with a new link, expiry, deletion after the retention); the tenant filter of the invitations.
/// Every test states what must hold <b>whichever request wins</b>, so none depends on the order the database serves them.
/// </summary>
public class OrgInvitationsPostgresTests : IAsyncLifetime
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

    // ─── The last seat ──────────────────────────────────────────────────────────────────────────────────

    [PostgresFact]
    public async Task CreateAsync_SixInvitationsForTheLastSeat_CreateExactlyOne()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync(tier: PlanTier.Starter, status: SubscriptionStatus.None);

        var outcomes = await InParallelAsync(Enumerable.Range(0, 6)
            .Select(i => (Func<Task>)(() => CreateInvitationAsync(Invite(org.Id, $"person{i}@example.com"))))
            .ToArray());

        Assert.Single(outcomes, o => o.Succeeded);
        Assert.All(outcomes.Where(o => !o.Succeeded), o => Assert.Equal(OrgSeatErrors.LimitReached, o.Code));
        Assert.Equal((1, 1), await SeatsAsync(org.Id));
        await using var verify = _kit.NewDb();
        Assert.Equal(1, await verify.OrgInvitations.IgnoreQueryFilters().CountAsync(i => i.OrgId == org.Id));
    }

    [PostgresFact]
    public async Task CreateAsync_TheSamePersonSixTimesAtOnce_CreatesOneInvitation()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync();
        var spellings = new[] { "same@example.com", "SAME@example.com", " same@example.com ", "Same@Example.COM", "same@example.com", "sAmE@example.com" };

        var outcomes = await InParallelAsync(spellings
            .Select(email => (Func<Task>)(() => CreateInvitationAsync(Invite(org.Id, email))))
            .ToArray());

        Assert.Single(outcomes, o => o.Succeeded);
        Assert.All(outcomes.Where(o => !o.Succeeded), o => Assert.Equal(OrgInvitationErrors.AlreadyPending, o.Code));
        await using var verify = _kit.NewDb();
        var rows = await verify.OrgInvitations.IgnoreQueryFilters().Where(i => i.OrgId == org.Id).ToListAsync();
        Assert.Equal("same@example.com", Assert.Single(rows).Email);
    }

    [PostgresFact]
    public async Task ReactivateAndInvite_ForTheLastSeat_OnlyOneOfThemWins()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync(tier: PlanTier.Starter, status: SubscriptionStatus.None);
        var (_, deactivated) = await _kit.SeedMemberAsync(org.Id, "auth0|dora", OrgRole.Collaborator, status: OrgMemberStatus.Deactivated);

        var outcomes = await InParallelAsync(
            () => CreateInvitationAsync(Invite(org.Id, "nuova@example.com")),
            async () =>
            {
                await using var db = _kit.NewDb();
                await _kit.Team(db).ReactivateAsync(org.Id, deactivated.Id, OwnerId);
            });

        Assert.Single(outcomes, o => o.Succeeded);
        Assert.Equal(OrgSeatErrors.LimitReached, Assert.Single(outcomes, o => !o.Succeeded).Code);
        var usage = await UsageAsync(org.Id);
        Assert.Equal(2, usage.Used);
        Assert.True(usage.Used <= usage.Max);
    }

    [PostgresFact]
    public async Task AcceptAndInvite_OnTheLastSeat_NeverOverbook()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync(tier: PlanTier.Starter, status: SubscriptionStatus.None);
        await _kit.SeedNewUserAsync("auth0|bea", "bea@example.com");
        var (_, token) = await _kit.SeedInvitationAsync(org.Id, "bea@example.com");

        var outcomes = await InParallelAsync(
            () => AcceptAsync(token, "auth0|bea", "bea@example.com"),
            () => CreateInvitationAsync(Invite(org.Id, "carlo@example.com")));

        // The pending invitation already held the second seat: the acceptance takes it over, and nobody else gets one.
        Assert.True(outcomes[0].Succeeded, outcomes[0].Code);
        Assert.Equal(OrgSeatErrors.LimitReached, outcomes[1].Code);
        Assert.Equal((2, 0), await SeatsAsync(org.Id));
    }

    // ─── Acceptance under concurrency ───────────────────────────────────────────────────────────────────

    [PostgresFact]
    public async Task AcceptAsync_TheSameTokenFiveTimesAtOnce_AddsOneMemberAndRecordsTheConsentsOnce()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync();
        await _kit.SeedNewUserAsync("auth0|anna", "anna@example.com");
        var (invitation, token) = await _kit.SeedInvitationAsync(org.Id, "anna@example.com", OrgRole.Accountant);

        // A double click (or a retry after a lost answer) finds the work done: every call answers the same acceptance.
        var accepted = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => AcceptAsync(token, "auth0|anna", "anna@example.com")));

        Assert.All(accepted, a => Assert.Equal((org.Id, OrgRole.Accountant), (a.OrgId, a.Role)));
        await using var verify = _kit.NewDb();
        var member = await verify.OrgMembers.IgnoreQueryFilters().SingleAsync(m => m.UserId == "auth0|anna");
        Assert.Equal((org.Id, OrgRole.Accountant, OrgMemberStatus.Active), (member.OrgId, member.Role, member.Status));
        Assert.Equal(
            new[] { ConsentType.Tos, ConsentType.Privacy, ConsentType.Dpa, ConsentType.SubprocessorsAck }.Order(),
            (await verify.ConsentRecords.IgnoreQueryFilters().Where(c => c.UserId == "auth0|anna").Select(c => c.Type).ToListAsync()).Order());
        Assert.Equal(OrgInvitationStatus.Accepted, (await _kit.ReadInvitationAsync(invitation.Id)).Status);
        Assert.Equal(org.Id, (await _kit.ReadUserAsync("auth0|anna")).OrgId);
        Assert.Equal((2, 0), await SeatsAsync(org.Id));
    }

    [PostgresFact]
    public async Task AcceptAsync_AnInvitationOfTwoOrgsAtOnce_TheSamePersonJoinsOneOnly()
    {
        var (orgA, _) = await _kit.SeedOwnerOrgAsync("auth0|owner-a");
        var (orgB, _) = await _kit.SeedOwnerOrgAsync("auth0|owner-b");
        await _kit.SeedNewUserAsync("auth0|anna", "anna@example.com");
        var (invitationA, tokenA) = await _kit.SeedInvitationAsync(orgA.Id, "anna@example.com", invitedBy: "auth0|owner-a");
        var (invitationB, tokenB) = await _kit.SeedInvitationAsync(orgB.Id, "anna@example.com", invitedBy: "auth0|owner-b");

        var outcomes = await InParallelAsync(
            () => AcceptAsync(tokenA, "auth0|anna", "anna@example.com"),
            () => AcceptAsync(tokenB, "auth0|anna", "anna@example.com"));

        // One org gets her; for the other she is a person who already has one, and its invitation stays open.
        Assert.Single(outcomes, o => o.Succeeded);
        Assert.Equal(OrgInvitationErrors.UserHasOrganization, Assert.Single(outcomes, o => !o.Succeeded).Code);

        await using var verify = _kit.NewDb();
        var member = await verify.OrgMembers.IgnoreQueryFilters().SingleAsync(m => m.UserId == "auth0|anna");
        Assert.Equal(member.OrgId, (await _kit.ReadUserAsync("auth0|anna")).OrgId);
        var winner = member.OrgId == orgA.Id ? invitationA : invitationB;
        var loser = member.OrgId == orgA.Id ? invitationB : invitationA;
        Assert.Equal(OrgInvitationStatus.Accepted, (await _kit.ReadInvitationAsync(winner.Id)).Status);
        Assert.Equal(OrgInvitationStatus.Pending, (await _kit.ReadInvitationAsync(loser.Id)).Status);

        // Nothing of the org that lost: no consent, and the memberships are the winner's alone.
        var consents = await verify.ConsentRecords.IgnoreQueryFilters().Where(c => c.UserId == "auth0|anna").ToListAsync();
        Assert.Equal(4, consents.Count);
        Assert.All(consents, c => Assert.Equal(member.OrgId, c.OrgId));
        Assert.Equal(["short-rent/staff"], await _kit.MembershipsAsync("auth0|anna"));
    }

    [PostgresFact]
    public async Task AcceptAsync_ThreePeopleOfTheSameOrgAtOnce_AllJoinAndTheSeatsAddUp()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync();
        var people = new[] { ("auth0|anna", "anna@example.com"), ("auth0|bruno", "bruno@example.com"), ("auth0|carla", "carla@example.com") };
        var tokens = new List<string>();
        foreach (var (id, email) in people)
        {
            await _kit.SeedNewUserAsync(id, email);
            tokens.Add((await _kit.SeedInvitationAsync(org.Id, email)).Token);
        }

        var outcomes = await InParallelAsync(people
            .Select((person, i) => (Func<Task>)(() => AcceptAsync(tokens[i], person.Item1, person.Item2)))
            .ToArray());

        Assert.All(outcomes, o => Assert.True(o.Succeeded, o.Code));
        Assert.Equal((4, 0), await SeatsAsync(org.Id));
    }

    [PostgresFact]
    public async Task AcceptAndRevoke_AtOnce_ExactlyOneOfThemWins()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync();
        await _kit.SeedNewUserAsync("auth0|anna", "anna@example.com");
        var (invitation, token) = await _kit.SeedInvitationAsync(org.Id, "anna@example.com");

        var outcomes = await InParallelAsync(
            () => AcceptAsync(token, "auth0|anna", "anna@example.com"),
            async () =>
            {
                await using var db = _kit.NewDb();
                await _kit.Invitations(db).RevokeAsync(org.Id, invitation.Id, OwnerId);
            });

        var stored = await _kit.ReadInvitationAsync(invitation.Id);
        var member = await _kit.ReadMemberAsync("auth0|anna");
        if (outcomes[0].Succeeded)
        {
            Assert.Equal(OrgInvitationErrors.NotPending, outcomes[1].Code);
            Assert.Equal(OrgInvitationStatus.Accepted, stored.Status);
            Assert.Equal(org.Id, member?.OrgId);
        }
        else
        {
            Assert.Equal(OrgInvitationErrors.Revoked, outcomes[0].Code);
            Assert.True(outcomes[1].Succeeded);
            Assert.Equal(OrgInvitationStatus.Revoked, stored.Status);
            Assert.Null(member);
        }
    }

    [PostgresFact]
    public async Task AcceptAsync_TwoOwnersOfEmptyOrgsJoinTheSameOrgAtOnce_BothLeaveTheirs()
    {
        var (target, _) = await _kit.SeedOwnerOrgAsync();
        var (orgPaola, _) = await _kit.SeedPersonWithEmptyOrgAsync("auth0|paola", "paola@example.com");
        var (orgQuinto, _) = await _kit.SeedPersonWithEmptyOrgAsync("auth0|quinto", "quinto@example.com");
        var (_, tokenPaola) = await _kit.SeedInvitationAsync(target.Id, "paola@example.com");
        var (_, tokenQuinto) = await _kit.SeedInvitationAsync(target.Id, "quinto@example.com");

        var accepted = await Task.WhenAll(
            AcceptAsync(tokenPaola, "auth0|paola", "paola@example.com"),
            AcceptAsync(tokenQuinto, "auth0|quinto", "quinto@example.com"));

        Assert.All(accepted, a => Assert.True(a.LeftEmptyOrg));
        await using var verify = _kit.NewDb();
        Assert.Empty(await verify.Orgs.Where(o => (o.Id == orgPaola.Id || o.Id == orgQuinto.Id) && o.IsActive).ToListAsync());
        Assert.Equal(3, await verify.OrgMembers.IgnoreQueryFilters().CountAsync(m => m.OrgId == target.Id));
        Assert.Empty(await verify.OrgMembers.IgnoreQueryFilters().Where(m => m.OrgId == orgPaola.Id || m.OrgId == orgQuinto.Id).ToListAsync());
    }

    [PostgresFact]
    public async Task AcceptAsync_LeavingAnEmptyOrgAndARoleRowIsMissing_SavesNothingNotEvenTheLeaving()
    {
        var (target, _) = await _kit.SeedOwnerOrgAsync();
        var (oldOrg, _) = await _kit.SeedPersonWithEmptyOrgAsync("auth0|anna", "anna@example.com");
        var (invitation, token) = await _kit.SeedInvitationAsync(target.Id, "anna@example.com");
        await using (var admin = _kit.NewDb())
        {
            await admin.Database.ExecuteSqlRawAsync(
                """DELETE FROM "Roles" WHERE "ContextKey" = 'short-rent' AND "RoleKey" = 'staff'""");
        }

        // The leaving is saved first and the new role is looked up after it: the failure must undo the leaving too.
        await Assert.ThrowsAsync<InvalidOperationException>(() => AcceptAsync(token, "auth0|anna", "anna@example.com"));

        await using var verify = _kit.NewDb();
        Assert.True((await verify.Orgs.SingleAsync(o => o.Id == oldOrg.Id)).IsActive);
        var member = await verify.OrgMembers.IgnoreQueryFilters().SingleAsync(m => m.UserId == "auth0|anna");
        Assert.Equal((oldOrg.Id, OrgRole.Owner), (member.OrgId, member.Role));
        var user = await _kit.ReadUserAsync("auth0|anna");
        Assert.Equal((oldOrg.Id, UserRole.PropertyOwner, RentalType.ShortTerm), (user.OrgId, user.Role, user.RentalType));
        Assert.Equal(["account/org_owner", "short-rent/property_owner"], await _kit.MembershipsAsync("auth0|anna"));
        Assert.Equal(OrgInvitationStatus.Pending, (await _kit.ReadInvitationAsync(invitation.Id)).Status);
        Assert.Empty(await verify.ConsentRecords.IgnoreQueryFilters().Where(c => c.UserId == "auth0|anna" && c.OrgId == target.Id).ToListAsync());
    }

    // ─── Reading what the database stores ───────────────────────────────────────────────────────────────

    [PostgresFact]
    public async Task LookupAsync_ReadsThePreviewFromTheDatabase_AndEveryOtherLinkIsTheSameNull()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync();
        var (_, open) = await _kit.SeedInvitationAsync(org.Id, "open@example.com", OrgRole.PropertyManager, areas: ["short-rent", "long-rent"]);
        var (_, revoked) = await _kit.SeedInvitationAsync(org.Id, "revoked@example.com", status: OrgInvitationStatus.Revoked, closedAt: _kit.Now);
        var (_, expired) = await _kit.SeedInvitationAsync(org.Id, "expired@example.com", expiresAt: _kit.Now.AddSeconds(-1));

        await using var db = _kit.NewDb();
        var service = _kit.Invitations(db);

        var preview = await service.LookupAsync(open);
        Assert.NotNull(preview);
        Assert.Equal(("Casa Rossi", "open@example.com", OrgRole.PropertyManager), (preview.OrgName, preview.Email, preview.Role));
        Assert.Equal(["long-rent", "short-rent"], preview.Areas.Order());
        Assert.Null(await service.LookupAsync(revoked));
        Assert.Null(await service.LookupAsync(expired));
        Assert.Null(await service.LookupAsync(new string('a', 64)));
        Assert.Null(await service.LookupAsync("not a token"));
    }

    [PostgresFact]
    public async Task ResendAsync_AnExpiredInvitation_ComesBackWithANewLinkAndTakesASeatAgain()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync(tier: PlanTier.Starter, status: SubscriptionStatus.None);
        var (invitation, oldToken) = await _kit.SeedInvitationAsync(org.Id, "late@example.com", expiresAt: _kit.Now.AddSeconds(-1));
        Assert.Equal((1, 0), await SeatsAsync(org.Id));

        OrgInvitationSent sent;
        await using (var db = _kit.NewDb())
            sent = await _kit.Invitations(db).ResendAsync(org.Id, invitation.Id, OwnerId);

        Assert.True(sent.EmailQueued);
        Assert.Equal((1, 1), await SeatsAsync(org.Id));
        await using var read = _kit.NewDb();
        Assert.Null(await _kit.Invitations(read).LookupAsync(oldToken));
        var email = Assert.Single(_kit.Emails.Snapshot());
        Assert.NotNull(await _kit.Invitations(read).LookupAsync(OrgInvitationTestKit.TokenInEmail(email)));
    }

    // ─── Maintenance ────────────────────────────────────────────────────────────────────────────────────

    [PostgresFact]
    public async Task Maintenance_OneRun_RemindsWithANewLink_ExpiresAndPurgesAfterTheRetention_AndTheSecondRunDoesNothing()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync();
        var (reminder, oldToken) = await _kit.SeedInvitationAsync(org.Id, "due@example.com", expiresAt: _kit.Now.AddDays(3).AddHours(23));
        var (overdue, _) = await _kit.SeedInvitationAsync(org.Id, "late@example.com", expiresAt: _kit.Now.AddHours(-1));
        var (fresh, _) = await _kit.SeedInvitationAsync(org.Id, "fresh@example.com");
        var (old, _) = await _kit.SeedInvitationAsync(
            org.Id, "old@example.com", status: OrgInvitationStatus.Revoked, expiresAt: _kit.Now.AddDays(-40), closedAt: _kit.Now.AddDays(-31));
        var (recent, _) = await _kit.SeedInvitationAsync(
            org.Id, "recent@example.com", status: OrgInvitationStatus.Revoked, expiresAt: _kit.Now.AddDays(-10), closedAt: _kit.Now.AddDays(-5));

        OrgInvitationMaintenanceResult first;
        await using (var db = _kit.NewDb())
            first = await _kit.Maintenance(db).RunAsync();

        Assert.Equal(new OrgInvitationMaintenanceResult(false, Reminded: 1, Expired: 1, Purged: 1), first);
        var reminded = await _kit.ReadInvitationAsync(reminder.Id);
        Assert.Equal(_kit.Now, reminded.ReminderSentAt);
        Assert.NotEqual(reminder.TokenHash, reminded.TokenHash);
        var expired = await _kit.ReadInvitationAsync(overdue.Id);
        Assert.Equal(OrgInvitationStatus.Expired, expired.Status);
        Assert.Equal(expired.ExpiresAt, expired.ClosedAt);
        Assert.Null((await _kit.ReadInvitationAsync(fresh.Id)).ReminderSentAt);
        await using (var verify = _kit.NewDb())
        {
            var left = await verify.OrgInvitations.IgnoreQueryFilters().Select(i => i.Id).ToListAsync();
            Assert.DoesNotContain(old.Id, left);
            Assert.Contains(recent.Id, left);
        }

        // The reminder carries a new link; the one of the first email no longer works.
        var emails = _kit.Emails.Snapshot();
        var reminderMail = Assert.Single(emails, e => e.Template == EmailTemplates.Names.OrgInvitationReminder);
        Assert.Equal("due@example.com", reminderMail.To);
        await using (var read = _kit.NewDb())
        {
            Assert.Null(await _kit.Invitations(read).LookupAsync(oldToken));
            Assert.NotNull(await _kit.Invitations(read).LookupAsync(OrgInvitationTestKit.TokenInEmail(reminderMail)));
        }

        Assert.Single(emails, e => e.Template == EmailTemplates.Names.OrgInvitationExpired);

        // Nothing is done twice.
        OrgInvitationMaintenanceResult second;
        await using (var db = _kit.NewDb())
            second = await _kit.Maintenance(db).RunAsync();

        Assert.Equal(new OrgInvitationMaintenanceResult(false, 0, 0, 0), second);
        Assert.Equal(emails.Count, _kit.Emails.Snapshot().Count);
    }

    [PostgresFact]
    public async Task Maintenance_WhileAnotherSessionHoldsTheRunLock_IsSkippedAndTheNextRunDoesTheWork()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync();
        var (overdue, _) = await _kit.SeedInvitationAsync(org.Id, "late@example.com", expiresAt: _kit.Now.AddHours(-1));

        OrgInvitationMaintenanceResult skipped;
        await using (var holder = _kit.NewDb())
        {
            await using var handle = await PostgresAdvisoryLocks.TryAcquireSessionLockAsync(
                holder,
                PostgresAdvisoryLocks.Scope.OrgInvitationMaintenance,
                OrgInvitationMaintenanceService.RunLockKey,
                CancellationToken.None);
            Assert.NotNull(handle);

            await using var db = _kit.NewDb();
            skipped = await _kit.Maintenance(db).RunAsync();
        }

        Assert.Equal(OrgInvitationMaintenanceResult.SkippedRun, skipped);
        Assert.Equal(OrgInvitationStatus.Pending, (await _kit.ReadInvitationAsync(overdue.Id)).Status);

        var ran = await RunMaintenanceAsync();

        Assert.Equal((false, 1), (ran.Skipped, ran.Expired));
        Assert.Equal(OrgInvitationStatus.Expired, (await _kit.ReadInvitationAsync(overdue.Id)).Status);
    }

    [PostgresFact]
    public async Task Maintenance_TwoRunsAtOnce_ExpireAndTellTheInviterOnce()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync();
        for (var i = 0; i < 4; i++)
            await _kit.SeedInvitationAsync(org.Id, $"late{i}@example.com", expiresAt: _kit.Now.AddHours(-1 - i));

        var results = await Task.WhenAll(RunMaintenanceAsync(), RunMaintenanceAsync());

        // Whether the second run was skipped or found nothing left, together they did the work once.
        Assert.Equal(4, results.Sum(r => r.Expired));
        Assert.Equal(4, _kit.Emails.Snapshot().Count(e => e.Template == EmailTemplates.Names.OrgInvitationExpired));
        await using var verify = _kit.NewDb();
        Assert.Equal(4, await verify.OrgInvitations.IgnoreQueryFilters().CountAsync(i => i.Status == OrgInvitationStatus.Expired));
    }

    private async Task<OrgInvitationMaintenanceResult> RunMaintenanceAsync()
    {
        await using var db = _kit.NewDb();
        return await _kit.Maintenance(db).RunAsync();
    }

    // ─── Tenants ────────────────────────────────────────────────────────────────────────────────────────

    [PostgresFact]
    public async Task TenantFilter_OrgInvitations_AreScopedToTheCallersOrgAndFailClosed()
    {
        var (orgA, _) = await _kit.SeedOwnerOrgAsync("auth0|owner-a");
        var (orgB, _) = await _kit.SeedOwnerOrgAsync("auth0|owner-b");
        await _kit.SeedInvitationAsync(orgA.Id, "a@example.com", invitedBy: "auth0|owner-a");
        await _kit.SeedInvitationAsync(orgB.Id, "b@example.com", invitedBy: "auth0|owner-b");

        await using var asA = new AppDbContext(_database!.CreateOptions(), new FixedTenantContext(orgA.Id, filterEnabled: true));
        await using var asB = new AppDbContext(_database.CreateOptions(), new FixedTenantContext(orgB.Id, filterEnabled: true));
        await using var noOrg = new AppDbContext(_database.CreateOptions(), new FixedTenantContext(null, filterEnabled: true));
        await using var system = new AppDbContext(_database.CreateOptions(), new FixedTenantContext(null, filterEnabled: false));

        Assert.Equal(["a@example.com"], await asA.OrgInvitations.Select(i => i.Email).ToListAsync());
        Assert.Equal(["b@example.com"], await asB.OrgInvitations.Select(i => i.Email).ToListAsync());
        Assert.Empty(await noOrg.OrgInvitations.ToListAsync());
        Assert.Equal(2, await system.OrgInvitations.CountAsync());
        Assert.Equal(2, await asA.OrgInvitations.IgnoreQueryFilters().CountAsync());
    }

    [PostgresFact]
    public async Task TheInvitationsOfAnotherOrg_AreNeitherListedNorReachableByTheirId()
    {
        var (orgA, _) = await _kit.SeedOwnerOrgAsync("auth0|owner-a");
        var (orgB, _) = await _kit.SeedOwnerOrgAsync("auth0|owner-b");
        var (foreign, _) = await _kit.SeedInvitationAsync(orgB.Id, "b@example.com", invitedBy: "auth0|owner-b");

        Assert.Empty(await ListAsync(orgA.Id));
        foreach (var act in new Func<OrgInvitationService, Task>[]
                 {
                     service => service.ResendAsync(orgA.Id, foreign.Id, "auth0|owner-a"),
                     service => service.RevokeAsync(orgA.Id, foreign.Id, "auth0|owner-a"),
                     service => service.CopyLinkAsync(orgA.Id, foreign.Id, "auth0|owner-a"),
                 })
        {
            await using var db = _kit.NewDb();
            var error = await Assert.ThrowsAsync<NotFoundException>(() => act(_kit.Invitations(db)));
            Assert.Equal(OrgInvitationErrors.NotFound, error.Code);
        }

        Assert.Equal(OrgInvitationStatus.Pending, (await _kit.ReadInvitationAsync(foreign.Id)).Status);
        Assert.Single(await ListAsync(orgB.Id));
    }

    private async Task<IReadOnlyList<OrgInvitationView>> ListAsync(Guid orgId)
    {
        await using var db = _kit.NewDb();
        return await _kit.Invitations(db).ListAsync(orgId);
    }

    // ─── Shared ─────────────────────────────────────────────────────────────────────────────────────────

    private static CreateOrgInvitation Invite(Guid orgId, string email, OrgRole role = OrgRole.Collaborator) =>
        new(orgId, OwnerId, email, "Persona di Prova", role, ["short-rent"]);

    private async Task CreateInvitationAsync(CreateOrgInvitation request)
    {
        await using var db = _kit.NewDb();
        await _kit.Invitations(db).CreateAsync(request);
    }

    private async Task<OrgInvitationAccepted> AcceptAsync(string token, string userId, string email)
    {
        await using var db = _kit.NewDb();
        return await _kit.Invitations(db).AcceptAsync(new AcceptOrgInvitation(
            token, userId, email, true, false, OrgInvitationTestKit.Consents(), "203.0.113.7"));
    }

    private async Task<OrgSeatUsage> UsageAsync(Guid orgId)
    {
        await using var db = _kit.NewDb();
        return await _kit.Seats(db).GetUsageAsync(orgId);
    }

    private async Task<(int ActiveMembers, int PendingInvitations)> SeatsAsync(Guid orgId)
    {
        var usage = await UsageAsync(orgId);
        return (usage.ActiveMembers, usage.PendingInvitations);
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

    private sealed class FixedTenantContext(Guid? orgId, bool filterEnabled) : ITenantContext
    {
        public Guid? OrgId { get; } = orgId;

        public bool FilterEnabled { get; } = filterEnabled;
    }
}
