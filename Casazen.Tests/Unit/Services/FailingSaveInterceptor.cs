using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// Makes the saves of a context fail while <see cref="Failure"/> is set, to see what a flow does when the database refuses its
/// change (a concurrent change, an error): the flows that store files or send messages must undo or skip them. Add it to the
/// options of the context; set <see cref="Failure"/> only for the call under test, so the test's own seeding still saves.
/// </summary>
internal sealed class FailingSaveInterceptor : SaveChangesInterceptor
{
    /// <summary>The exception thrown by the next saves; <c>null</c> lets them through.</summary>
    public Exception? Failure { get; set; }

    /// <summary>Saves refused so far.</summary>
    public int Refused { get; private set; }

    /// <summary>Called after each save that went through (for example to cancel the caller's token right after a commit).</summary>
    public Action? AfterSave { get; set; }

    public override ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData,
        int result,
        CancellationToken cancellationToken = default)
    {
        AfterSave?.Invoke();
        return base.SavedChangesAsync(eventData, result, cancellationToken);
    }

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        AfterSave?.Invoke();
        return base.SavedChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        if (Failure is { } failure)
        {
            Refused++;
            throw failure;
        }

        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        if (Failure is { } failure)
        {
            Refused++;
            throw failure;
        }

        return base.SavingChanges(eventData, result);
    }
}
