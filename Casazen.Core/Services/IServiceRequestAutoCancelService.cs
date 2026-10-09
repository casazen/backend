namespace Casazen.Core.Services;

/// <summary>
/// The automatic cancellation of the service requests nobody answered (SP-04, decision D8): the new requests (<c>Richiesto</c>)
/// whose <c>ResponseDueAt</c> has passed move to <c>Annullato</c> with the reason <c>NoResponse</c> (by CasaZen), and the host and
/// the supplier are told. Run every 10 minutes by the <c>service-request-auto-cancel</c> job, <b>only while the feature flag
/// <c>SupplierRequestAutoCancel</c> is on</b> (it is off by default: it changes what happens to the requests that exist).
/// </summary>
/// <remarks>
/// <para><b>Idempotent.</b> A request is cancelled at most once: it leaves the set the run reads (it is no longer <c>Richiesto</c>)
/// and the change is saved only if nobody touched the request since it was read (<c>xmin</c>): a supplier that takes it in the
/// same moment wins, and the run skips it. A run repeated or retried finds nothing left to do.</para>
/// <para><b>One run at a time.</b> A PostgreSQL session lock
/// (<c>PostgresAdvisoryLocks.Scope.ServiceRequestAutoCancelRun</c>) on top of Hangfire's own; a run that finds it taken does nothing.</para>
/// <para>A request with a time proposed by the supplier is not cancelled: the supplier did answer, the host has to.</para>
/// <para><b>The requests of the hosts only.</b> Since SP-10 this run leaves alone the requests born from a supplier's public
/// showcase (<c>ServiceRequestRentalContext.Showcase</c>): they have a customer, not a host, and lapse in the same way through
/// <see cref="CancelUnansweredShowcaseAsync"/>, which the always-on <c>service-request-expiry</c> job runs.</para>
/// </remarks>
public interface IServiceRequestAutoCancelService
{
    /// <summary>One run: the due requests are cancelled and their parties told. Never throws for a request it cannot handle: it counts it.</summary>
    Task<ServiceRequestAutoCancelRun> CancelUnansweredAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// The same cancellation for the requests of the public showcase (SP-10): the new ones past <c>ResponseDueAt</c> move to
    /// <c>Annullato</c> (reason <c>NoResponse</c>, by CasaZen), and the <b>customer</b> and the supplier are told. <b>Not behind</b>
    /// <c>SupplierRequestAutoCancel</c> (the showcase booking has its own flag and the requests that exist must lapse whatever the
    /// flags are), and a pending proposal of another time does not stop it: the proposal re-armed the deadline when it was made.
    /// Called with the lock of the expiry run already held by the caller, which is why it does not take one itself.
    /// </summary>
    Task<ServiceRequestAutoCancelRun> CancelUnansweredShowcaseAsync(CancellationToken cancellationToken = default);
}

/// <summary>Outcome of one run of <see cref="IServiceRequestAutoCancelService"/>.</summary>
/// <param name="Disabled">The feature flag is off: nothing was read or changed.</param>
/// <param name="Skipped">Another run holds the lock: nothing was read or changed.</param>
/// <param name="Cancelled">Requests cancelled by this run.</param>
/// <param name="Conflicts">Requests that changed under the run (taken, rejected or cancelled by someone else): left as they are.</param>
/// <param name="Failed">Requests that could not be cancelled because of an error: retried by the next run.</param>
public sealed record ServiceRequestAutoCancelRun(bool Disabled, bool Skipped, int Cancelled, int Conflicts, int Failed)
{
    /// <summary>The flag is off.</summary>
    public static ServiceRequestAutoCancelRun FlagOff { get; } = new(Disabled: true, Skipped: false, 0, 0, 0);

    /// <summary>Another run is in progress.</summary>
    public static ServiceRequestAutoCancelRun LockTaken { get; } = new(Disabled: false, Skipped: true, 0, 0, 0);

    /// <summary>Nothing was due.</summary>
    public static ServiceRequestAutoCancelRun Empty { get; } = new(Disabled: false, Skipped: false, 0, 0, 0);
}
