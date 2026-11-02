using System.Globalization;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Leases;
using Casazen.Core.Services;
using Casazen.Infrastructure.BackgroundJobs;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <summary>Where the lease of the guided service stands after <see cref="IConcordatoFacileCompletionService.SyncAsync"/> (CF-DONE).</summary>
public static class CfCompletionStates
{
    /// <summary>Not a lease of the guided service: nothing was done.</summary>
    public const string NotApplicable = "NotApplicable";

    /// <summary>No contract document is uploaded yet: the person uploads the one the service delivered.</summary>
    public const string WaitingForContract = "WaitingForContract";

    /// <summary>The contract document is being read (also when it was put back to be read into the draft).</summary>
    public const string ReadingContract = "ReadingContract";

    /// <summary>The practice does not say yet that the contract is signed: the lease waits for it.</summary>
    public const string WaitingForSignature = "WaitingForSignature";

    /// <summary>
    /// The contract cannot be imported by itself: what the import needs is not all there (data, the day of the signature), or the status of
    /// the practice cannot be read. The person completes the import, asked only for what is missing.
    /// </summary>
    public const string ContractNeedsData = "ContractNeedsData";

    /// <summary>The contract is imported (Signed); the receipt of the registration is not uploaded yet.</summary>
    public const string WaitingForReceipt = "WaitingForReceipt";

    /// <summary>The receipt of the registration is being read.</summary>
    public const string ReadingReceipt = "ReadingReceipt";

    /// <summary>The receipt cannot declare the registration by itself (a scan, a discarded request, no code or day): the person declares it.</summary>
    public const string ReceiptNotUsable = "ReceiptNotUsable";

    /// <summary>The lease is registered: the procedure is over.</summary>
    public const string Completed = "Completed";
}

/// <summary>The outcome of a sync: where the lease stands and its status now.</summary>
public sealed record CfCompletionResult(string State, LeaseStatus LeaseStatus);

/// <summary>
/// CF-DONE: the lease of the guided service follows the practice. The documents the service delivers (the signed contract with its attestation
/// in one file, then the receipt of the Agenzia delle Entrate) are read into the draft and, once the practice says the contract is signed, the
/// draft is imported as signed with the day of the signature read from them; the receipt read as a registration declares the registration.
/// What cannot be done by itself (a value the reading missed, a receipt that is a scan) is left to the person, who completes it by hand.
/// </summary>
public interface IConcordatoFacileCompletionService
{
    /// <summary>
    /// Does what can be done now for the lease and says where it stands. The lease must be <paramref name="orgId"/>'s
    /// (404 <c>lease_not_found</c> otherwise). <paramref name="actorUserId"/> is recorded as who declared the signature and the registration:
    /// the person who gave CasaZen the documents. Never fails because of the documents or of the practice: what blocks is a state.
    /// </summary>
    Task<CfCompletionResult> SyncAsync(Guid leaseId, Guid orgId, string actorUserId, CancellationToken cancellationToken = default);
}

/// <summary>Puts a document of a lease back to be read (the job <see cref="ExtractLeaseDocumentDataJob"/>): apart, so a test needs no Hangfire.</summary>
public interface ILeaseDocumentReadQueue
{
    void Enqueue(Guid documentId);
}

/// <inheritdoc />
public sealed class HangfireLeaseDocumentReadQueue : ILeaseDocumentReadQueue
{
    public void Enqueue(Guid documentId) => ExtractLeaseDocumentDataJob.EnqueueAsync(documentId);
}

/// <inheritdoc />
public sealed class ConcordatoFacileCompletionService(
    AppDbContext db,
    IConcordatoFacileStatusCacheService statuses,
    ILeaseSigningService signing,
    IRliRegistrationService registrations,
    ILeaseDocumentReadingService receipts,
    ILeaseDocumentReadQueue readQueue,
    ILogger<ConcordatoFacileCompletionService> logger,
    TimeProvider? timeProvider = null) : IConcordatoFacileCompletionService
{
    /// <summary>A document still being read after this long has lost its job: it is read again, once, instead of being waited for.</summary>
    public static readonly TimeSpan StaleReadingAfter = TimeSpan.FromMinutes(15);

    /// <summary>The states of a practice in which the contract is signed by the parties (ADR-008-R21, in the order of the practice).</summary>
    private static readonly CfPracticeStatus[] SignedStates =
        [CfPracticeStatus.SignedReceived, CfPracticeStatus.SentToAssociation, CfPracticeStatus.Attested, CfPracticeStatus.Registered];

    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<CfCompletionResult> SyncAsync(Guid leaseId, Guid orgId, string actorUserId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorUserId);
        await db.EnsureLeaseOfOrgAsync(leaseId, orgId, cancellationToken);

        var lease = await db.LeaseContracts.FirstAsync(l => l.Id == leaseId, cancellationToken);

        // A concordato draft whose guided form was opened before the mark existed (CF-DONE) becomes the service's draft here as well.
        if (!lease.IsConcordatoFacileSource && await FormOpenedAsync(leaseId, cancellationToken) && lease.MarkAsServiceDraft())
        {
            await db.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Draft marked as the guided service's. LeaseId={LeaseId}", leaseId);
        }

        if (!lease.IsConcordatoFacileSource)
            return new CfCompletionResult(CfCompletionStates.NotApplicable, lease.Status);

        if (lease.Status == LeaseStatus.Draft)
        {
            var waiting = await TryImportAsync(leaseId, orgId, actorUserId, cancellationToken);
            if (waiting is not null)
                return new CfCompletionResult(waiting, LeaseStatus.Draft);
        }

        var status = await StatusOfAsync(leaseId, cancellationToken);
        return status switch
        {
            LeaseStatus.Registered => new CfCompletionResult(CfCompletionStates.Completed, status),
            LeaseStatus.Signed => await TryRegisterAsync(leaseId, actorUserId, cancellationToken),
            _ => new CfCompletionResult(CfCompletionStates.NotApplicable, status),
        };
    }

    // ---- the signed contract --------------------------------------------------------------------------------------------

    /// <summary>
    /// Imports the draft as signed when it can: the contract document read into the draft, a practice that says the contract is signed, the
    /// day of the signature and nothing blocking in the review. Null once the lease is no longer a draft; the state it waits in otherwise.
    /// </summary>
    private async Task<string?> TryImportAsync(Guid leaseId, Guid orgId, string actorUserId, CancellationToken cancellationToken)
    {
        var contracts = await db.LeaseDocuments.AsNoTracking()
            .Where(d => d.LeaseContractId == leaseId && d.Kind == LeaseDocumentKind.Contract)
            .OrderByDescending(d => d.UploadedAt).ThenByDescending(d => d.Id)
            .Select(d => new ContractDocument(d.Id, d.ExtractionStatus, d.ExtractionResult, d.ExtractionStartedAt ?? d.UploadedAt))
            .ToListAsync(cancellationToken);
        if (contracts.Count == 0)
            return CfCompletionStates.WaitingForContract;
        if (contracts.Any(d => IsBeingRead(d) && !IsStale(d)))
            return CfCompletionStates.ReadingContract;

        var read = contracts.FirstOrDefault(d =>
            (d.Status is LeaseDocumentExtractionStatus.Completed or LeaseDocumentExtractionStatus.NeedsReview) && IsReadIntoDraft(d.Result));
        if (read is null)
        {
            // Read the way a document of an existing lease is (before the draft was the service's), skipped as a copy of one already there,
            // or lost with its job: read again, this time into the draft. A reading that failed is not tried again: it would fail the same way.
            var again = contracts.FirstOrDefault(d => d.Status != LeaseDocumentExtractionStatus.Failed);
            if (again is null)
                return CfCompletionStates.ContractNeedsData;

            await ReadAgainAsync(again.Id, cancellationToken);
            return CfCompletionStates.ReadingContract;
        }

        // The state of the lease follows the practice: the contract is recorded as signed only once the practice says it is. A practice that
        // cannot be read leaves the import to the person, who knows it.
        var practice = await statuses.GetPracticesAsync(leaseId, orgId, cancellationToken);
        if (!practice.IsAvailable)
            return CfCompletionStates.ContractNeedsData;
        if (!PracticeIsSigned(practice))
            return CfCompletionStates.WaitingForSignature;

        var stipula = StipulaOf(read.Result) ?? await StipulationOfReceiptAsync(leaseId, cancellationToken);
        if (stipula is null)
            return CfCompletionStates.ContractNeedsData;

        try
        {
            await signing.ImportSignedContractFromDocumentAsync(
                leaseId, actorUserId, new ImportedDocumentDeclaration(stipula.Value, read.Id), cancellationToken);
        }
        catch (DomainConflictException ex) when (ex.Code == LeaseDraftErrorCodes.NotDraft)
        {
            // Imported in the meantime (by hand, or by another call of this): the lease goes on from its new status.
            return null;
        }
        catch (Exception ex) when (ex is DomainException or NotFoundException)
        {
            // Data the reading did not find, a period another lease of the house takes, a file gone: the person completes the import.
            logger.LogInformation(
                "The service's contract could not be imported by itself. LeaseId={LeaseId} Reason={Reason}",
                leaseId, (ex as DomainException)?.Code ?? "document_not_found");
            return CfCompletionStates.ContractNeedsData;
        }

        logger.LogInformation("The service's contract was imported as signed. LeaseId={LeaseId}", leaseId);
        return null;
    }

    /// <summary>The document is put back to be read: its reading is «being read» until the job writes the new one.</summary>
    private async Task ReadAgainAsync(Guid documentId, CancellationToken cancellationToken)
    {
        var document = await db.LeaseDocuments.FirstAsync(d => d.Id == documentId, cancellationToken);
        document.ExtractionStatus = LeaseDocumentExtractionStatus.Processing;
        document.ExtractionError = null;
        document.ExtractionStartedAt = _clock.GetUtcNow().UtcDateTime;
        document.ExtractionCompletedAt = null;
        await db.SaveChangesAsync(cancellationToken);
        readQueue.Enqueue(documentId);
        logger.LogInformation("The service's contract is read again into its draft. DocumentId={DocumentId}", documentId);
    }

    private static bool PracticeIsSigned(CfStatusResult practices)
    {
        var practice = practices.Practices
            .Where(p => p.Status != CfPracticeStatus.ClosedWithoutAttestation)
            .OrderByDescending(p => p.StatusChangedAt)
            .FirstOrDefault();
        return practice is not null && SignedStates.Contains(practice.Status);
    }

    // ---- the registration -----------------------------------------------------------------------------------------------

    /// <summary>Declares the registration with the receipt of the Agenzia uploaded to the lease, when it is read as one with its code and day.</summary>
    private async Task<CfCompletionResult> TryRegisterAsync(Guid leaseId, string actorUserId, CancellationToken cancellationToken)
    {
        var receipt = await NewestReceiptAsync(leaseId, cancellationToken);
        if (receipt is null)
            return new CfCompletionResult(CfCompletionStates.WaitingForReceipt, LeaseStatus.Signed);
        if (IsBeingRead(receipt) && !IsStale(receipt))
            return new CfCompletionResult(CfCompletionStates.ReadingReceipt, LeaseStatus.Signed);

        var reading = await receipts.ReadAsync(leaseId, receipt.Id, cancellationToken);
        var values = reading is { State: LeaseDocumentReadingStates.Read, Receipt: { Outcome: "Registered" } read } ? read : null;
        var code = values is null
            ? null
            : values.Code ?? string.Join(' ', new[] { values.Number, values.Series }.Where(s => !string.IsNullOrWhiteSpace(s)));
        var day = ParseDay(values?.RegistrationDate);
        if (string.IsNullOrWhiteSpace(code) || day is null)
            return new CfCompletionResult(CfCompletionStates.ReceiptNotUsable, LeaseStatus.Signed);

        try
        {
            await registrations.DeclareManualRegistrationFromDocumentAsync(
                leaseId, actorUserId, new ManualRegistrationDocumentDeclaration(code, day.Value, receipt.Id), cancellationToken);
        }
        catch (Exception ex) when (ex is DomainException or NotFoundException)
        {
            logger.LogInformation(
                "The registration could not be declared from the receipt by itself. LeaseId={LeaseId} Reason={Reason}",
                leaseId, (ex as DomainException)?.Code ?? "document_not_found");
            var now = await StatusOfAsync(leaseId, cancellationToken);
            return new CfCompletionResult(now == LeaseStatus.Registered ? CfCompletionStates.Completed : CfCompletionStates.ReceiptNotUsable, now);
        }

        logger.LogInformation("The registration was declared from the service's receipt. LeaseId={LeaseId}", leaseId);
        return new CfCompletionResult(CfCompletionStates.Completed, LeaseStatus.Registered);
    }

    /// <summary>The day of the signature the receipt states (the Agenzia's own record), when the contract did not say it.</summary>
    private async Task<DateTime?> StipulationOfReceiptAsync(Guid leaseId, CancellationToken cancellationToken)
    {
        var receipt = await NewestReceiptAsync(leaseId, cancellationToken);
        if (receipt is null || IsBeingRead(receipt))
            return null;

        var reading = await receipts.ReadAsync(leaseId, receipt.Id, cancellationToken);
        return reading is { State: LeaseDocumentReadingStates.Read, Receipt: { Outcome: "Registered" } read } ? ParseDay(read.StipulationDate) : null;
    }

    /// <summary>The newest receipt of the lease that is not a copy of another one (a duplicate is skipped: its twin is the one read).</summary>
    private async Task<ContractDocument?> NewestReceiptAsync(Guid leaseId, CancellationToken cancellationToken) =>
        await db.LeaseDocuments.AsNoTracking()
            .Where(d => d.LeaseContractId == leaseId && d.Kind == LeaseDocumentKind.Registration
                && d.ExtractionStatus != LeaseDocumentExtractionStatus.Skipped)
            .OrderByDescending(d => d.UploadedAt).ThenByDescending(d => d.Id)
            .Select(d => new ContractDocument(d.Id, d.ExtractionStatus, null, d.ExtractionStartedAt ?? d.UploadedAt))
            .FirstOrDefaultAsync(cancellationToken);

    // ---- small reads ----------------------------------------------------------------------------------------------------

    /// <summary>A document of the lease with what the sync needs of its reading: never a value of a person.</summary>
    private sealed record ContractDocument(Guid Id, LeaseDocumentExtractionStatus Status, string? Result, DateTime Since);

    private static bool IsBeingRead(ContractDocument d) =>
        d.Status is LeaseDocumentExtractionStatus.NotStarted or LeaseDocumentExtractionStatus.Processing;

    private bool IsStale(ContractDocument d) => d.Since < _clock.GetUtcNow().UtcDateTime - StaleReadingAfter;

    private Task<bool> FormOpenedAsync(Guid leaseId, CancellationToken cancellationToken) =>
        db.LeaseEvents.AnyAsync(e => e.LeaseContractId == leaseId && e.EventType == LeaseEventType.ConcordatoFacileLinkGenerated, cancellationToken);

    private Task<LeaseStatus> StatusOfAsync(Guid leaseId, CancellationToken cancellationToken) =>
        db.LeaseContracts.AsNoTracking().Where(l => l.Id == leaseId).Select(l => l.Status).FirstAsync(cancellationToken);

    /// <summary>The result the reading of a contract writes when it reads it into a draft (version 3, kind contract), not an older one.</summary>
    private static bool IsReadIntoDraft(string? extractionResult) =>
        ReadResult(extractionResult, root =>
            root.TryGetProperty("version", out var version) && version.TryGetInt32(out var number) && number >= 3
            && root.TryGetProperty("kind", out var kind) && kind.ValueKind == JsonValueKind.String && kind.GetString() == "contract");

    /// <summary>The day of the signature the reading of the contract accepted, if it did.</summary>
    private static DateTime? StipulaOf(string? extractionResult)
    {
        DateTime? day = null;
        ReadResult(extractionResult, root =>
        {
            if (root.TryGetProperty("stipulaDate", out var value) && value.ValueKind == JsonValueKind.String)
                day = ParseDay(value.GetString());
            return true;
        });
        return day;
    }

    private static bool ReadResult(string? extractionResult, Func<JsonElement, bool> read)
    {
        if (string.IsNullOrEmpty(extractionResult))
            return false;
        try
        {
            using var result = JsonDocument.Parse(extractionResult);
            return result.RootElement.ValueKind == JsonValueKind.Object && read(result.RootElement);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static DateTime? ParseDay(string? value) =>
        DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) ? day : null;
}
