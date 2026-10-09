namespace Casazen.Core.Services;

/// <summary>
/// The reminder of the day before to the customers who booked a supplier from its public showcase (SP-10): an e-mail at 18:00
/// (Europe/Rome) the day before the work, to the customer of a request the supplier took (<c>PresoInCarico</c>), once
/// (<c>ServiceRequest.ReminderSentAt</c>). Run every hour by the <c>service-request-reminders</c> job, with the feature flag
/// off too (a booking that exists keeps its reminder). Only true things are said: the time of the work, the supplier, how to
/// manage the booking; no promise the platform does not keep (decision D24).
/// </summary>
/// <remarks>
/// <b>Idempotent, one run at a time.</b> The run marks the request (<c>ReminderSentAt</c>) in the same save that precedes the
/// queueing, so a retried or overlapping run never sends the same reminder twice; a PostgreSQL session lock
/// (<c>PostgresAdvisoryLocks.Scope.ServiceRequestRemindersRun</c>) on top of Hangfire's own makes a second run do nothing. A
/// request taken after 18:00 of the day before gets no reminder: the e-mail that told the customer it was accepted said it.
/// </remarks>
public interface IServiceRequestReminderService
{
    /// <summary>One run. Never throws for a request it cannot handle: it counts it.</summary>
    Task<ServiceRequestReminderRun> SendDueAsync(CancellationToken cancellationToken = default);
}

/// <summary>Outcome of one run of <see cref="IServiceRequestReminderService"/>.</summary>
/// <param name="Skipped">Another run holds the lock: nothing was read or changed.</param>
/// <param name="Sent">Reminders queued by this run.</param>
/// <param name="Failed">Requests whose reminder could not be queued: retried by the next run while the reminder is still due.</param>
public sealed record ServiceRequestReminderRun(bool Skipped, int Sent, int Failed)
{
    /// <summary>Another run is in progress.</summary>
    public static ServiceRequestReminderRun LockTaken { get; } = new(Skipped: true, 0, 0);
}
