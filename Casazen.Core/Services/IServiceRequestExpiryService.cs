namespace Casazen.Core.Services;

/// <summary>
/// The upkeep of the bookings from the suppliers' public showcases (SP-10): what lapses is cleared away. Run every 5 minutes by the
/// <c>service-request-expiry</c> job, <b>always, with the feature flag off too</b>: a booking made while the flag was on has to
/// lapse even if the flag is turned off afterwards.
/// <list type="number">
/// <item>The holds past their expiry are deleted: the slot they took is free again (the planner already ignored them) and the
/// customer's data they carried are gone.</item>
/// <item>The showcase requests nobody answered in time (<c>ResponseDueAt</c> passed while still <c>Richiesto</c>) move to
/// <c>Annullato</c> with the reason <c>NoResponse</c> and the customer and the supplier are told. It is the same cancellation as
/// <see cref="IServiceRequestAutoCancelService"/> makes for the requests of the hosts (decision D8), run for the showcase
/// ones: not behind <c>SupplierRequestAutoCancel</c>, and not skipped when the supplier proposed another time (the customer
/// has the time of <c>Suppliers:Showcase:ProposalResponseMinutes</c> to answer).</item>
/// </list>
/// </summary>
/// <remarks>
/// <b>Idempotent, one run at a time.</b> A hold is deleted once, a request cancelled once (the change is saved only if nobody
/// touched the request since it was read, <c>xmin</c>), a repeated or retried run finds nothing; a PostgreSQL session lock
/// (<c>PostgresAdvisoryLocks.Scope.ServiceRequestExpiryRun</c>) on top of Hangfire's own makes a second run do nothing.
/// </remarks>
public interface IServiceRequestExpiryService
{
    /// <summary>One run. Never throws for a hold or a request it cannot handle: it counts it, and the next run goes on.</summary>
    Task<ServiceRequestExpiryRun> RunAsync(CancellationToken cancellationToken = default);
}

/// <summary>Outcome of one run of <see cref="IServiceRequestExpiryService"/>.</summary>
/// <param name="Skipped">Another run holds the lock: nothing was read or changed.</param>
/// <param name="HoldsDeleted">Expired holds deleted by this run.</param>
/// <param name="RequestsCancelled">Showcase requests cancelled because nobody answered in time.</param>
/// <param name="Conflicts">Requests that changed under the run (taken, rejected or cancelled by someone else): left as they are.</param>
/// <param name="Failed">Requests that could not be cancelled because of an error: retried by the next run.</param>
public sealed record ServiceRequestExpiryRun(bool Skipped, int HoldsDeleted, int RequestsCancelled, int Conflicts, int Failed)
{
    /// <summary>Another run is in progress.</summary>
    public static ServiceRequestExpiryRun LockTaken { get; } = new(Skipped: true, 0, 0, 0, 0);
}
