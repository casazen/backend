namespace Casazen.Core.Services;

/// <summary>
/// Alerts sent by the recurring jobs (stay alerts: Alloggiati stages and check-out reminder, CO-10; CIN deadline). The
/// booking emails (BK-10) are sent by <c>BookingNotifier</c>, the refund emails by the refund service: no stub methods
/// here.
/// </summary>
public interface INotificationService
{
    /// <summary>
    /// Delivers a stay alert of the <c>stay-alerts</c> job (CO-10) to the property's hosts: an email to the org's contact
    /// address and a push to their devices, both queued on Hangfire (the push once per stage and device, MO-04). The job
    /// decides when; this only renders and queues.
    /// </summary>
    Task SendStayAlertAsync(StayAlert alert, CancellationToken cancellationToken = default);

    /// <summary>
    /// Delivers a CIN alert of the <c>cin-deadline-alert</c> job (CO-20) to an org: one email to its contact address,
    /// queued on Hangfire, listing its properties without a valid CIN. The job decides when; this only renders and
    /// delivers. False when the email was not queued (no contact address, provider not configured), with a log entry.
    /// </summary>
    Task<bool> SendCinDeadlineAlertAsync(CinDeadlineAlert alert, CancellationToken cancellationToken = default);
}
