using Casazen.Core.Entities;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
// The receipt the SOAP client reads, not the stored daily receipt (the entity Casazen.Core.Entities.AlloggiatiReceipt, AW-R05).
using AlloggiatiReceipt = Casazen.Infrastructure.Services.AlloggiatiReceipt;

namespace Casazen.Infrastructure.Jobs;

/// <summary>
/// Daily job to retrieve Alloggiati Web receipts (ADR-015-R7). Calls Ricevuta(date) per credential,
/// moves reports to <see cref="AlloggiatiWebStatus.Inviato"/> with ConfirmationNumber. Alerts if receipt missing beyond next day.
/// It deletes no document: the documents of the guests of a stay go once an archived receipt covers the stay, in one place only,
/// <see cref="Casazen.Core.Services.IGuestDocumentPurgeService"/> (E20/F02/US01/T02, behind <c>Features:PurgeDocumentsAfterReceipt</c>).
/// </summary>
public class AlloggiatiReceiptJob(
    AppDbContext db,
    AlloggiatiWebClient client,
    ILogger<AlloggiatiReceiptJob> logger,
    TimeProvider timeProvider)
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
            var toDate = timeProvider.TodayInRome();
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
    /// Process one receipt: update reports to Inviato.
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

        // The documents of these stays are not deleted here (ADR-015-R7, ADR-013-R7): the one place that deletes them after a receipt is
        // IGuestDocumentPurgeService (E20/F02/US01/T02), which asks for an archived receipt that covers the stay and for its flag.
    }

    /// <summary>
    /// Check for receipts missing beyond threshold and alert (ADR-015-R7).
    /// </summary>
    private async Task CheckMissingReceiptsAsync(Guid credentialId, CancellationToken ct)
    {
        var thresholdDate = timeProvider.TodayInRome().AddDays(-ReceiptAlertThresholdDays);

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
