using Casazen.Core.Entities;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
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

    public async Task ProcessReceiptsAsync(CancellationToken ct = default)
    {
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

    private async Task ProcessCredentialReceiptsAsync(Guid credentialId, CancellationToken ct)
    {
        try
        {
            var toDate = timeProvider.TodayInRome();
            var fromDate = toDate.AddDays(-ReceiptLookbackDays);

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

    private async Task ProcessReceiptAsync(
        Guid credentialId,
        AlloggiatiReceipt receipt,
        CancellationToken ct)
    {
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

        foreach (var report in reports)
        {
            report.Status = AlloggiatiWebStatus.Inviato;
            report.ReportedAt = DateTime.UtcNow;
            report.UpdatedAt = DateTime.UtcNow;
        }

        await db.SaveChangesAsync(ct);
    }

    private async Task CheckMissingReceiptsAsync(Guid credentialId, CancellationToken ct)
    {
        var thresholdDate = timeProvider.TodayInRome().AddDays(-ReceiptAlertThresholdDays);

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
