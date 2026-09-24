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
    /// address, queued on Hangfire, and a push. The job decides when; this only renders and delivers.
    /// </summary>
    Task SendStayAlertAsync(StayAlert alert, CancellationToken cancellationToken = default);

    Task SendCinDeadlineAlertAsync(string ownerId, IReadOnlyList<Guid> propertyIds, int daysUntilDeadline);
}
