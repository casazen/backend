using System.Data;
using Casazen.Core.DTOs.Leases;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Leases;
using Casazen.Core.Features;
using Casazen.Core.Regulatory;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services.LeaseDrafts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// Signature of a lease contract (LT-02: A7-02, A7-16, A7-20; decision D15). See <see cref="ILeaseSigningService"/>
/// and docs/runbooks/rli.md § Contract signature (LT-02).
/// </summary>
/// <remarks>
/// <para>Offline (default): the final contract exists only from an approved template (LT-03); the PDF signed by every
/// party goes to the private bucket first, then one transaction under a row lock on the lease records the stipula
/// (<see cref="LeaseContract.RecordStipula"/>, LT-04), the signers, the Signed status and the
/// <see cref="LeaseEventType.AllPartiesSigned"/> event (payload <c>offline</c>). A failed update removes the file.</para>
/// <para>Provider (<c>Features:ESignProvider</c> on and a configured provider): the provider is called outside any
/// transaction; its events are applied only to a lease AwaitingSignature or PartiallySigned, so a replayed or forged
/// "all signed" never moves a Signed or Registered lease back (A7-20). The lease is Signed only with the signed PDF
/// copied into the private bucket.</para>
/// <para>Import (LR-13): the one path that does not need a template of ours. A draft declared as an imported contract
/// when it was created, with nothing blocking in its review, becomes Signed with the PDF signed elsewhere and the day of the
/// signature, in the same shape as the offline signature (files first, one transaction under the row lock, the files removed if
/// it fails); the gate of the models stays for every other path. The signed PDF is also stored as the <c>Contract</c> document of
/// the lease, as a copy with its own key.</para>
/// </remarks>
public class LeaseSigningService(
    AppDbContext db,
    ILeaseESignService provider,
    IFeatureFlags featureFlags,
    ILeaseTemplateService templateService,
    IFileStorage storage,
    IApeComplianceService apeCompliance,
    ILogger<LeaseSigningService> logger,
    TimeProvider? timeProvider = null) : ILeaseSigningService
{
    public const string OfflineEventPayload = "offline";
    public const string ProviderEventPayload = "provider";
    public const string ImportedEventPayload = "imported";

    private const string PdfContentType = "application/pdf";

    /// <summary>The name of the signed copy in the Documents section when the file had no sound name (as long as <c>LeaseDocument.FileName</c> allows).</summary>
    private const string DefaultDocumentFileName = "contratto-firmato.pdf";

    private const int MaxDocumentFileNameLength = 240;

    private static readonly byte[] PdfSignature = "%PDF-"u8.ToArray();

    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public bool IsProviderSigningAvailable => ESignProviderSigning.IsAvailable(featureFlags, provider);

    public async Task<byte[]> GenerateContractForSignatureAsync(Guid leaseId, CancellationToken cancellationToken = default)
    {
        var lease = await LoadLeaseAsync(leaseId, cancellationToken);
        EnsureNotSignedYet(lease);
        await EnsureSignableAsync(lease);

        // Template gate (LT-03): 422 without an approved, complete template; the preview is the BOZZA endpoint.
        return await templateService.GeneratePdfAsync(lease);
    }

    public async Task<LeaseContract> DeclareOfflineSignatureAsync(
        Guid leaseId,
        string userId,
        OfflineSignatureDeclaration declaration,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentNullException.ThrowIfNull(declaration);

        var stipula = EnsureStipulaDateNotInFuture(declaration.StipulaDate);

        await using var signedContract = await ReadPdfAsync(
                declaration.SignedContract, declaration.SignedContractLength, cancellationToken)
            ?? throw new DomainRuleException(
                LeaseSigningErrorCodes.SignedContractInvalid,
                "LeaseSignedContractInvalid",
                LeaseSigningLimits.MaxSignedContractBytes / (1024 * 1024));

        var lease = await LoadLeaseAsync(leaseId, cancellationToken);
        EnsureNotSignedYet(lease);
        await EnsureSignableAsync(lease);
        // An offline signature is a signature of the final contract: never from a template that is not approved (LT-03).
        templateService.EnsureFinalContractAvailable(lease);

        // File first, in the private bucket: a failed upload changes nothing; a failed database update removes it.
        var key = StorageKeys.LeaseSignedContract(lease.OrgId, lease.Id, StorageKeys.NewFileName(".pdf"));
        await storage.PutAsync(StorageBucket.Private, key, signedContract, PdfContentType, cancellationToken);

        try
        {
            await using var transaction = await BeginTransactionAsync(cancellationToken);
            await LockLeaseAsync(lease.Id, cancellationToken);
            await ReloadAsync(lease, cancellationToken);
            EnsureNotSignedYet(lease);

            var now = UtcNow();
            lease.Status = LeaseStatus.Signed;
            lease.DraftData = null; // the typed state of the wizard (LR-12) is only for a draft; the parties' rows are the contract's data
            lease.SignedPdfStoragePath = key;
            lease.RecordStipula(stipula);
            LeaseRenewal.ConcludeIfEnded(lease, _clock.TodayInRome()); // signed after its last day: concluded, never renewed (LR-14)
            lease.StipulaDeclaredByUserId = userId;
            lease.UpdatedAt = now;
            await MarkEveryPartySignedAsync(lease, LeaseSignatureMethod.Offline, lease.StipulaDate!.Value, now, cancellationToken);
            db.LeaseEvents.Add(new LeaseEvent
            {
                LeaseContractId = lease.Id,
                EventType = LeaseEventType.AllPartiesSigned,
                OccurredAt = now,
                Payload = OfflineEventPayload,
            });

            await db.SaveChangesAsync(cancellationToken);
            if (transaction is not null)
                await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await TryDeleteFileAsync(key);
            throw;
        }

        logger.LogInformation(
            "Offline signature recorded. LeaseId={LeaseId} StipulaDate={StipulaDate:yyyy-MM-dd} RegistrationDeadline={RegistrationDeadline:yyyy-MM-dd}",
            lease.Id, lease.StipulaDate, lease.RegistrationDeadline);
        return lease;
    }

    public async Task<LeaseContract> ImportSignedContractAsync(
        Guid leaseId,
        string userId,
        ImportedContractDeclaration declaration,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentNullException.ThrowIfNull(declaration);

        var stipula = EnsureStipulaDateNotInFuture(declaration.StipulaDate);

        await using var signedContract = await ReadPdfAsync(
                declaration.SignedContract, declaration.SignedContractLength, cancellationToken)
            ?? throw new DomainRuleException(
                LeaseSigningErrorCodes.SignedContractInvalid,
                "LeaseSignedContractInvalid",
                LeaseSigningLimits.MaxSignedContractBytes / (1024 * 1024));

        // A request that is going to be refused writes nothing to the bucket: the same checks run again under the row lock.
        var lease = await LoadLeaseAsync(leaseId, cancellationToken);
        EnsureImportable(lease);

        // Files first, in the private bucket: the signed copy of the lease and its own copy as the Contract document of the Documents
        // section. Two files, not one shared key: deleting a document must never take away the signed copy of the contract (nor the
        // other way round). A failed upload or database update removes what was written.
        var signedKey = StorageKeys.LeaseSignedContract(lease.OrgId, lease.Id, StorageKeys.NewFileName(".pdf"));
        var documentKey = StorageKeys.LeaseAttachment(lease.OrgId, lease.Id, StorageKeys.NewFileName(".pdf"));
        var written = new List<string>(2);
        try
        {
            await storage.PutAsync(StorageBucket.Private, signedKey, signedContract, PdfContentType, cancellationToken);
            written.Add(signedKey);
            signedContract.Position = 0;
            await storage.PutAsync(StorageBucket.Private, documentKey, signedContract, PdfContentType, cancellationToken);
            written.Add(documentKey);

            return await RecordImportAsync(
                leaseId, userId, stipula, signedKey, documentKey, DocumentFileName(declaration.FileName), signedContract.Length, existingDocumentId: null, cancellationToken);
        }
        catch
        {
            foreach (var key in written)
                await TryDeleteFileAsync(key);
            throw;
        }
    }

    public async Task<LeaseContract> ImportSignedContractFromDocumentAsync(
        Guid leaseId,
        string userId,
        ImportedDocumentDeclaration declaration,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentNullException.ThrowIfNull(declaration);

        var stipula = EnsureStipulaDateNotInFuture(declaration.StipulaDate);

        // The document has to be a contract document of THIS lease and of its org (stated here, not left to the filter of the request):
        // the contract of another lease, a document of another kind, one that names another org, are not there.
        var orgId = await db.LeaseContracts
                .AsNoTracking()
                .Where(l => l.Id == leaseId)
                .Select(l => (Guid?)l.OrgId)
                .FirstOrDefaultAsync(cancellationToken)
            ?? throw LeaseNotFound(leaseId);
        var document = await db.LeaseDocuments
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    d => d.Id == declaration.DocumentId && d.LeaseContractId == leaseId && d.OrgId == orgId && d.Kind == LeaseDocumentKind.Contract,
                    cancellationToken)
            ?? throw DocumentNotFound();

        await using var stored = await storage.OpenReadAsync(StorageBucket.Private, document.StorageKey, cancellationToken)
            ?? throw DocumentNotFound();
        await using var signedContract = await ReadPdfAsync(stored, document.SizeBytes, cancellationToken)
            ?? throw new DomainRuleException(
                LeaseSigningErrorCodes.SignedContractInvalid,
                "LeaseSignedContractInvalid",
                LeaseSigningLimits.MaxSignedContractBytes / (1024 * 1024));

        // A request that is going to be refused writes nothing to the bucket: the same checks run again under the row lock.
        var lease = await LoadLeaseAsync(leaseId, cancellationToken);
        EnsureImportable(lease);

        // Only the signed copy of the lease is new: the document keeps the file it has, and is the Contract document of the Documents section.
        var signedKey = StorageKeys.LeaseSignedContract(lease.OrgId, lease.Id, StorageKeys.NewFileName(".pdf"));
        try
        {
            await storage.PutAsync(StorageBucket.Private, signedKey, signedContract, PdfContentType, cancellationToken);
            return await RecordImportAsync(
                leaseId, userId, stipula, signedKey, documentKey: null, document.FileName, document.SizeBytes, document.Id, cancellationToken);
        }
        catch
        {
            await TryDeleteFileAsync(signedKey);
            throw;
        }
    }

    private static NotFoundException DocumentNotFound() =>
        new("Lease document not found") { Code = "document_not_found", MessageKey = "DocumentNotFound" };

    /// <summary>
    /// The one transaction of the import, under the row lock the guided draft takes (a wait of more than a few seconds is a lost race,
    /// 409 <c>lease_draft_conflict</c>, never a timeout): the lease is read again once nobody else can change it, checked again, its
    /// typed state written to the columns if it still lives only in <c>DraftData</c>, and then signed.
    /// </summary>
    private async Task<LeaseContract> RecordImportAsync(
        Guid leaseId,
        string userId,
        DateTime stipula,
        string signedKey,
        string? documentKey,
        string documentName,
        long documentSize,
        Guid? existingDocumentId,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var transaction = await BeginTransactionAsync(cancellationToken);
            await LeaseDraftLocking.LockRowAsync(db, leaseId, cancellationToken);
            db.ChangeTracker.Clear();

            var lease = await LoadLeaseAsync(leaseId, cancellationToken);
            var values = EnsureImportable(lease);
            if (lease.DraftData is not null)
            {
                // Complete as far as the review can tell, but the columns still hold the starting values (the rules changed since the
                // last save): write what was typed where every other reader of the lease looks, as a save would.
                await LeaseDraftWriter.ProjectAsync(
                    db, lease, values, LeaseDraftRules.Read(values, lease.Origin), concordato: null, dataComplete: true,
                    _clock.TodayInRomeAsDateOnly(), cancellationToken);
            }

            LeaseContractTerms.EnsureTerm(lease.ContractType, lease.StartDate, lease.EndDate);

            var now = UtcNow();
            lease.Status = LeaseStatus.Signed;
            lease.DraftData = null; // the typed state of the wizard is only for a draft; the rows of the parties are the contract's data
            lease.SignedPdfStoragePath = signedKey;
            lease.RecordStipula(stipula);
            // A contract brought in after its last day is a lease that is over: concluded, never renewed (LR-14, no retroactive renewal).
            LeaseRenewal.ConcludeIfEnded(lease, _clock.TodayInRome());
            lease.StipulaDeclaredByUserId = userId;
            lease.UpdatedAt = now;
            await MarkEveryPartySignedAsync(lease, LeaseSignatureMethod.Offline, lease.StipulaDate!.Value, now, cancellationToken);

            if (existingDocumentId is { } existing)
            {
                // The contract document the owner uploaded: still there, under the lock (the draft is the lease's own).
                if (!await db.LeaseDocuments.AnyAsync(d => d.Id == existing && d.LeaseContractId == leaseId && d.Kind == LeaseDocumentKind.Contract, cancellationToken))
                    throw DocumentNotFound();
            }
            else
            {
                db.LeaseDocuments.Add(new LeaseDocument
                {
                    LeaseContractId = lease.Id,
                    OrgId = lease.OrgId,
                    Kind = LeaseDocumentKind.Contract,
                    FileName = documentName,
                    StorageKey = documentKey!,
                    UploadedBy = userId,
                    SizeBytes = documentSize,
                    UploadedAt = now,
                });
            }
            db.LeaseEvents.Add(new LeaseEvent
            {
                LeaseContractId = lease.Id,
                EventType = LeaseEventType.AllPartiesSigned,
                OccurredAt = now,
                Payload = ImportedEventPayload,
            });
            // A microsecond later (what the database keeps), so the timeline, which is ordered by instant, always lists the two the same way.
            db.LeaseEvents.Add(new LeaseEvent
            {
                LeaseContractId = lease.Id,
                EventType = LeaseEventType.Imported,
                OccurredAt = now.AddTicks(TimeSpan.TicksPerMicrosecond),
            });

            await db.SaveChangesAsync(cancellationToken);
            if (transaction is not null)
                await transaction.CommitAsync(cancellationToken);

            logger.LogInformation(
                "Signed contract imported. LeaseId={LeaseId} StipulaDate={StipulaDate:yyyy-MM-dd} RegistrationDeadline={RegistrationDeadline:yyyy-MM-dd}",
                lease.Id, lease.StipulaDate, lease.RegistrationDeadline);
            return lease;
        }
        catch (Exception ex) when (LeaseDraftLocking.IsContention(ex))
        {
            logger.LogInformation("Lease busy: the import was refused. LeaseId={LeaseId}", leaseId);
            throw new DomainConflictException(LeaseDraftErrorCodes.Conflict, "LeaseDraftConflict");
        }
    }

    /// <summary>
    /// The lease can be imported: a draft (409 <c>lease_not_draft</c>), declared as an imported contract when it was created (422
    /// <c>lease_import_not_imported_origin</c>: a contract made in CasaZen is signed through the models, decision LR-D28), with nothing
    /// blocking in the review of what was typed (422 <c>lease_review_blocked</c>). Returns the values read, which the caller projects.
    /// The lease is loaded with its property and its parties.
    /// </summary>
    private LeaseDraftValues EnsureImportable(LeaseContract lease)
    {
        if (lease.Status != LeaseStatus.Draft)
            throw new DomainConflictException(LeaseDraftErrorCodes.NotDraft, "LeaseNotDraft");

        if (lease.Origin != LeaseOrigin.Imported)
            throw new DomainRuleException(LeaseSigningErrorCodes.ImportNotImported, "LeaseImportNotImported");

        // The same review the draft shows: the blocks are the needs of the first four steps. For an imported contract the missing APE
        // and cadastral data are warnings, the template is not asked for and the range of a concordato is not checked.
        var values = LeaseDraftProjection.ReadValues(lease);
        var input = new LeaseDraftProgressInput
        {
            Origin = lease.Origin,
            Status = lease.Status,
            WizardMaxStep = lease.WizardMaxStep,
            Values = values,
            Facts = new LeaseDraftFacts { PropertyExists = lease.Property is not null },
            Today = _clock.TodayInRomeAsDateOnly(),
        };
        if (LeaseReview.HasBlock(LeaseReview.Compute(input, new LeaseReviewFacts())))
            throw new DomainRuleException(LeaseDraftErrorCodes.ReviewBlocked, "LeaseReviewBlocked");

        return values;
    }

    /// <summary>
    /// The name of the signed copy in the Documents section: the name the file had when it is a sound one (a name, not a path, no
    /// control character, a <c>.pdf</c>), otherwise a fixed one.
    /// </summary>
    private static string DocumentFileName(string? declared)
    {
        var name = Path.GetFileName((declared ?? string.Empty).Replace('\\', '/'));
        return name.Length is > 0 and <= MaxDocumentFileNameLength
            && !name.Any(char.IsControl)
            && string.Equals(Path.GetExtension(name), ".pdf", StringComparison.OrdinalIgnoreCase)
                ? name
                : DefaultDocumentFileName;
    }

    public async Task<LeaseContract> DeclareStipulaAsync(
        Guid leaseId,
        string userId,
        DateTime stipulaDate,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        var stipula = EnsureStipulaDateNotInFuture(stipulaDate);

        var lease = await LoadLeaseAsync(leaseId, cancellationToken);
        EnsureStipulaCanBeDeclared(lease);

        await using var transaction = await BeginTransactionAsync(cancellationToken);
        await LockLeaseAsync(lease.Id, cancellationToken);
        await ReloadAsync(lease, cancellationToken);
        EnsureStipulaCanBeDeclared(lease);

        var now = UtcNow();
        lease.RecordStipula(stipula);
        lease.StipulaDeclaredByUserId = userId;
        lease.UpdatedAt = now;
        db.LeaseEvents.Add(new LeaseEvent
        {
            LeaseContractId = lease.Id,
            EventType = LeaseEventType.StipulaDeclared,
            OccurredAt = now,
        });

        await db.SaveChangesAsync(cancellationToken);
        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);

        logger.LogInformation(
            "Stipula date declared for a signed lease. LeaseId={LeaseId} StipulaDate={StipulaDate:yyyy-MM-dd} RegistrationDeadline={RegistrationDeadline:yyyy-MM-dd}",
            lease.Id, lease.StipulaDate, lease.RegistrationDeadline);
        return lease;
    }

    public async Task<SignedContractFile> OpenSignedContractAsync(Guid leaseId, CancellationToken cancellationToken = default)
    {
        var lease = await db.LeaseContracts
            .AsNoTracking()
            .Where(l => l.Id == leaseId)
            .Select(l => new { l.Id, l.SignedPdfStoragePath })
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw LeaseNotFound(leaseId);

        // A path left by the old e-sign stub (a provider path, not a key of the private bucket) is not a file we hold.
        if (!StorageKeys.IsValid(lease.SignedPdfStoragePath))
        {
            throw new NotFoundException($"No signed contract for lease {leaseId}.")
            {
                Code = LeaseSigningErrorCodes.SignedContractNotAvailable,
                MessageKey = "LeaseSignedContractNotAvailable",
            };
        }

        var content = await storage.OpenReadAsync(StorageBucket.Private, lease.SignedPdfStoragePath!, cancellationToken);
        if (content is null)
        {
            logger.LogWarning("Stored signed contract missing. LeaseId={LeaseId}", leaseId);
            throw new NotFoundException($"Signed contract file of lease {leaseId} is missing.")
            {
                Code = "document_file_missing",
                MessageKey = "DocumentFileMissing",
            };
        }

        return new SignedContractFile(content, $"contratto-firmato-{lease.Id}.pdf");
    }

    public async Task<IReadOnlyList<LeaseSignerDto>> GetSignersAsync(Guid leaseId, CancellationToken cancellationToken = default)
    {
        var lease = await db.LeaseContracts
            .AsNoTracking()
            .Include(l => l.Parties)
            .Include(l => l.Signers)
            .SingleOrDefaultAsync(l => l.Id == leaseId, cancellationToken)
            ?? throw LeaseNotFound(leaseId);

        return ToSigners(lease);
    }

    public async Task<LeaseSigningStateDto> GetSigningStateAsync(Guid leaseId, CancellationToken cancellationToken = default)
    {
        var lease = await db.LeaseContracts
            .AsNoTracking()
            .Include(l => l.Property)
            .Include(l => l.Parties)
            .Include(l => l.Signers)
            .SingleOrDefaultAsync(l => l.Id == leaseId, cancellationToken)
            ?? throw LeaseNotFound(leaseId);

        // The final contract exists only before the signature and from a complete, approved template (LT-03).
        var contractBlocker = RliRegistrationDeadline.IsBeforeFullSignature(lease.Status)
            ? templateService.GetFinalContractBlocker(lease)
            : LeaseSigningErrorCodes.AlreadySigned;
        return new LeaseSigningStateDto(IsProviderSigningAvailable, contractBlocker is null, contractBlocker, ToSigners(lease));
    }

    /// <summary>One entry per party (parties and signers loaded): the persisted signer, or the offline state.</summary>
    private IReadOnlyList<LeaseSignerDto> ToSigners(LeaseContract lease)
    {
        var now = UtcNow();
        var notSignedYet = RliRegistrationDeadline.IsBeforeFullSignature(lease.Status);
        return lease.Parties
            .OrderBy(p => p.Role)
            .ThenBy(p => p.Position)
            .ThenBy(p => p.LastName, StringComparer.Ordinal)
            .Select(party =>
            {
                var signer = lease.Signers.FirstOrDefault(s => s.PartyId == party.Id);
                if (signer is null)
                {
                    // No signature path started for this party: it signs offline (paper or its own digital signature).
                    return new LeaseSignerDto(
                        party.Id, party.Role, party.FirstName, party.LastName,
                        LeaseSignatureMethod.Offline,
                        notSignedYet ? LeaseSignerStatus.Pending : LeaseSignerStatus.Signed,
                        SigningUrl: null,
                        SigningUrlExpiresAt: null,
                        SigningUrlExpired: false,
                        SignedAt: notSignedYet ? null : lease.StipulaDate);
                }

                var pending = signer.Status == LeaseSignerStatus.Pending;
                return new LeaseSignerDto(
                    party.Id, party.Role, party.FirstName, party.LastName,
                    signer.Method,
                    signer.Status,
                    pending ? signer.SigningUrl : null,
                    pending ? signer.SigningUrlExpiresAt : null,
                    SigningUrlExpired: pending && signer.SigningUrlExpiresAt is { } expiresAt && expiresAt <= now,
                    signer.SignedAt);
            })
            .ToList();
    }

    public async Task<IReadOnlyList<LeaseSignerDto>> InitiateProviderSigningAsync(Guid leaseId, CancellationToken cancellationToken = default)
    {
        // Flag off or no configured provider: nothing is recorded and nothing reaches a provider.
        if (!IsProviderSigningAvailable)
            throw new DomainConflictException(LeaseSigningErrorCodes.ProviderUnavailable, "ESignProviderUnavailable");

        var lease = await LoadLeaseAsync(leaseId, cancellationToken);
        EnsureDraft(lease);
        await EnsureSignableAsync(lease);
        var pdf = await templateService.GeneratePdfAsync(lease);

        // From here on a client that disconnects must not lose the provider's session: no cancellation.
        SigningSessionResult session;
        try
        {
            session = await provider.InitiateSigningAsync(lease, pdf, CancellationToken.None);
        }
        catch (ESignProviderException ex)
        {
            logger.LogWarning(ex, "E-signature provider refused the signing session. LeaseId={LeaseId}", lease.Id);
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "E-signature provider refused the signing session. LeaseId={LeaseId}", lease.Id);
            throw new ESignProviderException("The e-signature provider refused the signing session.", ex);
        }

        var links = session.Signers.DistinctBy(s => s.PartyId).ToDictionary(s => s.PartyId);
        if (string.IsNullOrWhiteSpace(session.ExternalSessionId) || lease.Parties.Any(p => !links.ContainsKey(p.Id)))
        {
            logger.LogWarning("E-signature provider returned an incomplete session. LeaseId={LeaseId}", lease.Id);
            throw new ESignProviderException("The e-signature provider returned a session without a link for every party.");
        }

        await using (var transaction = await BeginTransactionAsync(CancellationToken.None))
        {
            await LockLeaseAsync(lease.Id, CancellationToken.None);
            await ReloadAsync(lease, CancellationToken.None);
            EnsureDraft(lease);

            var now = UtcNow();
            lease.Status = LeaseStatus.AwaitingSignature;
            lease.DraftData = null; // the lease leaves the draft: no typed state of the wizard stays behind (LR-12)
            lease.ExternalSigningSessionId = session.ExternalSessionId;
            lease.UpdatedAt = now;

            var signers = await db.LeaseSigners.Where(s => s.LeaseContractId == lease.Id).ToListAsync(CancellationToken.None);
            foreach (var party in lease.Parties)
            {
                var link = links[party.Id];
                var signer = signers.FirstOrDefault(s => s.PartyId == party.Id) ?? AddSigner(lease, party.Id);
                signer.Method = LeaseSignatureMethod.Provider;
                signer.Status = LeaseSignerStatus.Pending;
                signer.ExternalSignerId = link.ExternalSignerId;
                signer.SigningUrl = link.SigningUrl;
                signer.SigningUrlExpiresAt = link.ExpiresAt;
                signer.SignedAt = null;
                signer.UpdatedAt = now;
            }

            db.LeaseEvents.Add(new LeaseEvent
            {
                LeaseContractId = lease.Id,
                EventType = LeaseEventType.SigningInitiated,
                OccurredAt = now,
                Payload = ProviderEventPayload,
            });

            await db.SaveChangesAsync(CancellationToken.None);
            if (transaction is not null)
                await transaction.CommitAsync(CancellationToken.None);
        }

        logger.LogInformation("Provider signing initiated. LeaseId={LeaseId}", lease.Id);
        return await GetSignersAsync(lease.Id, CancellationToken.None);
    }

    public async Task HandleProviderEventAsync(string payload, CancellationToken cancellationToken = default)
    {
        // Events queued before the flag was turned off (or without a provider client) are dropped: nothing reaches a
        // provider and nothing changes.
        if (!IsProviderSigningAvailable)
        {
            logger.LogWarning("E-sign webhook event ignored: the provider signing path is not available");
            return;
        }

        var providerEvent = await provider.ParseWebhookEventAsync(payload);
        if (providerEvent is null || string.IsNullOrWhiteSpace(providerEvent.ExternalSessionId))
        {
            logger.LogWarning("E-sign webhook event not recognised by the provider");
            return;
        }

        if (providerEvent.Kind == ESignEventKind.Other)
            return;

        // Background job of an anonymous webhook: no request user, so the tenant filter is off; the session id comes
        // from a body whose HMAC signature was verified.
        var lease = await db.LeaseContracts
            .AsNoTracking()
            .Where(l => l.ExternalSigningSessionId == providerEvent.ExternalSessionId)
            .Select(l => new { l.Id, l.OrgId, l.Status })
            .SingleOrDefaultAsync(cancellationToken);
        if (lease is null)
        {
            logger.LogWarning("E-sign webhook event for an unknown signing session");
            return;
        }

        if (!AcceptsProviderEvents(lease.Status))
        {
            logger.LogInformation(
                "E-sign webhook event {Kind} ignored for lease {LeaseId} in status {Status}",
                providerEvent.Kind, lease.Id, lease.Status);
            return;
        }

        if (providerEvent.Kind == ESignEventKind.SignerSigned)
            await RecordProviderSignatureAsync(lease.Id, providerEvent.ExternalSignerId, cancellationToken);
        else
            await CompleteProviderSigningAsync(lease.Id, lease.OrgId, providerEvent.ExternalSessionId, cancellationToken);
    }

    /// <summary>One party signed through the provider: its signer is Signed and the lease PartiallySigned.</summary>
    private async Task RecordProviderSignatureAsync(Guid leaseId, string? externalSignerId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(externalSignerId))
        {
            logger.LogWarning("E-sign signer event without signer id. LeaseId={LeaseId}", leaseId);
            return;
        }

        await using var transaction = await BeginTransactionAsync(cancellationToken);
        await LockLeaseAsync(leaseId, cancellationToken);
        var lease = await ReloadLeaseAsync(leaseId, cancellationToken);
        if (lease is null || !AcceptsProviderEvents(lease.Status))
            return;

        var signer = await db.LeaseSigners.SingleOrDefaultAsync(
            s => s.LeaseContractId == leaseId
                && s.Method == LeaseSignatureMethod.Provider
                && s.ExternalSignerId == externalSignerId,
            cancellationToken);
        if (signer is null)
        {
            logger.LogWarning("E-sign signer event for an unknown signer. LeaseId={LeaseId}", leaseId);
            return;
        }

        // Replayed event: the signature is already recorded.
        if (signer.Status == LeaseSignerStatus.Signed)
            return;

        var now = UtcNow();
        signer.Status = LeaseSignerStatus.Signed;
        signer.SignedAt = now;
        signer.SigningUrl = null;
        signer.SigningUrlExpiresAt = null;
        signer.UpdatedAt = now;
        lease.Status = LeaseStatus.PartiallySigned;
        lease.UpdatedAt = now;
        // The payload names the signer by party id, never by email: event payloads hold no personal data (A7-17).
        db.LeaseEvents.Add(new LeaseEvent
        {
            LeaseContractId = leaseId,
            EventType = LeaseEventType.PartySignedDocument,
            OccurredAt = now,
            Payload = signer.PartyId.ToString(),
        });

        await db.SaveChangesAsync(cancellationToken);
        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);
        logger.LogInformation("Party signed through the provider. LeaseId={LeaseId}", leaseId);
    }

    /// <summary>
    /// Every party signed: the signed PDF is copied from the provider into the private bucket, then the lease is Signed
    /// with its stipula. Without a valid PDF nothing changes and the job fails (Hangfire retries it).
    /// </summary>
    private async Task CompleteProviderSigningAsync(Guid leaseId, Guid orgId, string externalSessionId, CancellationToken cancellationToken)
    {
        MemoryStream? signedContract;
        await using (var download = await provider.DownloadSignedDocumentAsync(externalSessionId, cancellationToken))
        {
            signedContract = await ReadPdfAsync(download, declaredLength: null, cancellationToken);
        }

        if (signedContract is null)
            throw new ESignProviderException($"The e-signature provider returned no valid signed PDF for lease {leaseId}.");

        var key = StorageKeys.LeaseSignedContract(orgId, leaseId, StorageKeys.NewFileName(".pdf"));
        await using (signedContract)
        {
            await storage.PutAsync(StorageBucket.Private, key, signedContract, PdfContentType, cancellationToken);
        }

        try
        {
            await using var transaction = await BeginTransactionAsync(cancellationToken);
            await LockLeaseAsync(leaseId, cancellationToken);
            var lease = await ReloadLeaseAsync(leaseId, cancellationToken);
            if (lease is null || !AcceptsProviderEvents(lease.Status))
            {
                await TryDeleteFileAsync(key);
                return;
            }

            // The provider reports no signing time: the stipula is the Rome day on which CasaZen records the last
            // signature, the same instant as the AllPartiesSigned event (LT-04).
            var now = UtcNow();
            lease.Status = LeaseStatus.Signed;
            lease.SignedPdfStoragePath = key;
            lease.RecordStipula(now);
            LeaseRenewal.ConcludeIfEnded(lease, _clock.TodayInRome()); // signed after its last day: concluded, never renewed (LR-14)
            lease.UpdatedAt = now;
            await db.Entry(lease).Collection(l => l.Parties).LoadAsync(cancellationToken);
            await MarkEveryPartySignedAsync(lease, LeaseSignatureMethod.Provider, now, now, cancellationToken);
            db.LeaseEvents.Add(new LeaseEvent
            {
                LeaseContractId = leaseId,
                EventType = LeaseEventType.AllPartiesSigned,
                OccurredAt = now,
                Payload = ProviderEventPayload,
            });

            await db.SaveChangesAsync(cancellationToken);
            if (transaction is not null)
                await transaction.CommitAsync(cancellationToken);

            logger.LogInformation(
                "All parties signed through the provider. LeaseId={LeaseId} StipulaDate={StipulaDate:yyyy-MM-dd} RegistrationDeadline={RegistrationDeadline:yyyy-MM-dd}",
                leaseId, lease.StipulaDate, lease.RegistrationDeadline);
        }
        catch
        {
            await TryDeleteFileAsync(key);
            throw;
        }
    }

    /// <summary>Provider events move only a lease whose signature is in progress (A7-20).</summary>
    private static bool AcceptsProviderEvents(LeaseStatus status) =>
        status is LeaseStatus.AwaitingSignature or LeaseStatus.PartiallySigned;

    /// <summary>Every party's signer row Signed with <paramref name="signedAt"/> (kept when already recorded).</summary>
    private async Task MarkEveryPartySignedAsync(
        LeaseContract lease, LeaseSignatureMethod method, DateTime signedAt, DateTime now, CancellationToken cancellationToken)
    {
        var signers = await db.LeaseSigners.Where(s => s.LeaseContractId == lease.Id).ToListAsync(cancellationToken);
        foreach (var party in lease.Parties)
        {
            var signer = signers.FirstOrDefault(s => s.PartyId == party.Id) ?? AddSigner(lease, party.Id);
            var alreadySigned = signer.Status == LeaseSignerStatus.Signed;
            signer.Method = alreadySigned ? signer.Method : method;
            signer.Status = LeaseSignerStatus.Signed;
            signer.SignedAt = alreadySigned ? signer.SignedAt ?? signedAt : signedAt;
            signer.SigningUrl = null;
            signer.SigningUrlExpiresAt = null;
            signer.UpdatedAt = now;
        }
    }

    private LeaseSigner AddSigner(LeaseContract lease, Guid partyId)
    {
        var signer = new LeaseSigner
        {
            OrgId = lease.OrgId,
            LeaseContractId = lease.Id,
            PartyId = partyId,
        };
        db.LeaseSigners.Add(signer);
        return signer;
    }

    /// <summary>Checks shared by every signature path: the term of the contract type (LT-10, 422) and a valid APE.</summary>
    private async Task EnsureSignableAsync(LeaseContract lease)
    {
        LeaseContractTerms.EnsureTerm(lease.ContractType, lease.StartDate, lease.EndDate);
        await apeCompliance.EnsurePropertyHasValidApeAsync(lease.PropertyId);
    }

    private static void EnsureDraft(LeaseContract lease)
    {
        if (lease.Status != LeaseStatus.Draft)
            throw new DomainConflictException(LeaseSigningErrorCodes.NotDraft, "LeaseSigningNotDraft");
    }

    /// <summary>The contract is not signed by every party yet (Draft, AwaitingSignature, PartiallySigned).</summary>
    private static void EnsureNotSignedYet(LeaseContract lease)
    {
        if (!RliRegistrationDeadline.IsBeforeFullSignature(lease.Status))
            throw new DomainConflictException(LeaseSigningErrorCodes.AlreadySigned, "LeaseAlreadySigned");
    }

    /// <summary>A later stipula declaration: only a lease signed by every party whose stipula was never recorded.</summary>
    private static void EnsureStipulaCanBeDeclared(LeaseContract lease)
    {
        if (RliRegistrationDeadline.IsBeforeFullSignature(lease.Status))
            throw new DomainRuleException(LeaseSigningErrorCodes.StipulaLeaseNotSigned, "LeaseStipulaLeaseNotSigned");

        if (lease.StipulaDate is not null)
            throw new DomainConflictException(LeaseSigningErrorCodes.StipulaAlreadyRecorded, "LeaseStipulaAlreadyRecorded");
    }

    /// <summary>The stipula is a calendar date (Europe/Rome) not after today.</summary>
    private DateTime EnsureStipulaDateNotInFuture(DateTime stipulaDate)
    {
        if (RomeCalendar.DateInRome(stipulaDate) > _clock.TodayInRomeAsDateOnly())
            throw new DomainRuleException(LeaseSigningErrorCodes.StipulaDateInFuture, "LeaseStipulaDateInFuture");
        return stipulaDate;
    }

    private async Task<LeaseContract> LoadLeaseAsync(Guid leaseId, CancellationToken cancellationToken) =>
        await db.LeaseContracts
            .Include(l => l.Property)
            .Include(l => l.Parties)
            .SingleOrDefaultAsync(l => l.Id == leaseId, cancellationToken)
        ?? throw LeaseNotFound(leaseId);

    private static NotFoundException LeaseNotFound(Guid leaseId) =>
        new($"Lease {leaseId} not found.")
        {
            Code = "lease_not_found",
            MessageKey = "LeaseNotFound",
        };

    /// <summary>
    /// The whole content when it is a PDF (<c>%PDF-</c> signature) of at most
    /// <see cref="LeaseSigningLimits.MaxSignedContractBytes"/>, positioned at the start; null otherwise. The declared
    /// length is not trusted: the content is counted while it is read.
    /// </summary>
    private static async Task<MemoryStream?> ReadPdfAsync(Stream content, long? declaredLength, CancellationToken cancellationToken)
    {
        if (declaredLength is <= 0 or > LeaseSigningLimits.MaxSignedContractBytes)
            return null;

        var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await content.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > LeaseSigningLimits.MaxSignedContractBytes)
            {
                await buffer.DisposeAsync();
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        if (buffer.Length < PdfSignature.Length || !buffer.GetBuffer().AsSpan(0, PdfSignature.Length).SequenceEqual(PdfSignature))
        {
            await buffer.DisposeAsync();
            return null;
        }

        buffer.Position = 0;
        return buffer;
    }

    private async Task TryDeleteFileAsync(string key)
    {
        try
        {
            await storage.DeleteAsync(StorageBucket.Private, key, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not delete an orphan signed contract from the private bucket");
        }
    }

    private async Task<IDbContextTransaction?> BeginTransactionAsync(CancellationToken cancellationToken)
    {
        if (!db.Database.IsRelational() || db.Database.CurrentTransaction is not null)
            return null;

        return await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
    }

    /// <summary>Row lock on the lease until the end of the transaction: concurrent signature changes run one after the other.</summary>
    private async Task LockLeaseAsync(Guid leaseId, CancellationToken cancellationToken)
    {
        if (!db.Database.IsNpgsql())
            return;

        await db.Database
            .SqlQuery<Guid>($"""SELECT "Id" AS "Value" FROM "LeaseContracts" WHERE "Id" = {leaseId} FOR UPDATE""")
            .ToListAsync(cancellationToken);
    }

    /// <summary>The lease as stored now (a tracked instance is refreshed: its values may predate the lock).</summary>
    private async Task<LeaseContract?> ReloadLeaseAsync(Guid leaseId, CancellationToken cancellationToken)
    {
        var lease = await db.LeaseContracts.SingleOrDefaultAsync(l => l.Id == leaseId, cancellationToken);
        if (lease is not null)
            await ReloadAsync(lease, cancellationToken);
        return lease;
    }

    private async Task ReloadAsync(object entity, CancellationToken cancellationToken)
    {
        var entry = db.Entry(entity);
        if (entry.State is not (EntityState.Added or EntityState.Detached))
            await entry.ReloadAsync(cancellationToken);
    }

    private DateTime UtcNow() => _clock.GetUtcNow().UtcDateTime;
}
