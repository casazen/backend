using Casazen.Core.Services;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

public class NotificationService(ILogger<NotificationService> logger) : INotificationService
{
    public async Task SendBookingConfirmationAsync(Guid bookingId)
    {
        logger.LogInformation("Sending booking confirmation for {BookingId}", bookingId);
        await Task.Delay(100); // Simulate email send
    }

    public async Task SendPaymentReceiptAsync(Guid paymentId)
    {
        logger.LogInformation("Sending payment receipt for {PaymentId}", paymentId);
        await Task.Delay(100); // Simulate email send
    }

    public async Task SendPropertyUpdateAsync(Guid propertyId)
    {
        logger.LogInformation("Sending property update notification for {PropertyId}", propertyId);
        await Task.Delay(100); // Simulate email send
    }

    public async Task SendOtaSyncNotificationAsync(Guid propertyId, string platform)
    {
        logger.LogInformation("Sending OTA sync notification for {PropertyId} on {Platform}", propertyId, platform);
        await Task.Delay(100); // Simulate email send
    }

    public async Task SendRefundNotificationAsync(Guid paymentId)
    {
        logger.LogInformation("Sending refund notification for {PaymentId}", paymentId);
        await Task.Delay(100); // Simulate email send
    }
}