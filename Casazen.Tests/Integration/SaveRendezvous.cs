using Casazen.Core.Entities;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace Casazen.Tests.Integration;

/// <summary>
/// Holds every save that modifies a service request until <c>parties</c> saves have arrived, so that all of them read the same
/// state before any of them writes: the race of two operations on one request, made deterministic. The operations are then free
/// to run: whoever the database lets in first wins and the others must lose cleanly.
/// </summary>
/// <remarks>
/// <para><b>One instance for the whole host.</b> The options of <see cref="AppDbContext"/> are built again for every scope, one per
/// request, and the lambda of <c>ConfigureDbContext</c> runs every time. An interceptor created inside that lambda is a new object
/// for each request: every request then waits alone for a second party that is in another object, and when the time is up both
/// fail with a <see cref="TimeoutException"/> that the API answers with 500. This is what the five parallel-transition tests of
/// SP-04 did the first time they ran in CI (30 s and 500/500 instead of 200/409). <see cref="HoldParallelSaves"/> creates the
/// instance first and registers that one, so the mistake cannot be made.</para>
/// <para>It holds the asynchronous saves only (the services of the API never save synchronously).</para>
/// </remarks>
internal sealed class SaveRendezvous : SaveChangesInterceptor
{
    /// <summary>Long enough for a cold test host to serve both requests; a rendezvous that is never completed fails after it.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    private readonly TaskCompletionSource _allArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly int _parties;
    private readonly TimeSpan _timeout;
    private int _arrived;

    public SaveRendezvous(int parties, TimeSpan? timeout = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(parties, 2);
        _parties = parties;
        _timeout = timeout ?? DefaultTimeout;
    }

    /// <summary>The saves of a service request that reached the rendezvous so far.</summary>
    public int Arrived => Volatile.Read(ref _arrived);

    /// <summary>
    /// Puts <b>one</b> rendezvous of <paramref name="parties"/> saves on every <see cref="AppDbContext"/> of the host being built
    /// (call it from <c>ConfigureTestServices</c>, which runs once) and returns it.
    /// </summary>
    public static SaveRendezvous HoldParallelSaves(IServiceCollection services, int parties = 2, TimeSpan? timeout = null)
    {
        var rendezvous = new SaveRendezvous(parties, timeout);
        services.ConfigureDbContext<AppDbContext>(options => options.AddInterceptors(rendezvous));
        return rendezvous;
    }

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        var changesARequest = eventData.Context?.ChangeTracker.Entries<ServiceRequest>()
            .Any(entry => entry.State == EntityState.Modified) == true;
        if (!changesARequest)
            return result;

        if (Interlocked.Increment(ref _arrived) >= _parties)
            _allArrived.TrySetResult();

        try
        {
            await _allArrived.Task.WaitAsync(_timeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException(
                $"Only {Arrived} of {_parties} saves reached the rendezvous in {_timeout.TotalSeconds:0.##} s. If the requests ran in "
                + "parallel, they do not share it: register ONE instance for the host (SaveRendezvous.HoldParallelSaves), never one "
                + "built inside the ConfigureDbContext lambda, which creates a new object for every request.");
        }

        return result;
    }
}
