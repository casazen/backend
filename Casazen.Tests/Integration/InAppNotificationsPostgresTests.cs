using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
using Casazen.Core.Multitenancy;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Push;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Integration.Postgres;
using Casazen.Tests.Unit;
using Casazen.Tests.Unit.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Npgsql;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// UI-12a on a real PostgreSQL database: what the in-memory tests cannot prove about the in-app notifications. The table, its
/// unique and composite indexes and its two cascading keys; the job that writes the rows losing a race on the unique index
/// without an error and without a second row; the single statements that mark as read and delete (<c>ExecuteUpdate</c>,
/// <c>ExecuteDelete</c>); whose rows a user reaches (its own, in its orgs, with or without the host-org tenant filter);
/// and what happens to the rows when the user or the org is deleted (erasure).
/// </summary>
public class InAppNotificationsPostgresTests : IAsyncLifetime
{
    private static readonly DateTime Now = new(2026, 10, 9, 12, 0, 0, DateTimeKind.Utc);

    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(Now));
    private readonly Mock<IFeatureFlags> _flags = new();
    private PostgresTestDatabase? _database;
    private OrgInvitationTestKit _kit = null!;

    public async Task InitializeAsync()
    {
        _database = await PostgresTestDatabase.CreateAsync();
        await using (var db = _database.CreateContext())
            await db.Database.MigrateAsync();

        _kit = new OrgInvitationTestKit(() => _database.CreateContext());
        _flags.Setup(f => f.IsEnabled(FeatureFlags.InAppNotifications)).Returns(true);
    }

    public async Task DisposeAsync()
    {
        if (_database is not null)
            await _database.DisposeAsync();
    }

    private AppDbContext NewDb() => _database!.CreateContext();

    private InAppNotificationService Service(AppDbContext db) =>
        new(db, _clock, NullLogger<InAppNotificationService>.Instance);

    private InAppNotificationJob Job(AppDbContext db) =>
        new(db, _flags.Object, _clock, NullLogger<InAppNotificationJob>.Instance);

    /// <summary>One run of the job on a context of its own, the way a Hangfire worker takes it.</summary>
    private async Task RunJobAsync(string deliveryKey, QueuedInAppNotification queued)
    {
        await using var db = NewDb();
        await Job(db).CreateAsync(deliveryKey, queued, CancellationToken.None);
    }

    /// <summary>One call of the service on a context of its own, the way a request takes it.</summary>
    private async Task<T> CallAsync<T>(Func<InAppNotificationService, Task<T>> call)
    {
        await using var db = NewDb();
        return await call(Service(db));
    }

    private async Task<string> TextAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(_database!.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private async Task<int> ExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(_database!.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteNonQueryAsync();
    }

    // ─── Seeding ───

    /// <summary>An org with an owner, the owner's user row and member row (the way the onboarding leaves them).</summary>
    private async Task<(Guid OrgId, string UserId)> SeedHostAsync(string ownerId)
    {
        var (org, owner) = await _kit.SeedOwnerOrgAsync(ownerId);
        return (org.Id, owner.Id);
    }

    /// <summary>
    /// A supplier org with these accounts, each linked by <c>SupplierOrgId</c>; <c>OrgIdIsTheSupplierOrg</c> says whether its
    /// <c>OrgId</c> is the supplier org too (the legacy supplier-only account) or empty (the supplier-only account today).
    /// </summary>
    private async Task<Guid> SeedSupplierOrgAsync(params (string UserId, bool Active, bool OrgIdIsTheSupplierOrg)[] users)
    {
        await using var db = NewDb();
        var org = OrgTeamTestData.AddOrg(db, OrgType.Supplier);
        foreach (var (userId, active, orgIdIsTheSupplierOrg) in users)
        {
            var user = OrgTeamTestData.AddUser(db, userId, orgIdIsTheSupplierOrg ? org.Id : null, UserRole.Supplier);
            user.SupplierOrgId = org.Id;
            user.IsActive = active;
        }

        await db.SaveChangesAsync();
        return org.Id;
    }

    private async Task<Guid> SeedPropertyAsync(Guid orgId, string ownerId)
    {
        await using var db = NewDb();
        var property = new Property
        {
            OwnerId = ownerId,
            OrgId = orgId,
            Name = "Casa UI12a",
            Address = $"Via UI12a {Guid.NewGuid():N}",
            City = "Roma",
            PostalCode = "00100",
            Bedrooms = 2,
            Bathrooms = 1,
            MaxGuests = 4,
            NightlyRate = 100m,
            CinCode = "IT058091C27G5FFZDZ",
            IsActive = true,
        };
        db.Properties.Add(property);
        await db.SaveChangesAsync();
        return property.Id;
    }

    private static InAppNotification Row(string userId, Guid orgId, DateTime createdAt, DateTime? readAt = null, string? key = null) => new()
    {
        UserId = userId,
        OrgId = orgId,
        Type = PushTypes.NewBooking,
        EntityId = Guid.NewGuid(),
        DeliveryKey = key ?? $"key:{Guid.NewGuid():N}",
        CreatedAt = createdAt,
        ReadAt = readAt,
    };

    private async Task AddAsync(params InAppNotification[] rows)
    {
        await using var db = NewDb();
        db.InAppNotifications.AddRange(rows);
        await db.SaveChangesAsync();
    }

    private async Task<List<InAppNotification>> RowsAsync(string? userId = null)
    {
        await using var db = NewDb();
        return await db.InAppNotifications.AsNoTracking()
            .Where(n => userId == null || n.UserId == userId)
            .OrderBy(n => n.CreatedAt).ThenBy(n => n.UserId)
            .ToListAsync();
    }

    private static QueuedInAppNotification Queued(PushAudienceKind kind, Guid audienceId, string type, Guid? entityId = null) => new()
    {
        AudienceKind = kind,
        AudienceId = audienceId,
        Type = type,
        EntityId = entityId,
        OccurredAt = Now,
    };

    // ─── The table ───

    [PostgresFact]
    public async Task Table_HasItsIndexesItsTimestampColumnsAndTheTwoCascadingKeys()
    {
        var unique = await TextAsync("SELECT indexdef FROM pg_indexes WHERE indexname = 'UIX_InAppNotifications_DeliveryKey_UserId'");
        Assert.Contains("UNIQUE", unique);
        Assert.Contains("(\"DeliveryKey\", \"UserId\")", unique);
        Assert.Contains(
            "(\"UserId\", \"ReadAt\", \"CreatedAt\")",
            await TextAsync("SELECT indexdef FROM pg_indexes WHERE indexname = 'IX_InAppNotifications_UserId_ReadAt_CreatedAt'"));
        Assert.Equal("1", await TextAsync("SELECT COUNT(*) FROM pg_indexes WHERE indexname = 'IX_InAppNotifications_CreatedAt'"));
        Assert.Equal("1", await TextAsync("SELECT COUNT(*) FROM pg_indexes WHERE indexname = 'IX_InAppNotifications_OrgId'"));
        foreach (var column in new[] { "CreatedAt", "ReadAt" })
        {
            Assert.Equal(
                "timestamp with time zone",
                await TextAsync($"SELECT data_type FROM information_schema.columns WHERE table_name = 'InAppNotifications' AND column_name = '{column}'"));
        }

        Assert.Equal("YES", await TextAsync("SELECT is_nullable FROM information_schema.columns WHERE table_name = 'InAppNotifications' AND column_name = 'ReadAt'"));
        Assert.Equal("YES", await TextAsync("SELECT is_nullable FROM information_schema.columns WHERE table_name = 'InAppNotifications' AND column_name = 'EntityId'"));
        // The rows go with the org and with the account (erasure): both keys cascade.
        Assert.Equal("c", await TextAsync("SELECT confdeltype FROM pg_constraint WHERE conname = 'FK_InAppNotifications_Orgs_OrgId'"));
        Assert.Equal("c", await TextAsync("SELECT confdeltype FROM pg_constraint WHERE conname = 'FK_InAppNotifications_Users_UserId'"));
    }

    [PostgresFact]
    public async Task UniqueIndex_TheSameKeyAndUserTwice_IsRefused_AnotherUserOrAnotherKeyIsNot()
    {
        var (orgId, anna) = await SeedHostAsync("auth0|anna");
        var (_, bruno) = await SeedHostAsync("auth0|bruno");
        await AddAsync(Row(anna, orgId, Now, key: "booking:x:new"));

        await using (var duplicate = NewDb())
        {
            duplicate.InAppNotifications.Add(Row(anna, orgId, Now, key: "booking:x:new"));
            var error = await Assert.ThrowsAsync<DbUpdateException>(() => duplicate.SaveChangesAsync());
            var pg = Assert.IsType<PostgresException>(error.InnerException);
            Assert.Equal(PostgresErrorCodes.UniqueViolation, pg.SqlState);
            Assert.Equal(InAppNotification.OncePerEventAndUserIndexName, pg.ConstraintName);
        }

        await AddAsync(Row(bruno, orgId, Now, key: "booking:x:new"), Row(anna, orgId, Now, key: "booking:x:other"));
        Assert.Equal(3, (await RowsAsync()).Count);
    }

    [PostgresFact]
    public async Task ForeignKeys_ARowOfAnUnknownUserOrOrg_IsRefused()
    {
        var (orgId, anna) = await SeedHostAsync("auth0|anna");

        await using (var db = NewDb())
        {
            db.InAppNotifications.Add(Row("auth0|nobody", orgId, Now));
            var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, Assert.IsType<PostgresException>(error.InnerException).SqlState);
        }

        await using (var db = NewDb())
        {
            db.InAppNotifications.Add(Row(anna, Guid.NewGuid(), Now));
            var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, Assert.IsType<PostgresException>(error.InnerException).SqlState);
        }
    }

    // ─── The job on real SQL ───

    [PostgresFact]
    public async Task Job_TheInstantOfTheEvent_IsStoredAndReadBackExactly_ToTheMicrosecond()
    {
        var supplierOrgId = await SeedSupplierOrgAsync(("auth0|sara", true, true));
        var queued = Queued(PushAudienceKind.SupplierOrg, supplierOrgId, PushTypes.ServiceRequestCreated, Guid.NewGuid());
        // 100 ns finer than PostgreSQL keeps: the job cuts it, so the row reads back equal to what it wrote.
        queued.OccurredAt = Now.AddTicks(7);

        await RunJobAsync("service-request:t:created", queued);

        var row = Assert.Single(await RowsAsync("auth0|sara"));
        Assert.Equal(Now, row.CreatedAt);
        Assert.Equal(supplierOrgId, row.OrgId);
        Assert.Equal(queued.EntityId, row.EntityId);
    }

    [PostgresFact]
    public async Task Job_PropertyHosts_TellsTheOwnerOfTheOrg_WithNoDeviceAndOnce()
    {
        var (orgId, owner) = await SeedHostAsync("auth0|anna");
        var propertyId = await SeedPropertyAsync(orgId, owner);
        var queued = Queued(PushAudienceKind.PropertyHosts, propertyId, PushTypes.ServiceRequestCompleted, Guid.NewGuid());

        await RunJobAsync("service-request:r:Completato", queued);
        await RunJobAsync("service-request:r:Completato", queued);

        var row = Assert.Single(await RowsAsync());
        Assert.Equal((owner, orgId, PushTypes.ServiceRequestCompleted, queued.EntityId), (row.UserId, row.OrgId, row.Type, row.EntityId));
    }

    [PostgresFact]
    public async Task Job_SupplierOrg_TellsTheActiveUsersLinkedToTheSupplier_UnderTheSupplierOrg()
    {
        var (hostOrgId, hostAndSupplier) = await SeedHostAsync("auth0|host-and-supplier");
        var supplierOrgId = await SeedSupplierOrgAsync(("auth0|sara", true, true), ("auth0|gone", false, true));
        await using (var db = NewDb())
        {
            (await db.Users.SingleAsync(u => u.Id == hostAndSupplier)).SupplierOrgId = supplierOrgId;
            await db.SaveChangesAsync();
        }

        await RunJobAsync("service-request:s:created", Queued(PushAudienceKind.SupplierOrg, supplierOrgId, PushTypes.ServiceRequestCreated));

        // The member of the supplier org and the host who is a supplier too; not the inactive member. The rows belong to the
        // supplier org, not to the host org of the dual-role user.
        var rows = await RowsAsync();
        Assert.Equal(["auth0|host-and-supplier", "auth0|sara"], rows.Select(r => r.UserId).Order(StringComparer.Ordinal));
        Assert.All(rows, row => Assert.Equal(supplierOrgId, row.OrgId));
        Assert.DoesNotContain(rows, row => row.OrgId == hostOrgId);
    }

    [PostgresFact]
    public async Task Job_SeveralRunsOfTheSameEventAtTheSameTime_LoseTheRaceOnTheIndexQuietly_AndLeaveOneRowPerUser()
    {
        var supplierOrgId = await SeedSupplierOrgAsync(
            ("auth0|u1", true, true), ("auth0|u2", true, true), ("auth0|u3", true, true), ("auth0|u4", true, true));
        var queued = Queued(PushAudienceKind.SupplierOrg, supplierOrgId, PushTypes.ServiceRequestCreated, Guid.NewGuid());

        // Every run reads "nobody told yet" before any of them writes: the first INSERT wins, the others get 23505, read again
        // and find everything written. None of them fails.
        const int runs = 4;
        var rendezvous = new InsertRendezvous(runs);
        var options = new DbContextOptionsBuilder<AppDbContext>(_database!.CreateOptions()).AddInterceptors(rendezvous).Options;
        var tasks = Enumerable.Range(0, runs).Select(_ => Task.Run(async () =>
        {
            await using var db = new AppDbContext(options);
            await Job(db).CreateAsync("service-request:race:created", queued, CancellationToken.None);
        })).ToList();

        await Task.WhenAll(tasks);

        Assert.Equal(runs, rendezvous.Arrived);
        var rows = await RowsAsync();
        Assert.Equal(["auth0|u1", "auth0|u2", "auth0|u3", "auth0|u4"], rows.Select(r => r.UserId).Order(StringComparer.Ordinal));
        Assert.All(rows, row => Assert.Equal("service-request:race:created", row.DeliveryKey));
    }

    [PostgresFact]
    public async Task Job_ARunThatMeetsARowWrittenMeanwhile_WritesOnlyWhatIsMissing()
    {
        var supplierOrgId = await SeedSupplierOrgAsync(("auth0|u1", true, true), ("auth0|u2", true, true));
        await AddAsync(Row("auth0|u1", supplierOrgId, Now.AddMinutes(-1), key: "service-request:k:created"));

        await RunJobAsync("service-request:k:created", Queued(PushAudienceKind.SupplierOrg, supplierOrgId, PushTypes.ServiceRequestCreated));

        var rows = await RowsAsync();
        Assert.Equal(2, rows.Count);
        Assert.Equal(Now.AddMinutes(-1), rows.Single(r => r.UserId == "auth0|u1").CreatedAt);
        Assert.Equal(Now, rows.Single(r => r.UserId == "auth0|u2").CreatedAt);
    }

    // ─── Mark as read and the retention on real SQL ───

    [PostgresFact]
    public async Task MarkRead_SetsTheInstantOnceAndOnlyOnTheCallersOwnRow()
    {
        var (orgId, anna) = await SeedHostAsync("auth0|anna");
        var (otherOrgId, bruno) = await SeedHostAsync("auth0|bruno");
        var mine = Row(anna, orgId, Now.AddMinutes(-5));
        var mineToo = Row(anna, orgId, Now.AddMinutes(-4));
        var his = Row(bruno, otherOrgId, Now.AddMinutes(-3));
        await AddAsync(mine, mineToo, his);

        _clock.SetUtcNow(new DateTimeOffset(Now.AddTicks(7)));
        await CallAsync(async s => { await s.MarkReadAsync(anna, mine.Id); return 0; });
        _clock.SetUtcNow(new DateTimeOffset(Now.AddMinutes(10)));
        await CallAsync(async s => { await s.MarkReadAsync(anna, mine.Id); return 0; }); // idempotent: still the first instant

        var rows = (await RowsAsync()).ToDictionary(r => r.Id);
        Assert.Equal(Now, rows[mine.Id].ReadAt);
        Assert.Null(rows[mineToo.Id].ReadAt);
        Assert.Null(rows[his.Id].ReadAt);
    }

    [PostgresFact]
    public async Task MarkRead_AnotherUsersRowARowOfAnOrgTheUserDoesNotBelongToOrAnIdThatDoesNotExist_IsNotFound_AndNothingChanges()
    {
        var (orgId, anna) = await SeedHostAsync("auth0|anna");
        var (otherOrgId, bruno) = await SeedHostAsync("auth0|bruno");
        var his = Row(bruno, otherOrgId, Now.AddMinutes(-3));
        var mineInAnotherOrg = Row(anna, otherOrgId, Now.AddMinutes(-2));
        var mine = Row(anna, orgId, Now.AddMinutes(-1));
        await AddAsync(his, mineInAnotherOrg, mine);

        foreach (var id in new[] { his.Id, mineInAnotherOrg.Id, Guid.NewGuid() })
            await Assert.ThrowsAsync<NotFoundException>(() => CallAsync(async s => { await s.MarkReadAsync(anna, id); return 0; }));

        Assert.All(await RowsAsync(), row => Assert.Null(row.ReadAt));
    }

    [PostgresFact]
    public async Task MarkAllRead_MarksTheCallersUnreadRowsOnly_ReturnsHowManyAndKeepsTheInstantOfTheReadOnes()
    {
        var (orgId, anna) = await SeedHostAsync("auth0|anna");
        var (otherOrgId, bruno) = await SeedHostAsync("auth0|bruno");
        var readEarlier = Row(anna, orgId, Now.AddMinutes(-9), readAt: Now.AddMinutes(-8));
        await AddAsync(
            Row(anna, orgId, Now.AddMinutes(-7)),
            Row(anna, orgId, Now.AddMinutes(-6)),
            readEarlier,
            Row(anna, otherOrgId, Now.AddMinutes(-5)), // an org the user does not belong to: not visible, not touched
            Row(bruno, otherOrgId, Now.AddMinutes(-4)));

        var marked = await CallAsync(s => s.MarkAllReadAsync(anna));
        var again = await CallAsync(s => s.MarkAllReadAsync(anna));

        Assert.Equal(2, marked);
        Assert.Equal(0, again);
        var rows = await RowsAsync();
        Assert.Equal(Now, rows.Single(r => r.CreatedAt == Now.AddMinutes(-7)).ReadAt);
        Assert.Equal(Now, rows.Single(r => r.CreatedAt == Now.AddMinutes(-6)).ReadAt);
        Assert.Equal(Now.AddMinutes(-8), rows.Single(r => r.Id == readEarlier.Id).ReadAt);
        Assert.Null(rows.Single(r => r.UserId == anna && r.OrgId == otherOrgId).ReadAt);
        Assert.Null(rows.Single(r => r.UserId == bruno).ReadAt);
        Assert.Equal(0, await CallAsync(s => s.CountUnreadAsync(anna)));
    }

    [PostgresFact]
    public async Task PurgeExpired_DeletesWhatIsOlderThanNinetyDays_ReadOrNot_OfAnyUserAndOrg()
    {
        var (orgId, anna) = await SeedHostAsync("auth0|anna");
        var (otherOrgId, bruno) = await SeedHostAsync("auth0|bruno");
        var cutoff = Now.AddDays(-InAppNotificationLimits.RetentionDays);
        var expiredUnread = Row(anna, orgId, cutoff.AddSeconds(-1));
        var expiredRead = Row(bruno, otherOrgId, cutoff.AddDays(-30), readAt: cutoff.AddDays(-29));
        var exactlyAtTheLimit = Row(anna, orgId, cutoff);
        var recent = Row(bruno, otherOrgId, Now.AddDays(-89));
        var today = Row(anna, orgId, Now);
        await AddAsync(expiredUnread, expiredRead, exactlyAtTheLimit, recent, today);

        var deleted = await CallAsync(s => s.PurgeExpiredAsync());
        var deletedAgain = await CallAsync(s => s.PurgeExpiredAsync());

        Assert.Equal(2, deleted);
        Assert.Equal(0, deletedAgain);
        Assert.Equivalent(
            new[] { exactlyAtTheLimit.Id, recent.Id, today.Id },
            (await RowsAsync()).Select(r => r.Id));
    }

    // ─── Whose rows, on real SQL ───

    [PostgresFact]
    public async Task List_AHostUserOnARequestContext_ReadsItsOwnRowsOfItsOrgAndNoOneElses()
    {
        var (orgA, anna) = await SeedHostAsync("auth0|anna");
        var (orgB, bruno) = await SeedHostAsync("auth0|bruno");
        var mine = Row(anna, orgA, Now.AddMinutes(-2));
        await AddAsync(mine, Row(bruno, orgB, Now.AddMinutes(-1)), Row(anna, orgB, Now.AddMinutes(-3)));

        // The request's context: authenticated, the tenant is the host org of the caller.
        await using var asAnna = new AppDbContext(_database!.CreateOptions(), new FixedTenantContext(orgA, filterEnabled: true));
        var page = await Service(asAnna).ListAsync(anna, false, 1, 20);

        Assert.Equal([mine.Id], page.Items.Select(i => i.Id));
        Assert.Equal(1, page.TotalCount);
        Assert.Equal(1, await Service(asAnna).CountUnreadAsync(anna));
        // The tenant filter of the context agrees with the service: Anna's context shows her only the rows of org A, none of Bruno's.
        Assert.Equal(
            [mine.Id],
            await asAnna.InAppNotifications.AsNoTracking().Where(n => n.UserId == anna).Select(n => n.Id).ToListAsync());
        Assert.Empty(await asAnna.InAppNotifications.AsNoTracking().Where(n => n.UserId == bruno).ToListAsync());
    }

    [PostgresFact]
    public async Task List_ASupplierOnlyAccount_ReadsItsRowsEvenThoughTheTenantFilterMatchesNothing()
    {
        var supplierOrgId = await SeedSupplierOrgAsync(("auth0|sara", true, false));
        var row = Row("auth0|sara", supplierOrgId, Now.AddMinutes(-1));
        await AddAsync(row);

        // No host org: the tenant of its requests is null and every tenant-filtered table is empty for it.
        await using var asSara = new AppDbContext(_database!.CreateOptions(), new FixedTenantContext(null, filterEnabled: true));
        Assert.Empty(await asSara.InAppNotifications.ToListAsync());

        var page = await Service(asSara).ListAsync("auth0|sara", false, 1, 20);
        await Service(asSara).MarkReadAsync("auth0|sara", row.Id);

        Assert.Equal([row.Id], page.Items.Select(i => i.Id));
        Assert.Equal(0, await Service(asSara).CountUnreadAsync("auth0|sara"));
    }

    // ─── Erasure ───

    [PostgresFact]
    public async Task DeletingTheUser_TakesItsNotificationsWithIt_NoOneElses()
    {
        var (orgId, anna) = await SeedHostAsync("auth0|anna");
        var (otherOrgId, bruno) = await SeedHostAsync("auth0|bruno");
        await AddAsync(Row(anna, orgId, Now), Row(anna, orgId, Now.AddMinutes(-1)), Row(bruno, otherOrgId, Now));

        // An account is deleted straight from the database (nothing in the code does it yet): every key to Users cascades.
        Assert.Equal(1, await ExecuteAsync($"DELETE FROM \"Users\" WHERE \"Id\" = '{anna}'"));

        Assert.Empty(await RowsAsync(anna));
        Assert.Single(await RowsAsync(bruno));
    }

    [PostgresFact]
    public async Task DeletingTheOrg_TakesItsNotificationsWithIt_TheOnesOfOtherOrgsStay()
    {
        // An org with people cannot be deleted at all (its users and members reference it with a restricting key): the cascade is
        // proved on an org nobody belongs to, which still has rows of a user of another org (a supplier org, say).
        var (orgId, anna) = await SeedHostAsync("auth0|anna");
        Guid emptyOrg;
        await using (var db = NewDb())
        {
            emptyOrg = OrgTeamTestData.AddOrg(db, OrgType.Supplier).Id;
            await db.SaveChangesAsync();
        }

        await AddAsync(Row(anna, emptyOrg, Now), Row(anna, emptyOrg, Now.AddMinutes(-1)), Row(anna, orgId, Now));

        Assert.Equal(1, await ExecuteAsync($"DELETE FROM \"Orgs\" WHERE \"Id\" = '{emptyOrg}'"));

        var left = await RowsAsync(anna);
        Assert.Equal(orgId, Assert.Single(left).OrgId);
    }

    // ─── Helpers ───

    /// <summary>
    /// Holds the first save that adds an in-app notification of every run until <c>parties</c> saves have arrived, so that all
    /// the runs of the job read the same empty state before any of them writes. One instance for all the contexts of the test.
    /// </summary>
    private sealed class InsertRendezvous(int parties) : SaveChangesInterceptor
    {
        private readonly TaskCompletionSource _allArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrived;

        public int Arrived => Volatile.Read(ref _arrived);

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            var addsNotifications = eventData.Context?.ChangeTracker.Entries<InAppNotification>()
                .Any(entry => entry.State == EntityState.Added) == true;
            if (!addsNotifications)
                return result;

            // Only the first save of each run waits: the saves after a lost race (the rows still missing) go straight on.
            var arrival = Interlocked.Increment(ref _arrived);
            if (arrival <= parties)
            {
                if (arrival == parties)
                    _allArrived.TrySetResult();

                await _allArrived.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            }

            return result;
        }
    }

    private sealed class FixedTenantContext(Guid? orgId, bool filterEnabled) : ITenantContext
    {
        public Guid? OrgId { get; } = orgId;

        public bool FilterEnabled { get; } = filterEnabled;
    }
}
