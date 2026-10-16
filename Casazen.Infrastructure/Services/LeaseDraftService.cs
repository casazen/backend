using System.Data;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Leases;
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
/// The guided draft of a lease (LR-12, <see cref="ILeaseDraftService"/>). A draft is a <see cref="LeaseContract"/> in status
/// <see cref="LeaseStatus.Draft"/> whose columns are the starting values (<see cref="LeaseDraftDefaults"/>) until the landlord types
/// valid ones; the whole typed state is the encrypted JSON of <see cref="LeaseContract.DraftData"/> until it is complete, and the
/// valid values are projected into the columns and the rows of the parties every other reader of the lease uses
/// (<see cref="LeaseDraftProjection"/>).
/// </summary>
/// <remarks>
/// <para><b>One writer at a time.</b> Every change takes a row lock on the lease (<c>SELECT … FOR UPDATE</c>, PostgreSQL), the same
/// the signature takes, and reads the lease after it, so it sees what the previous writer committed. A save carries the version it
/// was made on; if the draft moved on (another tab, another device) nothing is written and the answer is 409
/// <c>lease_draft_conflict</c>: no value is lost without notice and none is overwritten.</para>
/// <para><b>No personal data in a log or an error.</b> The messages carry ids, steps and counts. The typed values are read from the
/// request and written to the encrypted column and the party rows; the exceptions that name a field never carry what was typed.</para>
/// </remarks>
public sealed class LeaseDraftService(
    AppDbContext db,
    ICanoneConcordatoEligibilityService eligibility,
    IApeComplianceService apeCompliance,
    ILeaseTemplateService templates,
    LeaseDraftDeleter deleter,
    ILogger<LeaseDraftService> logger,
    TimeProvider? timeProvider = null) : ILeaseDraftService
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private readonly LeaseConcordatoAssessor _concordato = new(eligibility, timeProvider ?? TimeProvider.System);

    // ---- creation and reads --------------------------------------------------------------------------------------

    public async Task<Guid?> FindPropertyIdAsync(Guid leaseId, CancellationToken cancellationToken = default) =>
        await db.LeaseContracts
            .AsNoTracking()
            .Where(l => l.Id == leaseId)
            .Select(l => (Guid?)l.PropertyId)
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<LeaseDraftCreated> CreateAsync(Guid propertyId, LeaseOrigin origin, CancellationToken cancellationToken = default)
    {
        var property = await db.Properties.AsNoTracking().FirstOrDefaultAsync(p => p.Id == propertyId, cancellationToken)
            ?? throw PropertyNotFound(propertyId);

        var now = UtcNow();
        var values = new LeaseDraftValues { PropertyId = property.Id };
        var lease = new LeaseContract
        {
            PropertyId = property.Id,
            OrgId = property.OrgId,
            Status = LeaseStatus.Draft,
            StartDate = LeaseDraftDefaults.PlaceholderDate,
            EndDate = LeaseDraftDefaults.PlaceholderDate,
            MonthlyRent = LeaseDraftDefaults.PlaceholderRent,
            WizardMaxStep = LeaseDraftDefaults.FirstStep,
            Origin = origin,
            DraftVersion = 1,
            DraftData = values.ToJson(),
            CreatedAt = now,
            UpdatedAt = now,
        };
        lease.SetContractTerms(LeaseDraftDefaults.PlaceholderType, null);

        db.LeaseContracts.Add(lease);
        db.LeaseEvents.Add(new LeaseEvent { LeaseContractId = lease.Id, EventType = LeaseEventType.Created, OccurredAt = now });
        await db.SaveChangesAsync(cancellationToken);

        var evaluation = await EvaluateAsync(lease, values, fullApeCheck: false, cancellationToken);
        logger.LogInformation("Lease draft created. LeaseId={LeaseId} PropertyId={PropertyId} Origin={Origin}", lease.Id, property.Id, origin);
        return new LeaseDraftCreated(lease.Id, lease.DraftVersion, evaluation.Flow);
    }

    public async Task<LeaseDraftView> GetAsync(Guid leaseId, CancellationToken cancellationToken = default)
    {
        var lease = await db.LeaseContracts
            .AsNoTracking()
            .Include(l => l.Parties)
            .FirstOrDefaultAsync(l => l.Id == leaseId, cancellationToken)
            ?? throw LeaseNotFound(leaseId);
        if (lease.Status != LeaseStatus.Draft)
            throw NotADraftNotFound(leaseId);

        var values = LeaseDraftProjection.ReadValues(lease);
        var evaluation = await EvaluateAsync(lease, values, fullApeCheck: false, cancellationToken);
        var reading = evaluation.Reading;
        return new LeaseDraftView(
            lease.Id,
            lease.DraftVersion,
            lease.Origin,
            lease.WizardMaxStep,
            lease.ReviewConfirmedAt,
            lease.SentForSignatureAt,
            lease.UpdatedAt,
            values,
            new LeaseDraftComputed(reading.Months is null ? null : reading.End?.ToString("yyyy-MM-dd"), reading.Months),
            evaluation.Flow);
    }

    // ---- saving a step -------------------------------------------------------------------------------------------

    public async Task<LeaseDraftSaved> SaveAsync(
        Guid leaseId, int version, int step, JsonElement values, CancellationToken cancellationToken = default)
    {
        await using var transaction = await BeginLockedAsync(leaseId, cancellationToken);
        var lease = await LoadForUpdateAsync(leaseId, includeProperty: false, cancellationToken);
        EnsureDraftForUpdate(lease);
        EnsureVersion(lease, version);

        var current = LeaseDraftProjection.ReadValues(lease);
        var next = LeaseDraftPatch.Apply(current, step, values, Guid.NewGuid);

        // The property can be changed at step 1 only to a property of the caller's org (the web layer has authorized it for the
        // caller): a property that is not visible is the same as one that does not exist.
        if (next.PropertyId is { } propertyId && propertyId != lease.PropertyId)
        {
            if (!await db.Properties.AsNoTracking().AnyAsync(p => p.Id == propertyId, cancellationToken))
                throw PropertyNotFound(propertyId);
            lease.PropertyId = propertyId;
        }

        var evaluation = await EvaluateAsync(lease, next, fullApeCheck: false, cancellationToken);
        var now = UtcNow();

        await RewritePartiesAsync(lease, LeaseDraftProjection.ToRows(next, lease.Id, evaluation.Input.Today), cancellationToken);
        LeaseDraftProjection.ApplyColumns(lease, evaluation.Reading, evaluation.Concordato);

        // The typed state is kept only while something of it cannot live in the columns and the rows.
        lease.DraftData = evaluation.Flow.DataComplete ? null : next.ToJson();
        lease.WizardMaxStep = Math.Max(lease.WizardMaxStep, step);

        var fingerprint = LeaseDraftFingerprint.Compute(next);
        if (lease.ReviewConfirmedAt is not null && lease.ReviewFingerprint != fingerprint)
            ReopenReview(lease, now, "changed");

        lease.DraftVersion++;
        lease.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);

        logger.LogInformation(
            "Lease draft saved. LeaseId={LeaseId} Step={Step} Version={Version} DataComplete={DataComplete}",
            lease.Id, step, lease.DraftVersion, evaluation.Flow.DataComplete);
        return new LeaseDraftSaved(
            lease.DraftVersion,
            FlowOf(evaluation, lease),
            lease.UpdatedAt,
            next.Landlords.Select(p => p.Id).ToList(),
            next.Tenants.Select(p => p.Id).ToList());
    }

    /// <summary>
    /// Makes the rows of the parties the ones the typed values give. The rows are deleted and written again (the entry keeps its id)
    /// in two saves, because the unique index of (lease, role, position) refuses the moment two rows swap places; both saves are
    /// in the transaction of the caller. Nothing is written when the rows are already the right ones.
    /// </summary>
    private async Task RewritePartiesAsync(LeaseContract lease, List<Party> rows, CancellationToken cancellationToken)
    {
        if (LeaseDraftProjection.SameRows(lease.Parties, rows))
            return;

        if (lease.Parties.Count > 0)
        {
            db.Parties.RemoveRange(lease.Parties);
            await db.SaveChangesAsync(cancellationToken);
        }

        db.Parties.AddRange(rows);
    }

    // ---- the review ----------------------------------------------------------------------------------------------

    public async Task<LeaseReviewView> GetReviewAsync(Guid leaseId, CancellationToken cancellationToken = default)
    {
        var lease = await db.LeaseContracts
            .AsNoTracking()
            .Include(l => l.Property)
            .Include(l => l.Parties)
            .FirstOrDefaultAsync(l => l.Id == leaseId, cancellationToken)
            ?? throw LeaseNotFound(leaseId);
        if (lease.Status != LeaseStatus.Draft)
            throw NotADraftNotFound(leaseId);

        var values = LeaseDraftProjection.ReadValues(lease);
        var evaluation = await EvaluateAsync(lease, values, fullApeCheck: true, cancellationToken);
        var checks = LeaseReview.Compute(FlowInput(evaluation, lease), await ReviewFactsAsync(lease, evaluation, cancellationToken));
        return new LeaseReviewView(lease.DraftVersion, lease.ReviewConfirmedAt, checks, Count(checks));
    }

    public async Task<LeaseDraftReviewResult> ConfirmReviewAsync(Guid leaseId, int? version, CancellationToken cancellationToken = default)
    {
        await using var transaction = await BeginLockedAsync(leaseId, cancellationToken);
        var lease = await LoadForUpdateAsync(leaseId, includeProperty: false, cancellationToken);
        EnsureDraftForUpdate(lease);
        EnsureVersion(lease, version);
        if (lease.Origin == LeaseOrigin.Imported)
            throw new DomainRuleException(LeaseDraftErrorCodes.ReviewNotApplicable, "LeaseReviewNotApplicable");

        var values = LeaseDraftProjection.ReadValues(lease);
        var evaluation = await EvaluateAsync(lease, values, fullApeCheck: true, cancellationToken);
        var checks = LeaseReview.Compute(FlowInput(evaluation, lease), new LeaseReviewFacts());
        if (LeaseReview.HasBlock(checks))
            throw new DomainRuleException(LeaseDraftErrorCodes.ReviewBlocked, "LeaseReviewBlocked");

        var fingerprint = LeaseDraftFingerprint.Compute(values);
        if (lease.ReviewConfirmedAt is null || lease.ReviewFingerprint != fingerprint)
        {
            var now = UtcNow();
            lease.ReviewConfirmedAt = now;
            lease.ReviewFingerprint = fingerprint;
            lease.WizardMaxStep = Math.Max(lease.WizardMaxStep, 6);
            lease.DraftVersion++;
            lease.UpdatedAt = now;
            db.LeaseEvents.Add(new LeaseEvent { LeaseContractId = lease.Id, EventType = LeaseEventType.ReviewConfirmed, OccurredAt = now });
            await db.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Lease draft review confirmed. LeaseId={LeaseId} Version={Version}", lease.Id, lease.DraftVersion);
        }

        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);
        return new LeaseDraftReviewResult(lease.DraftVersion, lease.ReviewConfirmedAt, FlowOf(evaluation, lease));
    }

    public async Task<LeaseDraftReviewResult> ReopenReviewAsync(Guid leaseId, int? version, CancellationToken cancellationToken = default)
    {
        await using var transaction = await BeginLockedAsync(leaseId, cancellationToken);
        var lease = await LoadForUpdateAsync(leaseId, includeProperty: false, cancellationToken);
        EnsureDraftForUpdate(lease);
        EnsureVersion(lease, version);

        if (lease.ReviewConfirmedAt is not null)
        {
            var now = UtcNow();
            ReopenReview(lease, now, "withdrawn");
            lease.DraftVersion++;
            lease.UpdatedAt = now;
            await db.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Lease draft review withdrawn. LeaseId={LeaseId} Version={Version}", lease.Id, lease.DraftVersion);
        }

        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);

        var evaluation = await EvaluateAsync(lease, LeaseDraftProjection.ReadValues(lease), fullApeCheck: false, cancellationToken);
        return new LeaseDraftReviewResult(lease.DraftVersion, lease.ReviewConfirmedAt, FlowOf(evaluation, lease));
    }

    /// <summary>
    /// Withdraws the confirmation and, with it, "downloaded for signature": the copy handed out for signature is of a contract the
    /// review no longer stands behind. Stages the changes without saving.
    /// </summary>
    private void ReopenReview(LeaseContract lease, DateTime now, string reason)
    {
        lease.ReviewConfirmedAt = null;
        lease.ReviewFingerprint = null;
        db.LeaseEvents.Add(new LeaseEvent
        {
            LeaseContractId = lease.Id,
            EventType = LeaseEventType.ReviewReopened,
            OccurredAt = now,
            Payload = reason,
        });

        if (lease.SentForSignatureAt is not null)
        {
            lease.SentForSignatureAt = null;
            db.LeaseEvents.Add(new LeaseEvent
            {
                LeaseContractId = lease.Id,
                EventType = LeaseEventType.SentRevoked,
                OccurredAt = now,
                Payload = "review_reopened",
            });
        }
    }

    private async Task<LeaseReviewFacts> ReviewFactsAsync(LeaseContract lease, DraftEvaluation evaluation, CancellationToken cancellationToken)
    {
        var overlapping = 0;
        if (evaluation.Reading is { Start: { } start, End: { } end })
        {
            var from = LeaseDraftRules.ToStoredDate(start);
            var to = LeaseDraftRules.ToStoredDate(end);
            // The other contracts of the property that still stand: not rejected, and not a draft nobody handed out for signature
            // (decision LR-D14: a warning, one can let by rooms).
            overlapping = await db.LeaseContracts
                .AsNoTracking()
                .CountAsync(
                    l => l.PropertyId == lease.PropertyId
                        && l.Id != lease.Id
                        && l.Status != LeaseStatus.Rejected
                        && (l.Status != LeaseStatus.Draft || l.SentForSignatureAt != null)
                        && l.StartDate <= to
                        && l.EndDate >= from,
                    cancellationToken);
        }

        var blocker = lease.Origin == LeaseOrigin.Imported ? null : templates.GetFinalContractBlocker(lease);
        return new LeaseReviewFacts
        {
            OverlappingLeases = overlapping,
            TemplateNotApproved = blocker == Casazen.Infrastructure.External.LeaseContractTemplateService.TemplateNotApprovedCode,
            TemplateDataMissing = blocker == Casazen.Infrastructure.External.LeaseContractTemplateService.DataMissingCode
                && evaluation.Flow.DataComplete,
        };
    }

    private static LeaseReviewCounts Count(IReadOnlyList<LeaseReviewCheck> checks) => new(
        checks.Count(c => c.Severity == LeaseReviewSeverity.Block),
        checks.Count(c => c.Severity == LeaseReviewSeverity.Warn),
        checks.Count(c => c.Severity == LeaseReviewSeverity.Info),
        checks.Count(c => c.Severity == LeaseReviewSeverity.Ok));

    // ---- downloaded for signature --------------------------------------------------------------------------------

    public async Task<LeaseDraftSentResult> MarkSentAsync(Guid leaseId, int? version, CancellationToken cancellationToken = default)
    {
        await using var transaction = await BeginLockedAsync(leaseId, cancellationToken);
        var lease = await LoadForUpdateAsync(leaseId, includeProperty: true, cancellationToken);
        EnsureDraftForUpdate(lease);
        EnsureVersion(lease, version);
        if (lease.Origin == LeaseOrigin.Imported)
            throw new DomainRuleException(LeaseDraftErrorCodes.SentNotApplicable, "LeaseSentNotApplicable");

        var values = LeaseDraftProjection.ReadValues(lease);
        var evaluation = await EvaluateAsync(lease, values, fullApeCheck: true, cancellationToken);
        if (lease.SentForSignatureAt is null)
        {
            if (lease.ReviewConfirmedAt is null)
                throw new DomainRuleException(LeaseDraftErrorCodes.ReviewNotConfirmed, "LeaseReviewNotConfirmed");

            // What was confirmed must still stand: a block that appeared since (the APE was removed) stops the download.
            if (LeaseReview.HasBlock(LeaseReview.Compute(FlowInput(evaluation, lease), new LeaseReviewFacts())))
                throw new DomainRuleException(LeaseDraftErrorCodes.ReviewBlocked, "LeaseReviewBlocked");

            // The gate of GET contract.pdf, so the state never says something that download would refuse (decision LR-D28): the
            // term of the type, then the template of the regime (422 contract_template_not_approved / contract_data_missing).
            LeaseContractTerms.EnsureTerm(lease.ContractType, lease.StartDate, lease.EndDate);
            templates.EnsureFinalContractAvailable(lease);

            var now = UtcNow();
            lease.SentForSignatureAt = now;
            lease.WizardMaxStep = Math.Max(lease.WizardMaxStep, 7);
            lease.DraftVersion++;
            lease.UpdatedAt = now;
            db.LeaseEvents.Add(new LeaseEvent { LeaseContractId = lease.Id, EventType = LeaseEventType.SentForSignature, OccurredAt = now });
            await db.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Lease draft marked as downloaded for signature. LeaseId={LeaseId} Version={Version}", lease.Id, lease.DraftVersion);
        }

        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);
        return new LeaseDraftSentResult(lease.DraftVersion, lease.SentForSignatureAt, FlowOf(evaluation, lease));
    }

    public async Task<LeaseDraftSentResult> RevokeSentAsync(Guid leaseId, int? version, CancellationToken cancellationToken = default)
    {
        await using var transaction = await BeginLockedAsync(leaseId, cancellationToken);
        var lease = await LoadForUpdateAsync(leaseId, includeProperty: false, cancellationToken);
        EnsureDraftForUpdate(lease);
        EnsureVersion(lease, version);

        if (lease.SentForSignatureAt is not null)
        {
            var now = UtcNow();
            lease.SentForSignatureAt = null;
            lease.DraftVersion++;
            lease.UpdatedAt = now;
            db.LeaseEvents.Add(new LeaseEvent
            {
                LeaseContractId = lease.Id,
                EventType = LeaseEventType.SentRevoked,
                OccurredAt = now,
                Payload = "withdrawn",
            });
            await db.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Lease draft download for signature withdrawn. LeaseId={LeaseId} Version={Version}", lease.Id, lease.DraftVersion);
        }

        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);

        var evaluation = await EvaluateAsync(lease, LeaseDraftProjection.ReadValues(lease), fullApeCheck: false, cancellationToken);
        return new LeaseDraftSentResult(lease.DraftVersion, lease.SentForSignatureAt, FlowOf(evaluation, lease));
    }

    // ---- deletion ------------------------------------------------------------------------------------------------

    public async Task DeleteAsync(Guid leaseId, CancellationToken cancellationToken = default)
    {
        await using var transaction = await BeginLockedAsync(leaseId, cancellationToken);
        var lease = await deleter.WithChildren().FirstOrDefaultAsync(l => l.Id == leaseId, cancellationToken)
            ?? throw LeaseNotFound(leaseId);

        var files = await deleter.DeleteAsync(lease, cancellationToken);
        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);

        await deleter.DeleteFilesAsync(files);
        logger.LogInformation("Lease draft deleted. LeaseId={LeaseId} Files={Files}", leaseId, files.Count);
    }

    // ---- evaluation ----------------------------------------------------------------------------------------------

    /// <summary>What the values typed mean for the lease: how they read, the flow they give and the range of the canone concordato.</summary>
    private sealed record DraftEvaluation(
        LeaseDraftReading Reading, LeaseDraftProgressInput Input, LeaseDraftFlow Flow, ConcordatoAssessmentResult? Concordato);

    /// <param name="fullApeCheck">
    /// Read the energy certificate itself (the review, the confirmation, the download for signature) instead of only checking that
    /// the property has one: a certificate can only be uploaded after it was recognised as official, so the existence is enough to
    /// show the steps while the landlord types, and saves nothing to read on every keystroke.
    /// </param>
    private async Task<DraftEvaluation> EvaluateAsync(
        LeaseContract lease, LeaseDraftValues values, bool fullApeCheck, CancellationToken cancellationToken)
    {
        var reading = LeaseDraftRules.Read(values);
        var facts = await LoadFactsAsync(values.PropertyId, fullApeCheck, cancellationToken);

        ConcordatoAssessmentResult? concordato = null;
        LeaseDraftConcordatoOutcome? outcome = null;
        if (facts.PropertyExists
            && reading is { Type: LeaseContractType.Concordato, TypeAvailable: true }
            && values.Concordato is { } characteristics
            && LeaseDraftRules.IsConcordatoValid(characteristics))
        {
            concordato = await _concordato.AssessAsync(
                values.PropertyId!.Value,
                LeaseDraftRules.ToCharacteristics(characteristics),
                new LeaseTerm(LeaseContractTerms.ConcordatoMinimumMonths, 0),
                reading.Rent ?? 0m,
                cancellationToken);
            outcome = concordato.Kind switch
            {
                ConcordatoAssessmentKind.Assessed => new LeaseDraftConcordatoOutcome
                {
                    RentOutOfBand = reading.Rent is not null && !concordato.RentWithinRange,
                    Indicative = concordato.Indicative,
                    MinMonthly = concordato.MinMonthly,
                    MaxMonthly = concordato.MaxMonthly,
                },
                ConcordatoAssessmentKind.Unavailable => new LeaseDraftConcordatoOutcome
                {
                    UnavailableReason = concordato.Reason ?? CanoneConcordatoReasonCodes.DataUnavailable,
                },
                _ => null,
            };
        }

        var input = new LeaseDraftProgressInput
        {
            Origin = lease.Origin,
            Status = lease.Status,
            WizardMaxStep = lease.WizardMaxStep,
            ReviewConfirmedAt = lease.ReviewConfirmedAt,
            SentForSignatureAt = lease.SentForSignatureAt,
            Values = values,
            Facts = facts,
            Concordato = outcome,
            Today = _clock.TodayInRomeAsDateOnly(),
        };
        return new DraftEvaluation(reading, input, LeaseDraftProgress.Compute(input), concordato);
    }

    private async Task<LeaseDraftFacts> LoadFactsAsync(Guid? propertyId, bool fullApeCheck, CancellationToken cancellationToken)
    {
        if (propertyId is not { } id)
            return new LeaseDraftFacts { PropertyExists = false };

        var property = await db.Properties.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (property is null)
            return new LeaseDraftFacts { PropertyExists = false };

        bool hasApe;
        if (fullApeCheck)
        {
            try
            {
                await apeCompliance.EnsurePropertyHasValidApeAsync(id);
                hasApe = true;
            }
            catch (ApeComplianceException)
            {
                hasApe = false;
            }
        }
        else
        {
            hasApe = await db.PropertyDocuments.AsNoTracking().AnyAsync(d => d.PropertyId == id && d.DocumentType == DocumentType.Ape, cancellationToken);
        }

        return new LeaseDraftFacts
        {
            PropertyExists = true,
            HasApe = hasApe,
            HasCadastralData = property.HasCadastralData,
            PropertyIsShortMode = property.RentalMode == RentalMode.Short,
        };
    }

    /// <summary>The input of the flow as the lease is now (the facts of the evaluation, the state fields of the lease).</summary>
    private static LeaseDraftProgressInput FlowInput(DraftEvaluation evaluation, LeaseContract lease) => evaluation.Input with
    {
        Status = lease.Status,
        WizardMaxStep = lease.WizardMaxStep,
        ReviewConfirmedAt = lease.ReviewConfirmedAt,
        SentForSignatureAt = lease.SentForSignatureAt,
    };

    private static LeaseDraftFlow FlowOf(DraftEvaluation evaluation, LeaseContract lease) =>
        LeaseDraftProgress.Compute(FlowInput(evaluation, lease));

    // ---- locking and loading -------------------------------------------------------------------------------------

    /// <summary>
    /// Opens the transaction (PostgreSQL only) and locks the row of the lease until it ends; a second writer waits here and then
    /// reads what the first committed. The change tracker is cleared so nothing read earlier in the request is taken for current.
    /// </summary>
    private async Task<IDbContextTransaction?> BeginLockedAsync(Guid leaseId, CancellationToken cancellationToken)
    {
        IDbContextTransaction? transaction = null;
        if (db.Database.IsRelational() && db.Database.CurrentTransaction is null)
            transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

        try
        {
            if (db.Database.IsNpgsql())
            {
                await db.Database
                    .SqlQuery<Guid>($"""SELECT "Id" AS "Value" FROM "LeaseContracts" WHERE "Id" = {leaseId} FOR UPDATE""")
                    .ToListAsync(cancellationToken);
            }

            db.ChangeTracker.Clear();
            return transaction;
        }
        catch
        {
            if (transaction is not null)
                await transaction.DisposeAsync();
            throw;
        }
    }

    private async Task<LeaseContract> LoadForUpdateAsync(Guid leaseId, bool includeProperty, CancellationToken cancellationToken)
    {
        var query = db.LeaseContracts.Include(l => l.Parties).AsQueryable();
        if (includeProperty)
            query = query.Include(l => l.Property);

        return await query.FirstOrDefaultAsync(l => l.Id == leaseId, cancellationToken) ?? throw LeaseNotFound(leaseId);
    }

    private static void EnsureDraftForUpdate(LeaseContract lease)
    {
        if (lease.Status != LeaseStatus.Draft)
            throw new DomainConflictException(LeaseDraftErrorCodes.NotDraft, "LeaseNotDraft");
    }

    private static void EnsureVersion(LeaseContract lease, int? version)
    {
        if (version is { } sent && sent != lease.DraftVersion)
            throw new DomainConflictException(LeaseDraftErrorCodes.Conflict, "LeaseDraftConflict");
    }

    private DateTime UtcNow() => _clock.GetUtcNow().UtcDateTime;

    private static NotFoundException LeaseNotFound(Guid leaseId) =>
        new($"Lease {leaseId} not found.") { Code = "lease_not_found", MessageKey = "LeaseNotFound" };

    private static NotFoundException NotADraftNotFound(Guid leaseId) =>
        new($"Lease {leaseId} is not a draft.") { Code = LeaseDraftErrorCodes.NotDraft, MessageKey = "LeaseNotDraft" };

    private static NotFoundException PropertyNotFound(Guid propertyId) =>
        new($"Property {propertyId} not found.") { Code = "property_not_found", MessageKey = "PropertyNotFound" };
}
