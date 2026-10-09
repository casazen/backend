using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Suppliers;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Tests.Integration.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// SP-03 on PostgreSQL, where it counts: the supplier's agenda is keyed by the supplier org and not tenant-filtered, so
/// isolation between two suppliers, the table checks, the unique index of the working hours, the cascade from the profile, the
/// one row of settings per supplier and the advisory lock <c>SupplierCalendarSync</c> (every write waits for it; parallel
/// writes never lose a limit, never create two rows and never mix two weeks) are proved on the real database. They are
/// skipped locally when no PostgreSQL is available and always run on CI (<see cref="PostgresFactAttribute"/>).
/// </summary>
public class SupplierAgendaPostgresTests(CasazenWebApplicationFactory factory) : IClassFixture<CasazenWebApplicationFactory>
{
    private const string Base = "/api/supplier/availability";

    private static DateOnly Today => TimeProvider.System.TodayInRomeAsDateOnly();

    // ─── Isolation between suppliers ─────────────────────────────────────────────

    [PostgresFact]
    public async Task TwoSuppliers_NeverSeeOrChangeEachOthersAgenda_AndEachRowBelongsToItsOwnOrg()
    {
        var (aUser, aOrg) = await SupplierCatalogTestData.SeedSupplierAsync(factory);
        var (bUser, bOrg) = await SupplierCatalogTestData.SeedSupplierAsync(factory);
        using var a = SupplierClient(aUser);
        using var b = SupplierClient(bUser);
        var start = RomeCalendar.ToUtc(Today.AddDays(4), new TimeOnly(10, 0));

        await a.PutAsJsonAsync($"{Base}/hours", DemoWeek());
        await a.PutAsJsonAsync($"{Base}/rules", Rules(maxJobsPerDay: 7));
        var aTimeOff = await ReadAsync(await a.PostAsJsonAsync($"{Base}/time-off", new { fromDate = Today.AddDays(3), toDate = Today.AddDays(4) }));
        var aBlock = await ReadAsync(await a.PostAsJsonAsync($"{Base}/blocks", new { kind = "Block", startUtc = start, endUtc = start.AddHours(1) }));
        await b.PutAsJsonAsync($"{Base}/hours", new { days = new[] { Day("Sunday", (600, 720)) } });
        await b.PostAsJsonAsync($"{Base}/time-off", new { fromDate = Today.AddDays(8), toDate = Today.AddDays(9) });

        // Each reads only its own.
        var bHours = await ReadAsync(await b.GetAsync($"{Base}/hours"));
        Assert.Equal(0, bHours.GetProperty("days")[0].GetProperty("bands").GetArrayLength());
        Assert.Equal(1, bHours.GetProperty("days")[6].GetProperty("bands").GetArrayLength());
        Assert.Equal(3, (await ReadAsync(await b.GetAsync($"{Base}/rules"))).GetProperty("maxJobsPerDay").GetInt32());
        Assert.Equal(1, (await ReadAsync(await b.GetAsync($"{Base}/time-off"))).GetProperty("total").GetInt32());
        Assert.Equal(0, (await ReadAsync(await b.GetAsync($"{Base}/blocks"))).GetProperty("total").GetInt32());

        // B cannot reach A's time off or block on any endpoint: 404, never 403 (it does not exist for B), and nothing changes.
        Assert.Equal(HttpStatusCode.NotFound, (await b.DeleteAsync($"{Base}/time-off/{aTimeOff.GetProperty("id").GetGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.DeleteAsync($"{Base}/blocks/{aBlock.GetProperty("id").GetGuid()}")).StatusCode);
        Assert.Equal(1, (await ReadAsync(await a.GetAsync($"{Base}/time-off"))).GetProperty("total").GetInt32());
        Assert.Equal(1, (await ReadAsync(await a.GetAsync($"{Base}/blocks"))).GetProperty("total").GetInt32());

        // The rows are tied to the right org.
        Assert.Equal((11, 1, 1, 1), await CountsAsync(aOrg));
        Assert.Equal((1, 1, 0, 1), await CountsAsync(bOrg));
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(aOrg, (await db.SupplierTimeOff.AsNoTracking().Where(t => t.OrgId == aOrg).SingleAsync()).OrgId);
        Assert.Equal(7, (await db.SupplierSettings.AsNoTracking().Where(s => s.OrgId == aOrg).SingleAsync()).MaxJobsPerDay);
        Assert.Equal(3, (await db.SupplierSettings.AsNoTracking().Where(s => s.OrgId == bOrg).SingleAsync()).MaxJobsPerDay);
    }

    [PostgresFact]
    public async Task ADualRoleAccount_UsesItsSupplierOrgForTheAgenda_NotItsHostOrg()
    {
        // A host that is also a supplier: User.OrgId is the host org, User.SupplierOrgId the supplier org. The tenant filter
        // follows the first, the agenda the second.
        var ownerId = $"auth0|sp03-dual-{Guid.NewGuid():N}";
        var hostOrg = await factory.SeedOrgForOwnerAsync(ownerId);
        Guid supplierOrgId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var email = $"sp03.dual.{Guid.NewGuid():N}@example.com";
            var supplierOrg = new OrgEntity
            {
                Name = "Fornitore doppio ruolo",
                Slug = $"supplier-{Guid.NewGuid():N}"[..30],
                DisplayName = "Fornitore doppio ruolo",
                ContactEmail = email,
                OrgType = OrgType.Supplier,
                PlanTier = PlanTier.Starter,
                IsActive = true,
            };
            db.Orgs.Add(supplierOrg);
            db.SupplierProfiles.Add(new SupplierProfile { OrgId = supplierOrg.Id, Email = email, LegalName = "Doppio Srl", Phone = "+39 06 030303" });
            var user = await db.Users.SingleAsync(u => u.Id == ownerId);
            user.SupplierOrgId = supplierOrg.Id;
            await db.SaveChangesAsync();
            supplierOrgId = supplierOrg.Id;
        }

        using var client = factory.CreateAuthenticatedClient(ownerId, roles: "Supplier,PropertyOwner");
        var saved = await client.PutAsJsonAsync($"{Base}/rules", Rules(maxJobsPerDay: 6));

        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal((0, 0, 0, 1), await CountsAsync(supplierOrgId));
        Assert.Equal((0, 0, 0, 0), await CountsAsync(hostOrg.Id));
    }

    // ─── The checks, the unique index and the cascade ────────────────────────────

    [PostgresFact]
    public async Task Checks_TheDatabaseRefusesRowsTheRulesNeverWrite()
    {
        var (_, orgId) = await SupplierCatalogTestData.SeedSupplierAsync(factory);
        var at = new DateTime(2026, 11, 10, 9, 0, 0, DateTimeKind.Utc);

        var refused = new (string Name, Action<AppDbContext> Add)[]
        {
            ("weekday 7", db => db.SupplierWorkingHours.Add(Hours(orgId, weekday: (DayOfWeek)7))),
            ("negative weekday", db => db.SupplierWorkingHours.Add(Hours(orgId, weekday: (DayOfWeek)(-1)))),
            ("band that ends where it starts", db => db.SupplierWorkingHours.Add(Hours(orgId, start: 600, end: 600))),
            ("band that ends before it starts", db => db.SupplierWorkingHours.Add(Hours(orgId, start: 600, end: 540))),
            ("negative start", db => db.SupplierWorkingHours.Add(Hours(orgId, start: -1, end: 60))),
            ("end after midnight", db => db.SupplierWorkingHours.Add(Hours(orgId, start: 0, end: 1441))),
            ("time off that ends before it starts", db => db.SupplierTimeOff.Add(new SupplierTimeOff { OrgId = orgId, FromDate = new DateOnly(2026, 11, 5), ToDate = new DateOnly(2026, 11, 4) })),
            ("window with no length", db => db.SupplierBusyWindows.Add(new SupplierBusyWindow { OrgId = orgId, StartUtc = at, EndUtc = at })),
            ("window that ends before it starts", db => db.SupplierBusyWindows.Add(new SupplierBusyWindow { OrgId = orgId, StartUtc = at, EndUtc = at.AddHours(-1) })),
            ("buffer above the limit", db => db.SupplierSettings.Add(new SupplierSettings { OrgId = orgId, BufferMinutes = 241 })),
            ("negative buffer", db => db.SupplierSettings.Add(new SupplierSettings { OrgId = orgId, BufferMinutes = -1 })),
            ("no jobs a day", db => db.SupplierSettings.Add(new SupplierSettings { OrgId = orgId, MaxJobsPerDay = 0 })),
            ("too many jobs a day", db => db.SupplierSettings.Add(new SupplierSettings { OrgId = orgId, MaxJobsPerDay = 51 })),
            ("negative notice", db => db.SupplierSettings.Add(new SupplierSettings { OrgId = orgId, MinNoticeHours = -1 })),
            ("notice above the limit", db => db.SupplierSettings.Add(new SupplierSettings { OrgId = orgId, MinNoticeHours = 721 })),
            ("no horizon", db => db.SupplierSettings.Add(new SupplierSettings { OrgId = orgId, HorizonDays = 0 })),
            ("horizon above the limit", db => db.SupplierSettings.Add(new SupplierSettings { OrgId = orgId, HorizonDays = 366 })),
            ("slot step below the limit", db => db.SupplierSettings.Add(new SupplierSettings { OrgId = orgId, SlotStepMinutes = 14 })),
            ("slot step above the limit", db => db.SupplierSettings.Add(new SupplierSettings { OrgId = orgId, SlotStepMinutes = 241 })),
            ("no parallel jobs", db => db.SupplierSettings.Add(new SupplierSettings { OrgId = orgId, ParallelJobs = 0 })),
            ("too many parallel jobs", db => db.SupplierSettings.Add(new SupplierSettings { OrgId = orgId, ParallelJobs = 11 })),
            ("no time to respond", db => db.SupplierSettings.Add(new SupplierSettings { OrgId = orgId, RespondWithinMinutes = 0 })),
        };

        foreach (var (name, add) in refused)
        {
            await using var scope = factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            add(db);

            var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            Assert.True(
                ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.CheckViolation },
                $"{name}: expected a check violation, got {ex.InnerException?.Message}");
        }

        // The accepted bounds are accepted.
        await using var okScope = factory.Services.CreateAsyncScope();
        var ok = okScope.ServiceProvider.GetRequiredService<AppDbContext>();
        ok.SupplierWorkingHours.Add(Hours(orgId, start: 0, end: 1440));
        ok.SupplierWorkingHours.Add(Hours(orgId, weekday: DayOfWeek.Sunday, start: 1439, end: 1440));
        ok.SupplierTimeOff.Add(new SupplierTimeOff { OrgId = orgId, FromDate = new DateOnly(2026, 11, 5), ToDate = new DateOnly(2026, 11, 5) });
        ok.SupplierBusyWindows.Add(new SupplierBusyWindow { OrgId = orgId, StartUtc = at, EndUtc = at.AddMinutes(1) });
        ok.SupplierSettings.Add(new SupplierSettings
        {
            OrgId = orgId,
            BufferMinutes = 0,
            MaxJobsPerDay = 50,
            MinNoticeHours = 720,
            HorizonDays = 365,
            SlotStepMinutes = 15,
            ParallelJobs = 10,
            RespondWithinMinutes = 15,
        });
        await ok.SaveChangesAsync();
    }

    [PostgresFact]
    public async Task WorkingHoursIndex_UniquePerSupplierWeekdayAndStart()
    {
        var (_, orgA) = await SupplierCatalogTestData.SeedSupplierAsync(factory);
        var (_, orgB) = await SupplierCatalogTestData.SeedSupplierAsync(factory);
        await SaveAsync(db => db.SupplierWorkingHours.Add(Hours(orgA)));

        // The same band twice for one supplier: the unique index refuses it.
        var duplicate = await Assert.ThrowsAsync<DbUpdateException>(() => SaveAsync(db => db.SupplierWorkingHours.Add(Hours(orgA, end: 700))));
        var postgres = Assert.IsType<PostgresException>(duplicate.InnerException);
        Assert.Equal(PostgresErrorCodes.UniqueViolation, postgres.SqlState);
        Assert.Equal("UIX_SupplierWorkingHours_OrgId_Weekday_StartMinute", postgres.ConstraintName);

        // Another supplier, another weekday and another start are all free.
        await SaveAsync(db => db.SupplierWorkingHours.Add(Hours(orgB)));
        await SaveAsync(db => db.SupplierWorkingHours.Add(Hours(orgA, weekday: DayOfWeek.Tuesday)));
        await SaveAsync(db => db.SupplierWorkingHours.Add(Hours(orgA, start: 840, end: 1080)));
        Assert.Equal(3, (await CountsAsync(orgA)).Hours);
    }

    [PostgresFact]
    public async Task SettingsKey_OneRowPerSupplier()
    {
        var (_, orgId) = await SupplierCatalogTestData.SeedSupplierAsync(factory);
        await SaveAsync(db => db.SupplierSettings.Add(new SupplierSettings { OrgId = orgId }));

        var second = await Assert.ThrowsAsync<DbUpdateException>(
            () => SaveAsync(db => db.SupplierSettings.Add(new SupplierSettings { OrgId = orgId })));

        Assert.Equal(PostgresErrorCodes.UniqueViolation, Assert.IsType<PostgresException>(second.InnerException).SqlState);
    }

    [PostgresFact]
    public async Task TheTables_AreChildrenOfTheSupplierProfileInCascade_AndTheDatabaseKnowsTheirIndexes()
    {
        var (_, orgId) = await SupplierCatalogTestData.SeedSupplierAsync(factory);
        await SaveAsync(db =>
        {
            db.SupplierWorkingHours.Add(Hours(orgId));
            db.SupplierTimeOff.Add(new SupplierTimeOff { OrgId = orgId, FromDate = Today, ToDate = Today });
            db.SupplierBusyWindows.Add(new SupplierBusyWindow { OrgId = orgId, StartUtc = DateTime.UtcNow.AddDays(2), EndUtc = DateTime.UtcNow.AddDays(2).AddHours(1) });
            db.SupplierSettings.Add(new SupplierSettings { OrgId = orgId });
        });
        Assert.Equal((1, 1, 1, 1), await CountsAsync(orgId));

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var indexes = await db.Database
            .SqlQuery<string>($"SELECT indexname AS \"Value\" FROM pg_indexes WHERE tablename IN ('SupplierWorkingHours', 'SupplierTimeOff', 'SupplierBusyWindows', 'SupplierSettings')")
            .ToListAsync();
        Assert.Contains("UIX_SupplierWorkingHours_OrgId_Weekday_StartMinute", indexes);
        Assert.Contains("IX_SupplierTimeOff_OrgId_FromDate", indexes);
        Assert.Contains("IX_SupplierBusyWindows_OrgId_StartUtc", indexes);
        Assert.Contains("PK_SupplierSettings", indexes);

        // Deleting the supplier profile takes the whole agenda with it.
        await db.SupplierProfiles.Where(sp => sp.OrgId == orgId).ExecuteDeleteAsync();
        Assert.Equal((0, 0, 0, 0), await CountsAsync(orgId));
    }

    [PostgresFact]
    public async Task NoRead_EverCreatesTheSettingsRow()
    {
        var (userId, orgId) = await SupplierCatalogTestData.SeedSupplierAsync(factory);
        using var client = SupplierClient(userId);

        foreach (var url in new[] { $"{Base}/hours", $"{Base}/rules", $"{Base}/time-off", $"{Base}/blocks", "/api/supplier/calendar" })
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(url)).StatusCode);

        Assert.Equal((0, 0, 0, 0), await CountsAsync(orgId));
    }

    // ─── Every write waits for the SupplierCalendarSync lock ─────────────────────

    [PostgresTheory]
    [InlineData("hours")]
    [InlineData("rules")]
    [InlineData("add-time-off")]
    [InlineData("delete-time-off")]
    [InlineData("add-block")]
    [InlineData("delete-block")]
    [InlineData("day-override")]
    public async Task EveryWriteOfTheAgenda_WaitsForTheCalendarSyncLockOfItsSupplier(string write)
    {
        var (userId, orgId) = await SupplierCatalogTestData.SeedSupplierAsync(factory);
        using var client = SupplierClient(userId);
        var start = RomeCalendar.ToUtc(Today.AddDays(4), new TimeOnly(10, 0));
        var timeOffId = (await ReadAsync(await client.PostAsJsonAsync($"{Base}/time-off", new { fromDate = Today.AddDays(20), toDate = Today.AddDays(21) }))).GetProperty("id").GetGuid();
        var blockId = (await ReadAsync(await client.PostAsJsonAsync($"{Base}/blocks", new { kind = "Block", startUtc = start, endUtc = start.AddHours(1) }))).GetProperty("id").GetGuid();

        Task<HttpResponseMessage> Write() => write switch
        {
            "hours" => client.PutAsJsonAsync($"{Base}/hours", DemoWeek()),
            "rules" => client.PutAsJsonAsync($"{Base}/rules", Rules(maxJobsPerDay: 5)),
            "add-time-off" => client.PostAsJsonAsync($"{Base}/time-off", new { fromDate = Today.AddDays(30), toDate = Today.AddDays(31) }),
            "delete-time-off" => client.DeleteAsync($"{Base}/time-off/{timeOffId}"),
            "add-block" => client.PostAsJsonAsync($"{Base}/blocks", new { kind = "Block", startUtc = start.AddHours(3), endUtc = start.AddHours(4) }),
            "delete-block" => client.DeleteAsync($"{Base}/blocks/{blockId}"),
            _ => client.PutAsJsonAsync("/api/supplier/availability", new { dates = new[] { new { date = Today.AddDays(6).ToString("yyyy-MM-dd"), available = false } } }),
        };

        // Another connection holds the lock of this supplier, as an iCal sync that is writing its days would.
        await using var holder = new NpgsqlConnection(await ConnectionStringAsync());
        await holder.OpenAsync();
        await using var holderTransaction = await holder.BeginTransactionAsync();
        await using (var lockCommand = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@scope, @key)", holder, holderTransaction))
        {
            lockCommand.Parameters.AddWithValue("scope", (int)PostgresAdvisoryLocks.Scope.SupplierCalendarSync);
            lockCommand.Parameters.AddWithValue("key", PostgresAdvisoryLocks.Hash(orgId.ToString("N")));
            await lockCommand.ExecuteNonQueryAsync();
        }

        var pending = Task.Run(Write);
        await WaitForAdvisoryLockWaitersAsync(1);
        await Task.Delay(300);
        Assert.False(pending.IsCompleted, $"{write} did not wait for the lock of the supplier's calendar");

        await holderTransaction.CommitAsync();
        var response = await pending.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(response.IsSuccessStatusCode, $"{write}: {response.StatusCode}");
    }

    [PostgresFact]
    public async Task TheLockOfOneSupplier_NeverHoldsBackAnother()
    {
        var (_, aOrg) = await SupplierCatalogTestData.SeedSupplierAsync(factory);
        var (bUser, _) = await SupplierCatalogTestData.SeedSupplierAsync(factory);
        using var b = SupplierClient(bUser);

        await using var holder = new NpgsqlConnection(await ConnectionStringAsync());
        await holder.OpenAsync();
        await using var holderTransaction = await holder.BeginTransactionAsync();
        await using (var lockCommand = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@scope, @key)", holder, holderTransaction))
        {
            lockCommand.Parameters.AddWithValue("scope", (int)PostgresAdvisoryLocks.Scope.SupplierCalendarSync);
            lockCommand.Parameters.AddWithValue("key", PostgresAdvisoryLocks.Hash(aOrg.ToString("N")));
            await lockCommand.ExecuteNonQueryAsync();
        }

        var response = await b.PutAsJsonAsync($"{Base}/rules", Rules(maxJobsPerDay: 4)).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ─── Parallel writes ─────────────────────────────────────────────────────────

    [PostgresFact]
    public async Task ParallelPutHours_OneWeekWins_TheStoredBandsAreNeverAMixOfTwo()
    {
        var (userId, orgId) = await SupplierCatalogTestData.SeedSupplierAsync(factory);

        const int attempts = 8;
        var responses = await Task.WhenAll(Enumerable.Range(0, attempts).Select(async i =>
        {
            using var client = SupplierClient(userId);
            // Week i: Monday 08:00+i*10 minutes to 13:00, and Friday with i+1 bands... only the Monday band differs by start.
            return await client.PutAsJsonAsync($"{Base}/hours", new { days = new[] { Day("Monday", (480 + (i * 10), 780)), Day("Tuesday", (480 + (i * 10), 780)) } });
        }));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var rows = await db.SupplierWorkingHours.AsNoTracking().Where(h => h.OrgId == orgId).ToListAsync();
        // Exactly the bands of one request: one Monday and one Tuesday band, starting at the same minute (the same week).
        Assert.Equal(2, rows.Count);
        Assert.Single(rows, row => row.Weekday == DayOfWeek.Monday);
        Assert.Single(rows, row => row.Weekday == DayOfWeek.Tuesday);
        Assert.Single(rows.Select(row => row.StartMinute).Distinct());
    }

    [PostgresFact]
    public async Task ParallelPutHoursOfTheSameWeek_NeverHitTheUniqueIndex()
    {
        var (userId, orgId) = await SupplierCatalogTestData.SeedSupplierAsync(factory);

        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            using var client = SupplierClient(userId);
            return await client.PutAsJsonAsync($"{Base}/hours", DemoWeek());
        }));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        Assert.Equal((11, 0, 0, 1), await CountsAsync(orgId));
    }

    [PostgresFact]
    public async Task ParallelFirstWrites_OfTheRulesAndTheHours_CreateTheSettingsRowOnce()
    {
        var (userId, orgId) = await SupplierCatalogTestData.SeedSupplierAsync(factory);

        var responses = await Task.WhenAll(
            Enumerable.Range(1, 6).Select(async i =>
            {
                using var client = SupplierClient(userId);
                return await client.PutAsJsonAsync($"{Base}/rules", Rules(maxJobsPerDay: i));
            }).Concat(Enumerable.Range(0, 4).Select(async _ =>
            {
                using var client = SupplierClient(userId);
                return await client.PutAsJsonAsync($"{Base}/hours", DemoWeek());
            })));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        var counts = await CountsAsync(orgId);
        Assert.Equal(1, counts.Settings);
        Assert.Equal(11, counts.Hours);
        // Both writes kept what the other wrote: the hours are marked as configured and a rule from the requests is saved.
        await using var scope = factory.Services.CreateAsyncScope();
        var settings = await scope.ServiceProvider.GetRequiredService<AppDbContext>()
            .SupplierSettings.AsNoTracking().Where(s => s.OrgId == orgId).SingleAsync();
        Assert.NotNull(settings.HoursConfiguredAt);
        Assert.InRange(settings.MaxJobsPerDay, 1, 6);
    }

    [PostgresFact]
    public async Task ParallelTimeOffAtTheLimit_ExactlyOneTakesTheLastPlace()
    {
        var (userId, orgId) = await SupplierCatalogTestData.SeedSupplierAsync(factory);
        await SaveAsync(db =>
        {
            for (var i = 0; i < SupplierAgendaLimits.MaxTimeOffEntries - 1; i++)
                db.SupplierTimeOff.Add(new SupplierTimeOff { OrgId = orgId, FromDate = Today.AddDays(10 + i), ToDate = Today.AddDays(10 + i) });
        });

        const int attempts = 6;
        var responses = await Task.WhenAll(Enumerable.Range(0, attempts).Select(async i =>
        {
            using var client = SupplierClient(userId);
            return await client.PostAsJsonAsync($"{Base}/time-off", new { fromDate = Today.AddDays(300 + i), toDate = Today.AddDays(300 + i) });
        }));

        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Created);
        var refused = responses.Where(r => r.StatusCode != HttpStatusCode.Created).ToList();
        Assert.Equal(attempts - 1, refused.Count);
        foreach (var response in refused)
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            Assert.Equal("supplier_time_off_limit_reached", (await ReadAsync(response)).GetProperty("code").GetString());
        }

        Assert.Equal(SupplierAgendaLimits.MaxTimeOffEntries, (await CountsAsync(orgId)).TimeOff);
    }

    [PostgresFact]
    public async Task ParallelBlocksAtTheLimit_ExactlyOneTakesTheLastPlace()
    {
        var (userId, orgId) = await SupplierCatalogTestData.SeedSupplierAsync(factory);
        var first = RomeCalendar.ToUtc(Today.AddDays(10), new TimeOnly(8, 0));
        await SaveAsync(db =>
        {
            for (var i = 0; i < SupplierAgendaLimits.MaxManualWindows - 1; i++)
            {
                db.SupplierBusyWindows.Add(new SupplierBusyWindow
                {
                    OrgId = orgId,
                    StartUtc = first.AddDays(i),
                    EndUtc = first.AddDays(i).AddHours(1),
                    Kind = SupplierBusyWindowKind.Block,
                });
            }
        });

        const int attempts = 6;
        var responses = await Task.WhenAll(Enumerable.Range(0, attempts).Select(async i =>
        {
            using var client = SupplierClient(userId);
            var start = RomeCalendar.ToUtc(Today.AddDays(400), new TimeOnly(8, 0)).AddHours(i * 2);
            return await client.PostAsJsonAsync($"{Base}/blocks", new { kind = "Block", startUtc = start, endUtc = start.AddHours(1) });
        }));

        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Created);
        foreach (var response in responses.Where(r => r.StatusCode != HttpStatusCode.Created))
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            Assert.Equal("supplier_block_limit_reached", (await ReadAsync(response)).GetProperty("code").GetString());
        }

        Assert.Equal(SupplierAgendaLimits.MaxManualWindows, (await CountsAsync(orgId)).Windows);
    }

    // ─── helpers ─────────────────────────────────────────────────────────────────

    private HttpClient SupplierClient(string userId) => factory.CreateAuthenticatedClient(userId, roles: "Supplier");

    private async Task SaveAsync(Action<AppDbContext> change)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        change(db);
        await db.SaveChangesAsync();
    }

    private async Task<(int Hours, int TimeOff, int Windows, int Settings)> CountsAsync(Guid orgId) =>
        await SupplierAgendaTestData.CountsAsync(factory, orgId);

    private async Task<string> ConnectionStringAsync()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.GetConnectionString()!;
    }

    private async Task WaitForAdvisoryLockWaitersAsync(int count)
    {
        await using var connection = new NpgsqlConnection(await ConnectionStringAsync());
        await connection.OpenAsync();
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            await using var command = new NpgsqlCommand(
                "SELECT count(*) FROM pg_locks l JOIN pg_database d ON d.oid = l.database " +
                "WHERE l.locktype = 'advisory' AND NOT l.granted AND d.datname = current_database()",
                connection);
            if ((long)(await command.ExecuteScalarAsync())! >= count)
                return;

            await Task.Delay(25);
        }

        throw new TimeoutException($"Fewer than {count} sessions waited on the lock of the supplier's calendar.");
    }

    private static SupplierWorkingHours Hours(Guid orgId, DayOfWeek weekday = DayOfWeek.Monday, int start = 480, int end = 780) =>
        new() { OrgId = orgId, Weekday = weekday, StartMinute = start, EndMinute = end };

    private static object Rules(
        int bufferMinutes = 30,
        int maxJobsPerDay = 3,
        int minNoticeHours = 24,
        int horizonDays = 35,
        int slotStepMinutes = 60) =>
        new { bufferMinutes, maxJobsPerDay, minNoticeHours, horizonDays, slotStepMinutes };

    private static object Day(string weekday, params (int Start, int End)[] bands) =>
        new { weekday, bands = bands.Select(b => new { startMinute = b.Start, endMinute = b.End }).ToArray() };

    /// <summary>Monday to Friday 08:00-13:00 and 14:00-18:00, Saturday 08:00-14:00 (11 bands).</summary>
    private static object DemoWeek() => new
    {
        days = new[]
        {
            Day("Monday", (480, 780), (840, 1080)),
            Day("Tuesday", (480, 780), (840, 1080)),
            Day("Wednesday", (480, 780), (840, 1080)),
            Day("Thursday", (480, 780), (840, 1080)),
            Day("Friday", (480, 780), (840, 1080)),
            Day("Saturday", (480, 840)),
        },
    };

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>();
}
