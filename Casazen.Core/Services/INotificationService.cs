namespace Casazen.Core.Services;

public interface INotificationService
{
    Task SendBookingConfirmationAsync(Guid bookingId);
    Task SendPaymentReceiptAsync(Guid paymentId);
    Task SendPropertyUpdateAsync(Guid propertyId);
    Task SendOtaSyncNotificationAsync(Guid propertyId, string platform);
    Task SendRefundNotificationAsync(Guid paymentId);
    Task SendAlloggiatiDeadlineAlertAsync(Guid bookingId);
}