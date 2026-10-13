using Casazen.Core.Entities;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// <see cref="IAiBudgetGuard"/> on the single <see cref="PlatformAiBudget"/> row. Reservation and settlement are
/// single SQL <c>UPDATE</c> statements (the cap is checked in the <c>WHERE</c>), so concurrent requests and replicas
/// cannot overspend by reading the same counter.
/// </summary>
public sealed class PlatformAiBudgetGuard(
    AppDbContext db,
    TimeProvider timeProvider,
    ILogger<PlatformAiBudgetGuard> logger) : IAiBudgetGuard
{
    public async Task<AiBudgetReservation> ReserveAsync(long estimatedTokens, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(estimatedTokens);

        var budgetId = await GetCurrentMonthBudgetIdAsync(cancellationToken);
        var now = timeProvider.GetUtcNow().UtcDateTime;

        var reserved = await db.PlatformAiBudgets
            .Where(b => b.Id == budgetId && b.TokensUsedThisMonth + estimatedTokens <= b.MonthlyTokenCap)
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(b => b.TokensUsedThisMonth, b => b.TokensUsedThisMonth + estimatedTokens)
                    .SetProperty(b => b.UpdatedAt, now),
                cancellationToken);

        if (reserved == 0)
        {
            logger.LogWarning(
                "Platform AI budget exhausted: a call needing up to {EstimatedTokens} tokens was not sent to the provider",
                estimatedTokens);
            throw new AiBudgetExceededException();
        }

        return new AiBudgetReservation(budgetId, estimatedTokens);
    }

    public async Task SettleAsync(AiBudgetReservation reservation, long usedTokens, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reservation);
        var delta = Math.Max(0, usedTokens) - reservation.ReservedTokens;
        if (delta == 0)
            return;

        var now = timeProvider.GetUtcNow().UtcDateTime;
        await db.PlatformAiBudgets
            .Where(b => b.Id == reservation.BudgetId)
            .ExecuteUpdateAsync(
                set => set
                    // Never below zero: a month reset between reservation and settlement already cleared the counter.
                    .SetProperty(b => b.TokensUsedThisMonth, b => Math.Max(0L, b.TokensUsedThisMonth + delta))
                    .SetProperty(b => b.UpdatedAt, now),
                cancellationToken);
    }

    /// <summary>The budget row (created on first use), reset to zero once the first time it is used in a new UTC month.</summary>
    private async Task<Guid> GetCurrentMonthBudgetIdAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var monthStart = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);

        var budgetId = await db.PlatformAiBudgets
            .AsNoTracking()
            .OrderBy(b => b.Id)
            .Select(b => (Guid?)b.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (budgetId is null)
        {
            var budget = new PlatformAiBudget { LastResetAt = monthStart, UpdatedAt = now };
            db.PlatformAiBudgets.Add(budget);
            await db.SaveChangesAsync(cancellationToken);
            db.Entry(budget).State = EntityState.Detached;
            return budget.Id;
        }

        // Only the first call of the month matches the WHERE: the reset happens once, even across replicas.
        await db.PlatformAiBudgets
            .Where(b => b.Id == budgetId && b.LastResetAt < monthStart)
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(b => b.TokensUsedThisMonth, 0L)
                    .SetProperty(b => b.LastResetAt, monthStart)
                    .SetProperty(b => b.UpdatedAt, now),
                cancellationToken);

        return budgetId.Value;
    }
}
