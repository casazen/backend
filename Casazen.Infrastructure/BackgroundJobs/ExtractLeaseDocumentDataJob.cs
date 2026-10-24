using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.BackgroundJobs;

/// <summary>
/// Hangfire job that extracts data from lease contract documents using OCR (ADR-009).
/// Processes PDF documents to extract parties, rent, dates, and other contract details.
/// Runbook: docs/runbooks/lease-document-extraction.md
/// </summary>
public sealed class ExtractLeaseDocumentDataJob(
    AppDbContext db,
    ILeaseDocumentExtractionService extractionService,
    ILogger<ExtractLeaseDocumentDataJob> logger)
{
    public const int MaxAttempts = 3;

    /// <summary>
    /// Enqueue the extraction job for a document.
    /// </summary>
    public static string EnqueueAsync(Guid documentId)
    {
        return BackgroundJob.Enqueue<ExtractLeaseDocumentDataJob>(job => job.ExecuteAsync(documentId, CancellationToken.None));
    }

    /// <summary>
    /// Execute the extraction job. Verifies tenant isolation, extracts text from all pages,
    /// parses fields, validates coherence, and populates lease fields if confidence is high.
    /// </summary>
    [AutomaticRetry(Attempts = MaxAttempts, OnAttemptsExceeded = AttemptsExceededAction.Delete)]
    [DisableConcurrentExecution(timeoutInSeconds: 300)]
    public async Task ExecuteAsync(Guid documentId, CancellationToken ct)
    {
        logger.LogInformation("Starting extraction for document {DocumentId}", documentId);

        try
        {
            // Verify the document exists and belongs to a lease contract (tenant isolation check)
            var document = await db.LeaseDocuments
                .Include(d => d.LeaseContract)
                .AsNoTracking()
                .FirstOrDefaultAsync(d => d.Id == documentId, ct);

            if (document == null)
            {
                logger.LogWarning("Document {DocumentId} not found, skipping extraction", documentId);
                return;
            }

            // Verify tenant isolation: the document's OrgId must match the lease contract's OrgId
            if (document.OrgId != document.LeaseContract.OrgId)
            {
                logger.LogError(
                    "Tenant isolation violation: document {DocumentId} OrgId {DocumentOrgId} does not match lease contract OrgId {LeaseOrgId}",
                    documentId,
                    document.OrgId,
                    document.LeaseContract.OrgId);
                throw new DomainRuleException("tenant_isolation_violation", "TenantIsolationViolation");
            }

            // Perform the extraction
            await extractionService.ExtractAsync(documentId, ct);

            logger.LogInformation("Extraction completed successfully for document {DocumentId}", documentId);
        }
        catch (NotFoundException ex)
        {
            logger.LogWarning(ex, "Document {DocumentId} not found during extraction", documentId);
            // Don't retry - document is gone
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Extraction failed for document {DocumentId}", documentId);
            throw;
        }
    }
}
