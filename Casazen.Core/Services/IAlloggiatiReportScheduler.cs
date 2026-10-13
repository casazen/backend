namespace Casazen.Core.Services;

/// <summary>
/// Schedules the Alloggiati Web job of a booking at the start of its arrival day in Europe/Rome (Hangfire
/// <c>Schedule</c>), or right away when the day has already started. Idempotent per booking and guest: the guest
/// portal and the host check-in can both call it, the job is queued once (CO-11, A5-03).
/// </summary>
public interface IAlloggiatiReportScheduler
{
    Task EnsureScheduledAsync(Guid bookingId);
}
