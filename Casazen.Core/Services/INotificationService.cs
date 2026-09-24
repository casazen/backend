namespace Casazen.Core.Services;

public interface INotificationService
{
    Task SendBookingConfirmationAsync(Guid bookingId);
    Task SendPaymentReceiptAsync(Guid paymentId);
    Task SendPropertyUpdateAsync(Guid propertyId);
    Task SendOtaSyncNotificationAsync(Guid propertyId, string platform);
    Task SendRefundNotificationAsync(Guid paymentId);
    /// <summary>
    /// Delivers a stay alert of the <c>stay-alerts</c> job (CO-10) to the property's hosts: an email to the org's contact
    /// address, queued on Hangfire, and a push. The job decides when; this only renders and delivers.
    /// </summary>
    Task SendStayAlertAsync(StayAlert alert, CancellationToken cancellationToken = default);
    Task SendCinDeadlineAlertAsync(string ownerId, IReadOnlyList<Guid> propertyIds, int daysUntilDeadline);
}