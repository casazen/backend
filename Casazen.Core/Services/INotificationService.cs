namespace Casazen.Core.Services;

/// <summary>
/// Alerts sent by the recurring jobs (Alloggiati deadline, check-out reminder, CIN deadline). The booking emails (BK-10)
/// are sent by <c>BookingNotifier</c>, the refund emails by the refund service: no stub methods here.
/// </summary>
public interface INotificationService
{
    Task SendAlloggiatiDeadlineAlertAsync(Guid bookingId);
    Task SendCheckoutReminderAsync(Guid bookingId);
    Task SendCinDeadlineAlertAsync(string ownerId, IReadOnlyList<Guid> propertyIds, int daysUntilDeadline);
}
