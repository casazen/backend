using Casazen.Core.Entities;
using Casazen.Core.Exceptions;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Integration.Postgres;
using Casazen.Tests.Unit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// FD-21 (A8-01, A8-07): the platform AI budget is checked before the call and counted with single SQL updates on
/// PostgreSQL, so a reservation over the cap is refused and concurrent callers cannot overspend.
/// </summary>
public class PlatformAiBudgetGuardPostgresTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 24, 10, 0, 0, TimeSpan.Zero);

    [PostgresFact]
    public async Task ReserveAsync_WithinCap_CountsReservationThenSettledTokens()
    {
        await using var database = await CreateMigratedDatabaseAsync();
        await SeedBudgetAsync(database, cap: 10_000, used: 1_000, lastReset: Now.UtcDateTime.AddDays(-3));

        await using (var db = database.CreateContext())
        {
            var guard = CreateGuard(db);
            var reservation = await guard.ReserveAsync(2_500);
            Assert.Equal(3_500, await ReadUsedAsync(database));

            await guard.SettleAsync(reservation, 700);
        }

        Assert.Equal(1_700, await ReadUsedAsync(database));
    }

    [PostgresFact]
    public async Task ReserveAsync_OverCap_ThrowsAndLeavesCounterUnchanged()
    {
        await using var database = await CreateMigratedDatabaseAsync();
        await SeedBudgetAsync(database, cap: 10_000, used: 9_000, lastReset: Now.UtcDateTime.AddDays(-3));

        await using var db = database.CreateContext();
        await Assert.ThrowsAsync<AiBudgetExceededException>(() => CreateGuard(db).ReserveAsync(1_001));

        Assert.Equal(9_000, await ReadUsedAsync(database));
    }

    [PostgresFact]
    public async Task ReserveAsync_FirstCallOfNewMonth_ResetsCounter()
    {
        await using var database = await CreateMigratedDatabaseAsync();
        await SeedBudgetAsync(database, cap: 10_000, used: 10_000, lastReset: new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc));

        await using var db = database.CreateContext();
        await CreateGuard(db).ReserveAsync(100);

        await using var check = database.CreateContext();
        var budget = await check.PlatformAiBudgets.SingleAsync();
        Assert.Equal(100, budget.TokensUsedThisMonth);
        Assert.Equal(new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc), budget.LastResetAt);
    }

    [PostgresFact]
    public async Task ReserveAsync_NoBudgetRow_CreatesDefaultCapRow()
    {
        await using var database = await CreateMigratedDatabaseAsync();

        await using var db = database.CreateContext();
        await CreateGuard(db).ReserveAsync(50);

        await using var check = database.CreateContext();
        var budget = await check.PlatformAiBudgets.SingleAsync();
        Assert.Equal(new PlatformAiBudget().MonthlyTokenCap, budget.MonthlyTokenCap);
        Assert.Equal(50, budget.TokensUsedThisMonth);
    }

    [PostgresFact]
    public async Task ReserveAsync_ConcurrentCallers_NeverExceedCap()
    {
        await using var database = await CreateMigratedDatabaseAsync();
        await SeedBudgetAsync(database, cap: 1_000, used: 0, lastReset: Now.UtcDateTime.AddDays(-1));

        var attempts = Enumerable.Range(0, 20).Select(async _ =>
        {
            await using var db = database.CreateContext();
            try
            {
                await CreateGuard(db).ReserveAsync(100);
                return true;
            }
            catch (AiBudgetExceededException)
            {
                return false;
            }
        });
        var outcomes = await Task.WhenAll(attempts);

        Assert.Equal(10, outcomes.Count(ok => ok));
        Assert.Equal(1_000, await ReadUsedAsync(database));
    }

    private static PlatformAiBudgetGuard CreateGuard(Casazen.Infrastructure.Data.AppDbContext db) =>
        new(db, new FixedTimeProvider(Now), NullLogger<PlatformAiBudgetGuard>.Instance);

    private static async Task<PostgresTestDatabase> CreateMigratedDatabaseAsync()
    {
        var database = await PostgresTestDatabase.CreateAsync("aibudget");
        database.MigrateToLatest();
        return database;
    }

    private static async Task SeedBudgetAsync(PostgresTestDatabase database, long cap, long used, DateTime lastReset)
    {
        await using var db = database.CreateContext();
        db.PlatformAiBudgets.Add(new PlatformAiBudget
        {
            MonthlyTokenCap = cap,
            TokensUsedThisMonth = used,
            LastResetAt = lastReset,
        });
        await db.SaveChangesAsync();
    }

    private static async Task<long> ReadUsedAsync(PostgresTestDatabase database)
    {
        await using var db = database.CreateContext();
        return await db.PlatformAiBudgets.Select(b => b.TokensUsedThisMonth).SingleAsync();
    }
}
