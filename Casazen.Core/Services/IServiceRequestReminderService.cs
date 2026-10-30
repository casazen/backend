namespace Casazen.Core.Services;

/// <summary>
/// The reminders of the day before the work (SP-10, SP-16d): at 18:00 (Europe/Rome) the day before, an e-mail to the customer who booked a
/// supplier from its public showcase (SP-10, <c>ServiceRequest.ReminderSentAt</c>) and, for every request the supplier took
/// (<c>PresoInCarico</c>) in any context — a host's short or long stay, or the showcase —, an e-mail and a push to the <b>supplier</b>
/// (SP-16d, <c>ServiceRequest.SupplierReminderSentAt</c>), once each. Run every hour by the <c>service-request-reminders</c> job, with the
/// feature flag off too (a booking that exists keeps its reminder). Only true things are said: the time of the work, the supplier, how to
/// manage the booking; no promise the platform does not keep (decision D24). The reminder to the supplier names the service, the time and the
/// comune and links to the request; the address and the contact stay in the console (decision D9).
/// </summary>
/// <remarks>
/// <b>Idempotent, one run at a time.</b> The run marks the request (<c>ReminderSentAt</c>, <c>SupplierReminderSentAt</c>) in the same save
/// that precedes the queueing, so a retried or overlapping run never sends the same reminder twice; a PostgreSQL session lock
/// (<c>PostgresAdvisoryLocks.Scope.ServiceRequestRemindersRun</c>) on top of Hangfire's own makes a second run do nothing. A
/// request taken after 18:00 of the day before gets no reminder: the e-mail that told the customer it was accepted said it, and the supplier
/// took it a moment ago. The supplier's reminder follows its switch «Promemoria del giorno prima» (<c>SupplierSettings.NotifyDayBeforeReminder</c>)
/// and is not sent to a supplier that is suspended.
/// </remarks>
public interface IServiceRequestReminderService
{
    /// <summary>One run. Never throws for a request it cannot handle: it counts it.</summary>
    Task<ServiceRequestReminderRun> SendDueAsync(CancellationToken cancellationToken = default);
}

/// <summary>Outcome of one run of <see cref="IServiceRequestReminderService"/>.</summary>
/// <param name="Skipped">Another run holds the lock: nothing was read or changed.</param>
/// <param name="Sent">Reminders to customers queued by this run.</param>
/// <param name="Failed">Requests whose reminder to the customer could not be queued: retried by the next run while the reminder is still due.</param>
public sealed record ServiceRequestReminderRun(bool Skipped, int Sent, int Failed)
{
    /// <summary>Another run is in progress.</summary>
    public static ServiceRequestReminderRun LockTaken { get; } = new(Skipped: true, 0, 0);

    /// <summary>Reminders to suppliers queued by this run (SP-16d).</summary>
    public int SupplierSent { get; init; }

    /// <summary>Requests whose reminder to the supplier could not be queued.</summary>
    public int SupplierFailed { get; init; }
}
