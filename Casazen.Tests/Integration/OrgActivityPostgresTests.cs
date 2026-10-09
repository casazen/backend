using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Multitenancy;
using Casazen.Core.OrgTeam;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email.Templates;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Integration.Postgres;
using Casazen.Tests.Unit.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// AM-02b on a real PostgreSQL database, with the real services (<see cref="OrgInvitationTestKit"/> over a migrated database):
/// what the in-memory tests cannot prove about the activity log. The line is in the transaction of the change it records (a
/// rollback takes both away; a line that cannot be written takes the change with it); the table, its indexes and its keys; the
/// tenant filter; the read side (filters, paging that never repeats a line, the stream of the CSV) on real SQL and a real jsonb
/// column; the retention with its run lock; and the daily limit of the access requests, which holds when the requests arrive
/// together. Every test states what must hold <b>whichever request wins</b>.
/// </summary>
public class OrgActivityPostgresTests : IAsyncLifetime
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

    private async Task<string> TextAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(_database!.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
    }

    // ─── The table ──────────────────────────────────────────────────────────────────────────────────────

    [PostgresFact]
    public async Task Table_HasItsIndexesItsJsonColumnItsCascadeAndNoKeyToTheAccount()
    {
        Assert.Contains("DESC", await TextAsync("SELECT indexdef FROM pg_indexes WHERE indexname = 'IX_OrgActivityEntries_OrgId_When'"));
        Assert.Equal("1", await TextAsync("SELECT COUNT(*) FROM pg_indexes WHERE indexname = 'IX_OrgActivityEntries_OrgId_Type'"));
        Assert.Equal("1", await TextAsync("SELECT COUNT(*) FROM pg_indexes WHERE indexname = 'IX_OrgActivityEntries_When'"));
        Assert.Equal(
            "jsonb",
            await TextAsync("SELECT data_type FROM information_schema.columns WHERE table_name = 'OrgActivityEntries' AND column_name = 'DetailsJson'"));
        Assert.Equal(
            "timestamp with time zone",
            await TextAsync("SELECT data_type FROM information_schema.columns WHERE table_name = 'OrgActivityEntries' AND column_name = 'When'"));
        // The lines go with their org; the only foreign key of the table is that one (the history outlives the account).
        Assert.Equal("c", await TextAsync("SELECT confdeltype FROM pg_constraint WHERE conname = 'FK_OrgActivityEntries_Orgs_OrgId'"));
        Assert.Equal("1", await TextAsync("SELECT COUNT(*) FROM pg_constraint WHERE conrelid = '\"OrgActivityEntries\"'::regclass AND contype = 'f'"));
    }

    [PostgresFact]
    public async Task Row_EveryFieldIsStoredAndReadBack_TheInstantExactly()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync();
        _kit.Clock.SetUtcNow(new DateTimeOffset(2026, 10, 9, 8, 7, 6, TimeSpan.Zero).AddTicks(9_876_543));
        await using (var db = _kit.NewDb())
        {
            _kit.Activity(db).Record(OrgActivity.Of(
                org.Id, OrgActivityType.MemberRoleChanged, OwnerId, "auth0|anna",
                (OrgActivityDetailKeys.FromRole, "Collaborator"), (OrgActivityDetailKeys.ToRole, "Admin")));
            await db.SaveChangesAsync();
        }

        var line = Assert.Single(await _kit.ReadActivityAsync(org.Id));

        // PostgreSQL keeps microseconds: what the clock said, cut to them, is exactly what comes back.
        Assert.Equal(new DateTime(2026, 10, 9, 8, 7, 6, DateTimeKind.Utc).AddTicks(9_876_540), line.When);
        Assert.Equal(
            (OrgActivityType.MemberRoleChanged, OrgActivityArea.Account, OrgActivitySubjectType.Member, OwnerId, "auth0|anna"),
            (line.Type, line.Area, line.SubjectType, line.ActorUserId, line.SubjectId));
        Assert.Equal(
            new Dictionary<string, string> { ["fromRole"] = "Collaborator", ["toRole"] = "Admin" },
            OrgActivityDetails.Parse(line.DetailsJson));
        // jsonb holds the object (its text form is normalized by the database).
        Assert.Equal(
            "Admin",
            await TextAsync($"SELECT \"DetailsJson\"->>'toRole' FROM \"OrgActivityEntries\" WHERE \"Id\" = '{line.Id}'"));
        Assert.Equal("1", await TextAsync($"SELECT \"Area\" FROM \"OrgActivityEntries\" WHERE \"Id\" = '{line.Id}'"));
        Assert.Equal("4", await TextAsync($"SELECT \"Type\" FROM \"OrgActivityEntries\" WHERE \"Id\" = '{line.Id}'"));
    }

    [PostgresFact]
    public async Task ForeignKey_DeletingTheOrg_TakesItsLinesWithIt()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync();
        var (other, _) = await _kit.SeedOwnerOrgAsync("auth0|owner-b");
        await RecordAsync(org.Id, OrgActivityType.PlanChanged);
        await RecordAsync(other.Id, OrgActivityType.PlanChanged);

        await using var connection = new NpgsqlConnection(_database!.ConnectionString);
        await connection.OpenAsync();
        await using var delete = connection.CreateCommand();
        delete.CommandText = $"DELETE FROM \"Orgs\" WHERE \"Id\" = '{org.Id}'";
        await delete.ExecuteNonQueryAsync();

        Assert.Empty(await _kit.ReadActivityAsync(org.Id));
        Assert.Single(await _kit.ReadActivityAsync(other.Id));
    }

    [PostgresFact]
    public async Task ALineForAnOrgThatDoesNotExist_IsRefusedByTheDatabase()
    {
        await using var db = _kit.NewDb();
        _kit.Activity(db).Record(OrgActivity.Of(Guid.NewGuid(), OrgActivityType.PlanChanged, null, Guid.NewGuid().ToString()));

        var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());

        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, Assert.IsType<PostgresException>(error.InnerException).SqlState);
    }

    [PostgresFact]
    public async Task TenantFilter_TheLinesAreScopedToTheCallersOrgAndFailClosed()
    {
        var (orgA, _) = await _kit.SeedOwnerOrgAsync("auth0|owner-a");
        var (orgB, _) = await _kit.SeedOwnerOrgAsync("auth0|owner-b");
        await RecordAsync(orgA.Id, OrgActivityType.MemberInvited, subject: "inv-a");
        await RecordAsync(orgB.Id, OrgActivityType.MemberInvited, subject: "inv-b");

        await using var asA = new AppDbContext(_database!.CreateOptions(), new FixedTenantContext(orgA.Id, filterEnabled: true));
        await using var asB = new AppDbContext(_database.CreateOptions(), new FixedTenantContext(orgB.Id, filterEnabled: true));
        await using var noOrg = new AppDbContext(_database.CreateOptions(), new FixedTenantContext(null, filterEnabled: true));
        await using var system = new AppDbContext(_database.CreateOptions(), new FixedTenantContext(null, filterEnabled: false));

        Assert.Equal(["inv-a"], await asA.OrgActivityEntries.Select(e => e.SubjectId).ToListAsync());
        Assert.Equal(["inv-b"], await asB.OrgActivityEntries.Select(e => e.SubjectId).ToListAsync());
        Assert.Empty(await noOrg.OrgActivityEntries.ToListAsync());
        Assert.Equal(2, await system.OrgActivityEntries.CountAsync());

        // The reader of the page, handed another org's id by mistake, finds nothing.
        var page = await new OrgActivityService(asA).ListAsync(orgB.Id, new OrgActivityFilter(), 1, 50);
        Assert.Empty(page.Items);
        Assert.Equal(["inv-a"], (await new OrgActivityService(asA).ListAsync(orgA.Id, new OrgActivityFilter(), 1, 50)).Items.Select(i => i.SubjectId));
    }

    // ─── The same transaction as the change ─────────────────────────────────────────────────────────────

    [PostgresFact]
    public async Task ARolledBackTransaction_TakesTheChangeAndItsLineAway_ACommittedOneKeepsBoth()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync();
        var (_, member) = await _kit.SeedMemberAsync(org.Id, "auth0|anna", OrgRole.Collaborator);

        // The services join a transaction that is already open and leave the commit to its owner.
        await using (var db = _kit.NewDb())
        {
            await using var outer = await db.Database.BeginTransactionAsync();
            await _kit.Team(db).ChangeRoleAsync(org.Id, member.Id, OrgRole.Accountant, OwnerId);
            await outer.RollbackAsync();
        }

        Assert.Equal(OrgRole.Collaborator, (await _kit.ReadMemberAsync("auth0|anna"))!.Role);
        Assert.Empty(await _kit.ReadActivityAsync(org.Id));

        await using (var db = _kit.NewDb())
        {
            await using var outer = await db.Database.BeginTransactionAsync();
            await _kit.Team(db).ChangeRoleAsync(org.Id, member.Id, OrgRole.Accountant, OwnerId);
            await outer.CommitAsync();
        }

        Assert.Equal(OrgRole.Accountant, (await _kit.ReadMemberAsync("auth0|anna"))!.Role);
        Assert.Equal([OrgActivityType.MemberRoleChanged], (await _kit.ReadActivityAsync(org.Id)).Select(l => l.Type));
    }

    [PostgresFact]
    public async Task AChangeAndItsLine_AreOneSave_ForThePlanAndTheSettingsToo()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync(tier: PlanTier.Pro, status: SubscriptionStatus.None);

        await using (var db = _kit.NewDb())
        {
            await using var outer = await db.Database.BeginTransactionAsync();
            await new OrgService(db, _kit.Activity(db)).UpdatePlanTierAsync(org.Id, PlanTier.Starter, OwnerId);
            await new OrgService(db, _kit.Activity(db)).UpdateSettingsAsync(org.Id, "Villa Nuova", "villa-nuova-test", "host@example.com", false, OwnerId);
            await outer.RollbackAsync();
        }

        await using (var verify = _kit.NewDb())
        {
            var stored = await verify.Orgs.AsNoTracking().SingleAsync(o => o.Id == org.Id);
            Assert.Equal((PlanTier.Pro, "Casa Rossi"), (stored.PlanTier, stored.Name));
        }

        Assert.Empty(await _kit.ReadActivityAsync(org.Id));
    }

    [PostgresFact]
    public async Task ALineThatCannotBeWritten_TakesTheChangeWithIt_ForTheTeam()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync();
        var (_, member) = await _kit.SeedMemberAsync(org.Id, "auth0|anna", OrgRole.Collaborator);

        await using (var db = _kit.NewDb())
        {
            var team = new OrgTeamService(
                db, _kit.Seats(db), _kit.Membership(db), _kit.Cache.Object, new LineOfNoOrgLog(db), NullLogger<OrgTeamService>.Instance);

            await Assert.ThrowsAsync<DbUpdateException>(() => team.ChangeRoleAsync(org.Id, member.Id, OrgRole.Accountant, OwnerId));
        }

        // The role was saved before the line, in the same transaction: the failure of the line took it back.
        var stored = await _kit.ReadMemberAsync("auth0|anna");
        Assert.Equal(OrgRole.Collaborator, stored!.Role);
        Assert.Equal(["short-rent/staff"], await _kit.MembershipsAsync("auth0|anna"));
    }

    [PostgresFact]
    public async Task ALineThatCannotBeWritten_TakesTheChangeWithIt_ForTheInvitationAndItsAcceptance()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync();
        await _kit.SeedNewUserAsync("auth0|anna", "anna@example.com");

        await using (var db = _kit.NewDb())
        {
            var failing = Invitations(db, new LineOfNoOrgLog(db));
            await Assert.ThrowsAsync<DbUpdateException>(() => failing.CreateAsync(
                new CreateOrgInvitation(org.Id, OwnerId, "anna@example.com", "Anna Leone", OrgRole.Collaborator, ["short-rent"])));
        }

        await using (var verify = _kit.NewDb())
            Assert.Empty(await verify.OrgInvitations.IgnoreQueryFilters().Where(i => i.OrgId == org.Id).ToListAsync());

        // And an acceptance: the invitation stays pending, the person is no member, no consent was kept.
        var (invitation, token) = await _kit.SeedInvitationAsync(org.Id, "anna@example.com");
        await using (var db = _kit.NewDb())
        {
            await Assert.ThrowsAsync<DbUpdateException>(() => Invitations(db, new LineOfNoOrgLog(db)).AcceptAsync(
                new AcceptOrgInvitation(token, "auth0|anna", "anna@example.com", true, false, OrgInvitationTestKit.Consents(), "203.0.113.7")));
        }

        Assert.Equal(OrgInvitationStatus.Pending, (await _kit.ReadInvitationAsync(invitation.Id)).Status);
        Assert.Null(await _kit.ReadMemberAsync("auth0|anna"));
        await using var check = _kit.NewDb();
        Assert.False(await check.ConsentRecords.IgnoreQueryFilters().AnyAsync(c => c.UserId == "auth0|anna" && c.OrgId == org.Id));
    }

    [PostgresFact]
    public async Task ALineThatCannotBeWritten_TakesTheChangeWithIt_ForThePlanAndThePropertyAccess()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync(tier: PlanTier.Pro, status: SubscriptionStatus.None);
        var (_, member) = await _kit.SeedMemberAsync(org.Id, "auth0|anna", OrgRole.Collaborator);
        Guid propertyId;
        await using (var db = _kit.NewDb())
        {
            var property = HostScopeScenario.NewProperty(org.Id, OwnerId, "Trullo");
            db.Properties.Add(property);
            await db.SaveChangesAsync();
            propertyId = property.Id;
        }

        await using (var db = _kit.NewDb())
        {
            await Assert.ThrowsAsync<DbUpdateException>(
                () => new OrgService(db, new LineOfNoOrgLog(db)).UpdatePlanTierAsync(org.Id, PlanTier.Starter, OwnerId));
        }

        await using (var db = _kit.NewDb())
        {
            var access = new OrgPropertyAccessService(
                db, _kit.Cache.Object, new LineOfNoOrgLog(db), NullLogger<OrgPropertyAccessService>.Instance, _kit.Clock);
            await Assert.ThrowsAsync<DbUpdateException>(
                () => access.SetAsync(org.Id, member.Id, OwnerId, PropertyScope.Selected, [propertyId]));
        }

        await using var verify = _kit.NewDb();
        Assert.Equal(PlanTier.Pro, (await verify.Orgs.AsNoTracking().SingleAsync(o => o.Id == org.Id)).PlanTier);
        Assert.Empty(await verify.PropertyMemberAccesses.IgnoreQueryFilters().ToListAsync());
        Assert.Equal(PropertyScope.All, (await _kit.ReadMemberAsync("auth0|anna"))!.PropertyScope);
    }

    private OrgInvitationService Invitations(AppDbContext db, IActivityLog activityLog) => new(
        db,
        _kit.Seats(db),
        _kit.Membership(db),
        new OrgEmptinessChecker(db),
        _kit.Onboarding(db),
        _kit.Auth0.Object,
        _kit.Cache.Object,
        _kit.Emails,
        Casazen.Tests.Unit.Email.EmailTestHelpers.Links(),
        activityLog,
        NullLogger<OrgInvitationService>.Instance,
        _kit.Clock);

    /// <summary>A log that writes a line no database accepts (an org that does not exist): the way to make the save of the line fail.</summary>
    private sealed class LineOfNoOrgLog(AppDbContext db) : IActivityLog
    {
        public void Record(OrgActivity activity) => db.OrgActivityEntries.Add(new OrgActivityEntry
        {
            OrgId = Guid.NewGuid(),
            When = DateTime.UtcNow,
            Area = OrgActivityArea.Account,
            Type = activity.Type,
            SubjectType = OrgActivitySubjectType.Org,
            SubjectId = activity.SubjectId,
        });
    }

    // ─── Reading ────────────────────────────────────────────────────────────────────────────────────────

    [PostgresFact]
    public async Task List_Filters_AndPaging_OnRealSql_NeverRepeatOrSkipALine()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync();
        var (other, _) = await _kit.SeedOwnerOrgAsync("auth0|owner-b");
        var t0 = _kit.Now;
        for (var i = 0; i < 9; i++)
        {
            // Three lines share each instant, to make the tie-break by id matter.
            _kit.Clock.SetUtcNow(new DateTimeOffset(t0.AddMinutes(i / 3)));
            await RecordAsync(
                org.Id,
                i % 3 == 0 ? OrgActivityType.MemberInvited : OrgActivityType.MemberRoleChanged,
                actor: i % 2 == 0 ? "auth0|owner" : null,
                subject: $"subject-{i}");
        }

        await RecordAsync(other.Id, OrgActivityType.PlanChanged, subject: "foreign");

        await using var db = _kit.NewDb();
        var service = _kit.ActivityReader(db);

        var seen = new List<Guid>();
        for (var page = 1; page <= 3; page++)
        {
            var result = await service.ListAsync(org.Id, new OrgActivityFilter(), page, 4);
            Assert.Equal(9, result.TotalCount);
            seen.AddRange(result.Items.Select(i => i.Id));
        }

        Assert.Equal(9, seen.Distinct().Count());
        Assert.Equal(seen, (await ToListAsync(service.StreamAsync(org.Id, new OrgActivityFilter()))).Select(i => i.Id));

        Assert.Equal(3, (await service.ListAsync(org.Id, new OrgActivityFilter(Types: [OrgActivityType.MemberInvited]), 1, 50)).TotalCount);
        Assert.Equal(5, (await service.ListAsync(org.Id, new OrgActivityFilter(ActorUserId: "auth0|owner"), 1, 50)).TotalCount);
        Assert.Equal(4, (await service.ListAsync(org.Id, new OrgActivityFilter(SystemActor: true), 1, 50)).TotalCount);
        Assert.Equal(3, (await service.ListAsync(org.Id, new OrgActivityFilter(From: t0.AddMinutes(1), To: t0.AddMinutes(1)), 1, 50)).TotalCount);
        Assert.Equal(9, (await service.ListAsync(org.Id, new OrgActivityFilter(Area: OrgActivityArea.Account), 1, 50)).TotalCount);
        Assert.Equal(0, (await service.ListAsync(org.Id, new OrgActivityFilter(Area: OrgActivityArea.Supplier), 1, 50)).TotalCount);
        Assert.DoesNotContain("foreign", (await ToListAsync(service.StreamAsync(org.Id, new OrgActivityFilter()))).Select(i => i.SubjectId));
    }

    private static async Task<List<OrgActivityItem>> ToListAsync(IAsyncEnumerable<OrgActivityItem> items)
    {
        var list = new List<OrgActivityItem>();
        await foreach (var item in items)
            list.Add(item);
        return list;
    }

    // ─── Retention ──────────────────────────────────────────────────────────────────────────────────────

    [PostgresFact]
    public async Task Retention_DeletesWhatIsOlderThanTwelveMonths_InBatches_AndTheSecondRunFindsNothing()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync();
        var (other, _) = await _kit.SeedOwnerOrgAsync("auth0|owner-b");
        var old = _kit.Now.AddMonths(-14);
        var total = OrgActivityRetentionService.BatchSize * 2 + 11;
        await using (var db = _kit.NewDb())
        {
            for (var i = 0; i < total; i++)
            {
                db.OrgActivityEntries.Add(new OrgActivityEntry
                {
                    OrgId = i % 2 == 0 ? org.Id : other.Id,
                    When = old.AddMinutes(i),
                    Area = OrgActivityArea.Account,
                    Type = OrgActivityType.MemberDeactivated,
                    SubjectType = OrgActivitySubjectType.Member,
                    SubjectId = $"auth0|p{i}",
                });
            }

            await db.SaveChangesAsync();
        }

        await RecordAsync(org.Id, OrgActivityType.MemberDeactivated, subject: "recent");
        _kit.Clock.Advance(TimeSpan.FromDays(40));

        OrgActivityRetentionResult first;
        await using (var db = _kit.NewDb())
            first = await _kit.Retention(db).RunAsync();
        OrgActivityRetentionResult second;
        await using (var db = _kit.NewDb())
            second = await _kit.Retention(db).RunAsync();

        Assert.Equal((false, total), (first.Skipped, first.Deleted));
        Assert.Equal((false, 0), (second.Skipped, second.Deleted));
        Assert.Equal(["recent"], (await _kit.ReadActivityAsync(org.Id)).Select(l => l.SubjectId));
        Assert.Empty(await _kit.ReadActivityAsync(other.Id));
    }

    [PostgresFact]
    public async Task Retention_WhileAnotherSessionHoldsTheRunLock_IsSkipped_AndTheNextRunDoesTheWork()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync();
        await using (var db = _kit.NewDb())
        {
            db.OrgActivityEntries.Add(new OrgActivityEntry
            {
                OrgId = org.Id,
                When = _kit.Now.AddMonths(-20),
                Area = OrgActivityArea.Account,
                Type = OrgActivityType.MemberDeactivated,
                SubjectType = OrgActivitySubjectType.Member,
                SubjectId = "auth0|old",
            });
            await db.SaveChangesAsync();
        }

        OrgActivityRetentionResult skipped;
        await using (var holder = _kit.NewDb())
        {
            await using var handle = await PostgresAdvisoryLocks.TryAcquireSessionLockAsync(
                holder,
                PostgresAdvisoryLocks.Scope.OrgActivityRetention,
                OrgActivityRetentionService.RunLockKey,
                CancellationToken.None);
            Assert.NotNull(handle);

            await using var db = _kit.NewDb();
            skipped = await _kit.Retention(db).RunAsync();
        }

        Assert.Equal(OrgActivityRetentionResult.SkippedRun, skipped);
        Assert.Single(await _kit.ReadActivityAsync(org.Id));

        await using var next = _kit.NewDb();
        var ran = await _kit.Retention(next).RunAsync();

        Assert.Equal((false, 1), (ran.Skipped, ran.Deleted));
        Assert.Empty(await _kit.ReadActivityAsync(org.Id));
    }

    // ─── The daily limit of the access requests, together ───────────────────────────────────────────────

    [PostgresFact]
    public async Task AccessRequests_SixAtOnceFromOnePerson_ThreeWork_AndTheOthersAreTheLimit()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync();
        await _kit.SeedMemberAsync(org.Id, "auth0|admin", OrgRole.Admin);
        await _kit.SeedMemberAsync(org.Id, "auth0|collab", OrgRole.Collaborator);

        var outcomes = await InParallelAsync(Enumerable.Range(0, 6)
            .Select(i => (Func<Task>)(async () =>
            {
                await using var db = _kit.NewDb();
                await _kit.AccessRequests(db).RequestAsync(new RequestOrgAccess(org.Id, "auth0|collab", "billing", $"nota {i}", "it"));
            }))
            .ToArray());

        Assert.Equal(3, outcomes.Count(o => o.Succeeded));
        Assert.All(outcomes.Where(o => !o.Succeeded), o => Assert.Equal(OrgAccessRequestErrors.LimitReached, o.Code));
        var lines = await _kit.ReadActivityAsync(org.Id);
        Assert.Equal(3, lines.Count);
        Assert.All(lines, l => Assert.Equal(OrgActivityType.AccessRequested, l.Type));
        // Two administrators told, three times: nobody was told for a request that was refused.
        Assert.Equal(6, _kit.Emails.Snapshot().Count(e => e.Template == EmailTemplates.Names.OrgAccessRequest));
    }

    [PostgresFact]
    public async Task AccessRequests_DifferentPeopleAtOnce_AreNotHeldBackByEachOther()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync();
        var people = new[] { "auth0|p1", "auth0|p2", "auth0|p3", "auth0|p4" };
        foreach (var person in people)
            await _kit.SeedMemberAsync(org.Id, person, OrgRole.Collaborator);

        var outcomes = await InParallelAsync(people
            .Select(person => (Func<Task>)(async () =>
            {
                await using var db = _kit.NewDb();
                await _kit.AccessRequests(db).RequestAsync(new RequestOrgAccess(org.Id, person, "reports", null));
            }))
            .ToArray());

        Assert.All(outcomes, o => Assert.True(o.Succeeded, o.Code));
        Assert.Equal(people.Order(), (await _kit.ReadActivityAsync(org.Id)).Select(l => l.ActorUserId!).Order());
    }

    // ─── Shared ─────────────────────────────────────────────────────────────────────────────────────────

    private async Task RecordAsync(Guid orgId, OrgActivityType type, string? actor = OwnerId, string subject = "auth0|anna")
    {
        await using var db = _kit.NewDb();
        var info = OrgActivityCatalog.Describe(type);
        db.OrgActivityEntries.Add(new OrgActivityEntry
        {
            OrgId = orgId,
            When = ActivityLog.ToMicroseconds(_kit.Now),
            ActorUserId = actor,
            Area = info.DefaultArea,
            Type = type,
            SubjectType = info.SubjectType,
            SubjectId = subject,
            DetailsJson = "{}",
        });
        await db.SaveChangesAsync();
    }

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
