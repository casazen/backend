using Casazen.Infrastructure.External;

namespace Casazen.Web.BackgroundJobs;

/// <summary>
/// Background job for processing queued email notifications
/// Handles transactional emails (booking confirmations, payment receipts, etc.)
/// </summary>
public class EmailQueueProcessor
{
    private readonly ISendGridService _sendGridService;
    private readonly ILogger<EmailQueueProcessor> _logger;

    public EmailQueueProcessor(ISendGridService sendGridService, ILogger<EmailQueueProcessor> logger)
    {
        _sendGridService = sendGridService;
        _logger = logger;
    }

    /// <summary>
    /// Sends a single email
    /// </summary>
    /// <param name="to">Recipient email address</param>
    /// <param name="subject">Email subject</param>
    /// <param name="htmlContent">Email HTML content</param>
    public async Task SendEmailAsync(string to, string subject, string htmlContent)
    {
        try
        {
            _logger.LogInformation("Processing email to {To} with subject '{Subject}'", to, subject);

            var success = await _sendGridService.SendEmailAsync(to, subject, htmlContent);

            if (success)
            {
                _logger.LogInformation("Email sent successfully to {To}", to);
            }
            else
            {
                _logger.LogWarning("Email send failed for {To}", to);
                throw new Exception($"Failed to send email to {to}");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing email to {To}", to);
            throw; // Re-throw to let Hangfire handle retry logic
        }
    }

    /// <summary>
    /// Sends a booking confirmation email
    /// </summary>
    /// <param name="to">Guest email</param>
    /// <param name="bookingId">Booking ID</param>
    /// <param name="propertyName">Property name</param>
    /// <param name="checkIn">Check-in date</param>
    /// <param name="checkOut">Check-out date</param>
    public async Task SendBookingConfirmationAsync(string to, Guid bookingId, string propertyName, DateTime checkIn, DateTime checkOut)
    {
        var subject = $"Booking Confirmation - {propertyName}";
        var htmlContent = $@"
            <h1>Booking Confirmed</h1>
            <p>Thank you for your booking!</p>
            <ul>
                <li><strong>Booking ID:</strong> {bookingId}</li>
                <li><strong>Property:</strong> {propertyName}</li>
                <li><strong>Check-in:</strong> {checkIn:yyyy-MM-dd}</li>
                <li><strong>Check-out:</strong> {checkOut:yyyy-MM-dd}</li>
            </ul>
            <p>We look forward to hosting you!</p>
        ";

        await SendEmailAsync(to, subject, htmlContent);
    }

    /// <summary>
    /// Sends a payment receipt email
    /// </summary>
    /// <param name="to">Guest email</param>
    /// <param name="paymentId">Payment ID</param>
    /// <param name="amount">Payment amount</param>
    public async Task SendPaymentReceiptAsync(string to, Guid paymentId, decimal amount)
    {
        var subject = "Payment Receipt - CASAZEN";
        var htmlContent = $@"
            <h1>Payment Receipt</h1>
            <p>Your payment has been processed successfully.</p>
            <ul>
                <li><strong>Payment ID:</strong> {paymentId}</li>
                <li><strong>Amount:</strong> €{amount:F2}</li>
                <li><strong>Date:</strong> {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC</li>
            </ul>
            <p>Thank you for your payment!</p>
        ";

        await SendEmailAsync(to, subject, htmlContent);
    }
}
