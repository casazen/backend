using Casazen.Core.Entities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Jobs;

/// <summary>
/// Daily job to retrieve Alloggiati Web receipts (ADR-015-R7). Calls Ricevuta(date) per credential,
/// moves reports to <see cref="AlloggiatiWebStatus.Inviato"/> with ConfirmationNumber,
/// and triggers document deletion when receipt is received. Alerts if receipt missing beyond next day.
/// </summary>
public class AlloggiatiReceiptJob(
    AppDbContext db,
    AlloggiatiWebClient client,
    ILogger<AlloggiatiReceiptJob> logger)
{
    private const int ReceiptLookbackDays = 30;
    private const int ReceiptAlertThresholdDays = 1;

    /// <summary>
    /// Process receipt retrieval for all credentials with pending reports.
    /// Called daily by Hangfire.
    /// </summary>
    public async Task ProcessReceiptsAsync(CancellationToken ct = default)
    {
        // Find all credentials with reports in SentReceiptPending status
        var propertyIds = await db.AlloggiatiWebReports
            .Include(r => r.Booking)
            .Where(r => r.Status == AlloggiatiWebStatus.SentReceiptPending)
            .Select(r => r.Booking.PropertyId)
            .Distinct()
            .ToListAsync(ct);

        var credentialIds = await db.PropertyQuesturaCredentials
            .Where(c => propertyIds.Contains(c.PropertyId))
            .Select(c => c.Id)
            .Distinct()
            .ToListAsync(ct);

        logger.LogInformation(
            "Processing receipt retrieval for {CredentialCount} credentials",
            credentialIds.Count);

        foreach (var credentialId in credentialIds)
        {
            await ProcessCredentialReceiptsAsync(credentialId, ct);
        }
    }

    /// <summary>
    /// Retrieve receipts for one credential and update matching reports.
    /// </summary>
    private async Task ProcessCredentialReceiptsAsync(Guid credentialId, CancellationToken ct)
    {
        try
        {
            var toDate = DateTime.UtcNow.Date;
            var fromDate = toDate.AddDays(-ReceiptLookbackDays);

            // Retrieve receipts from Alloggiati Web for last 30 days
            var receipts = await client.RicevutaAsync(credentialId, fromDate, toDate, ct);

            if (receipts.Count == 0)
            {
                logger.LogDebug("No receipts found for credential {CredentialId}", credentialId);
                await CheckMissingReceiptsAsync(credentialId, ct);
                return;
            }

            logger.LogInformation(
                "Retrieved {ReceiptCount} receipts for credential {CredentialId}",
                receipts.Count,
                credentialId);

            // Match receipts to pending reports and update their status
            foreach (var receipt in receipts)
            {
                await ProcessReceiptAsync(credentialId, receipt, ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(
                ex,
                "Failed to retrieve receipts for credential {CredentialId}",
                credentialId);
        }
    }

    /// <summary>
    /// Process one receipt: update reports to Inviato and trigger document deletion.
    /// </summary>
    private async Task ProcessReceiptAsync(
        Guid credentialId,
        AlloggiatiReceipt receipt,
        CancellationToken ct)
    {
        // Find reports with matching confirmation number and SentReceiptPending status
        var reports = await db.AlloggiatiWebReports
            .Include(r => r.Booking)
            .Where(r => r.Status == AlloggiatiWebStatus.SentReceiptPending
                && r.ConfirmationNumber == receipt.ConfirmationNumber)
            .ToListAsync(ct);

        if (reports.Count == 0)
        {
            logger.LogDebug(
                "No pending reports found for confirmation {Confirmation}",
                receipt.ConfirmationNumber);
            return;
        }

        logger.LogInformation(
            "Processing receipt {Confirmation} for {ReportCount} reports",
            receipt.ConfirmationNumber,
            reports.Count);

        // Update reports to Inviato status
        foreach (var report in reports)
        {
            report.Status = AlloggiatiWebStatus.Inviato;
            report.ReportedAt = DateTime.UtcNow;
            report.UpdatedAt = DateTime.UtcNow;
        }

        await db.SaveChangesAsync(ct);

        // Trigger document deletion for each booking (ADR-015-R7, ADR-013 integration)
        foreach (var report in reports)
        {
            await DeleteDocumentImagesAsync(report.BookingId, ct);
        }
    }

    /// <summary>
    /// Delete document images for a booking after receipt received (ADR-013 integration).
    /// Sets DeletedAt timestamp; images remain in storage but are marked as deleted.
    /// </summary>
    private async Task DeleteDocumentImagesAsync(Guid bookingId, CancellationToken ct)
    {
        var images = await db.DocumentImages
            .Where(i => i.BookingId == bookingId && i.DeletedAt == null)
            .ToListAsync(ct);

        if (images.Count == 0)
        {
            logger.LogDebug("No document images to delete for booking {BookingId}", bookingId);
            return;
        }

        logger.LogInformation(
            "Deleting {ImageCount} document images for booking {BookingId} (receipt received)",
            images.Count,
            bookingId);

        foreach (var image in images)
        {
            image.DeletedAt = DateTime.UtcNow;
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Check for receipts missing beyond threshold and alert (ADR-015-R7).
    /// </summary>
    private async Task CheckMissingReceiptsAsync(Guid credentialId, CancellationToken ct)
    {
        var thresholdDate = DateTime.UtcNow.Date.AddDays(-ReceiptAlertThresholdDays);

        // Find properties that use this credential
        var propertyIds = await db.PropertyQuesturaCredentials
            .Where(c => c.Id == credentialId)
            .Select(c => c.PropertyId)
            .ToListAsync(ct);

        var missingReports = await db.AlloggiatiWebReports
            .Include(r => r.Booking)
            .Where(r => r.Status == AlloggiatiWebStatus.SentReceiptPending
                && r.UpdatedAt < thresholdDate
                && propertyIds.Contains(r.Booking.PropertyId))
            .ToListAsync(ct);

        if (missingReports.Count > 0)
        {
            logger.LogWarning(
                "Found {Count} reports with missing receipts beyond {Days} days for credential {CredentialId}",
                missingReports.Count,
                ReceiptAlertThresholdDays,
                credentialId);

            // Alert mechanism: log for now, could trigger notification/email in future
            foreach (var report in missingReports)
            {
                logger.LogWarning(
                    "Receipt missing for booking {BookingId}, report {ReportId}, sent {SentAt}",
                    report.BookingId,
                    report.Id,
                    report.UpdatedAt);
            }
        }
    }
}
