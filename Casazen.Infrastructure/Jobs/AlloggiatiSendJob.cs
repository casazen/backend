using Casazen.Core.Entities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Jobs;

/// <summary>
/// Sends Alloggiati Web reports on arrival day (ADR-015). Calls Test first, proceeds with Send only if all rows valid.
/// Sets status to <see cref="AlloggiatiWebStatus.SentReceiptPending"/> on success.
/// No auto-retry on timeout/network error per ADR-015-R6.
/// </summary>
public class AlloggiatiSendJob(
    AppDbContext db,
    AlloggiatiWebClient client,
    ILogger<AlloggiatiSendJob> logger)
{
    /// <summary>
    /// Process reports ready to send for a booking. Called by arrival-day scheduler.
    /// </summary>
    public async Task ProcessBookingAsync(Guid bookingId, CancellationToken ct = default)
    {
        var reports = await db.AlloggiatiWebReports
            .Include(r => r.Booking)
                .ThenInclude(b => b.Property)
            .Include(r => r.Guest)
            .Where(r => r.BookingId == bookingId && r.Status == AlloggiatiWebStatus.DaInviare)
            .ToListAsync(ct);

        if (reports.Count == 0)
        {
            logger.LogDebug("No reports to send for booking {BookingId}", bookingId);
            return;
        }

        var property = reports[0].Booking.Property;
        var credential = await db.PropertyQuesturaCredentials
            .FirstOrDefaultAsync(c => c.PropertyId == property.Id, ct);

        if (credential == null)
        {
            logger.LogWarning(
                "No Questura credentials configured for property {PropertyId}, booking {BookingId}",
                property.Id,
                bookingId);

            foreach (var report in reports)
            {
                report.Status = AlloggiatiWebStatus.DaInviareManualmente;
                report.ErrorMessage = "questura_credentials_missing";
                report.UpdatedAt = DateTime.UtcNow;
            }

            await db.SaveChangesAsync(ct);
            return;
        }

        try
        {
            // Build rows from reports (168-char format for Single Structure, 174-char for GA)
            var rows = BuildAlloggiatiRows(reports);

            // Step 1: Test all rows (ADR-015-R6)
            var testResult = await client.TestAsync(credential.Id, rows, ct);

            if (!testResult.Success)
            {
                logger.LogWarning(
                    "Alloggiati Test failed for booking {BookingId} with {ErrorCount} errors",
                    bookingId,
                    testResult.Errors.Count);

                foreach (var report in reports)
                {
                    report.Status = AlloggiatiWebStatus.Errore;
                    report.ErrorMessage = BuildErrorMessage(testResult.Errors);
                    report.RetryCount++;
                    report.UpdatedAt = DateTime.UtcNow;
                }

                await db.SaveChangesAsync(ct);
                return;
            }

            // Step 2: Send only if all rows valid
            var sendResult = await client.SendAsync(credential.Id, rows, ct);

            if (sendResult.Success)
            {
                logger.LogInformation(
                    "Alloggiati Send successful for booking {BookingId}, confirmation={Confirmation}",
                    bookingId,
                    sendResult.ConfirmationNumber);

                foreach (var report in reports)
                {
                    report.Status = AlloggiatiWebStatus.SentReceiptPending;
                    report.ConfirmationNumber = sendResult.ConfirmationNumber;
                    report.ErrorMessage = null;
                    report.UpdatedAt = DateTime.UtcNow;
                }

                await db.SaveChangesAsync(ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // No auto-retry on network/timeout per ADR-015-R6
            logger.LogError(
                ex,
                "Alloggiati Send failed for booking {BookingId} (no auto-retry)",
                bookingId);

            foreach (var report in reports)
            {
                report.Status = AlloggiatiWebStatus.Errore;
                report.ErrorMessage = "alloggiati_send_failed";
                report.RetryCount++;
                report.UpdatedAt = DateTime.UtcNow;
            }

            await db.SaveChangesAsync(ct);
        }
    }

    /// <summary>
    /// Build 168-char rows for Single Structure profile (GA profile would be 174 chars with IdAppartamento).
    /// Real implementation would use <see cref="Core.Regulatory.AlloggiatiRecordFile"/> for proper formatting.
    /// </summary>
    private List<string> BuildAlloggiatiRows(List<AlloggiatiWebReport> reports)
    {
        var rows = new List<string>();

        foreach (var report in reports)
        {
            // Placeholder: real implementation uses AlloggiatiRecordFile.BuildRow
            // 168-char format: tipo(1) + cognome(50) + nome(30) + sesso(1) + data_nascita(10) + ...
            var row = $"1{report.Guest.LastName?.PadRight(50)[..50] ?? new string(' ', 50)}" +
                     $"{report.Guest.FirstName?.PadRight(30)[..30] ?? new string(' ', 30)}" +
                     new string(' ', 87); // Fill to 168 chars

            rows.Add(row);
        }

        return rows;
    }

    /// <summary>
    /// Build stable error message from Test errors (no guest PII per privacy rule).
    /// </summary>
    private string BuildErrorMessage(List<AlloggiatiRowError> errors)
    {
        var firstError = errors.FirstOrDefault();
        if (firstError == null)
            return "alloggiati_test_failed";

        // Include only error codes, never guest names or document numbers
        return $"alloggiati_test_error_row_{firstError.RowNumber}_code_{firstError.ErrorCode}";
    }
}
