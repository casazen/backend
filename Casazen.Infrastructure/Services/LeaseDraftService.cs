using System.Data;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Leases;
using Casazen.Core.Leases.Reading;
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

    public async Task<Guid> CreateCfDraftAsync(Guid propertyId, Guid orgId, CancellationToken cancellationToken = default)
    {
        // Idempotent: if a CF draft for this property already exists, return it. A ConcordatoFacile draft is an imported draft (CF-IMPORT):
        // the contract is written and signed by the partner outside our models, so it comes back as a signed contract to import. A draft
        // marked CF before that, with the origin Created, can never be imported: it is not the one to go on with.
        var existing = await db.LeaseContracts
            .AsNoTracking()
            .Where(l => l.PropertyId == propertyId
                && l.OrgId == orgId
                && l.Status == LeaseStatus.Draft
                && l.Origin == LeaseOrigin.Imported
                && l.IsConcordatoFacileSource)
            .Select(l => l.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (existing != default)
        {
            logger.LogInformation("Returning existing CF draft. LeaseId={LeaseId} PropertyId={PropertyId}", existing, propertyId);
            return existing;
        }

        var property = await db.Properties.AsNoTracking().FirstOrDefaultAsync(p => p.Id == propertyId, cancellationToken)
            ?? throw PropertyNotFound(propertyId);

        // The type is the one value the draft knows. The dates and the rent are the starting values of the columns (they read as missing,
        // never as values), and the typed state is kept like in any draft that is not complete, so the readers that skip the incomplete
        // drafts (the agenda, the RLI reminders) skip this one too.
        var values = new LeaseDraftValues { PropertyId = property.Id, ContractType = nameof(LeaseContractType.Concordato) };
        var now = UtcNow();
        var lease = new LeaseContract
        {
            PropertyId = property.Id,
            OrgId = property.OrgId,
            Status = LeaseStatus.Draft,
            StartDate = LeaseDraftDefaults.PlaceholderDate,
            EndDate = LeaseDraftDefaults.PlaceholderDate,
            MonthlyRent = LeaseDraftDefaults.PlaceholderRent,
            WizardMaxStep = LeaseDraftDefaults.FirstStep,
            Origin = LeaseOrigin.Imported,
            DraftVersion = 1,
            DraftData = values.ToJson(),
            IsConcordatoFacileSource = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
        lease.SetContractTerms(LeaseContractType.Concordato, null);

        db.LeaseContracts.Add(lease);
        db.LeaseEvents.Add(new LeaseEvent { LeaseContractId = lease.Id, EventType = LeaseEventType.Created, OccurredAt = now });
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation("CF draft created. LeaseId={LeaseId} PropertyId={PropertyId} OrgId={OrgId}", lease.Id, property.Id, property.OrgId);
        return lease.Id;
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

        await ProjectAsync(lease, next, evaluation, cancellationToken);
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
    /// Writes the typed values where every other reader of the lease looks — the columns and the rows of the parties — and keeps the typed
    /// state only while something of it cannot live there. The one place the projection is applied: a save does it, and so does the
    /// confirmation of the review when the data became complete since the last save.
    /// </summary>
    private Task ProjectAsync(LeaseContract lease, LeaseDraftValues values, DraftEvaluation evaluation, CancellationToken cancellationToken) =>
        LeaseDraftWriter.ProjectAsync(
            db, lease, values, evaluation.Reading, evaluation.Concordato, evaluation.Flow.DataComplete, evaluation.Input.Today, cancellationToken);

    // ---- the review ----------------------------------------------------------------------------------------------

    public async Task<LeaseReviewView> GetReviewAsync(Guid leaseId, CancellationToken cancellationToken = default)
    {
        // The property comes with its energy certificate: the template of the contract prints it, and a lease read without it would
        // always look as if the data of the template were missing.
        var lease = await db.LeaseContracts
            .AsNoTracking()
            .Include(l => l.Property).ThenInclude(p => p.PropertyDocuments.Where(d => d.DocumentType == DocumentType.Ape))
            .Include(l => l.Parties)
            .FirstOrDefaultAsync(l => l.Id == leaseId, cancellationToken)
            ?? throw LeaseNotFound(leaseId);
        if (lease.Status != LeaseStatus.Draft)
            throw NotADraftNotFound(leaseId);

        var values = LeaseDraftProjection.ReadValues(lease);
        var evaluation = await EvaluateAsync(lease, values, fullApeCheck: true, cancellationToken);
        var checks = LeaseReview.Compute(FlowInput(evaluation, lease), ReviewFacts(lease, evaluation));
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

        // LR-OVERLAP: the contract takes its days only if no other lease of the property has them (409 lease_period_overlaps).
        await EnsurePeriodFreeAsync(lease, evaluation, cancellationToken);

        // The data may have become complete without a save (the agreement data of the comune were loaded, a date typed ahead of today has
        // come): the columns and the rows are written now, as a save would, so what is confirmed is what every other reader sees and
        // the draft stops looking like one that is still being typed.
        var projected = false;
        if (lease.DraftData is not null && evaluation.Flow.DataComplete)
        {
            await ProjectAsync(lease, values, evaluation, cancellationToken);
            projected = true;
        }

        var fingerprint = LeaseDraftFingerprint.Compute(values);
        var confirms = lease.ReviewConfirmedAt is null || lease.ReviewFingerprint != fingerprint;
        if (projected || confirms)
        {
            var now = UtcNow();
            if (confirms)
            {
                lease.ReviewConfirmedAt = now;
                lease.ReviewFingerprint = fingerprint;
                lease.WizardMaxStep = Math.Max(lease.WizardMaxStep, 6);
                db.LeaseEvents.Add(new LeaseEvent { LeaseContractId = lease.Id, EventType = LeaseEventType.ReviewConfirmed, OccurredAt = now });
            }

            lease.DraftVersion++;
            lease.UpdatedAt = now;
            await db.SaveChangesAsync(cancellationToken);
            logger.LogInformation(
                "Lease draft review confirmed. LeaseId={LeaseId} Version={Version} Confirmed={Confirmed} Projected={Projected}",
                lease.Id, lease.DraftVersion, confirms, projected);
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

    private LeaseReviewFacts ReviewFacts(LeaseContract lease, DraftEvaluation evaluation)
    {
        var blocker = lease.Origin == LeaseOrigin.Imported ? null : templates.GetFinalContractBlocker(lease);
        return new LeaseReviewFacts
        {
            // LR-OVERLAP: the lease in the way, found while the draft was evaluated (the rule of the days of LeasePeriodRules).
            PeriodConflict = evaluation.PeriodConflict,
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

            // LR-OVERLAP: nor can it go to the signature if another lease has taken the days since (409 lease_period_overlaps).
            await EnsurePeriodFreeAsync(lease, evaluation, cancellationToken);

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

    // ---- filled from the contract the owner uploaded (NL-DOCS) ----------------------------------------------------

    public async Task<LeaseDraftFillResult> ApplyContractReadingAsync(
        Guid leaseId, LeaseContractReading reading, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reading);

        // The same road as a save: the row lock, the lease read after it, the evaluation of the values and the one writer of the columns
        // and the parties. What is read is merged into the values as they are NOW, under the lock: nothing typed since the reading started is lost.
        await using var transaction = await BeginLockedAsync(leaseId, cancellationToken);
        var lease = await LoadForUpdateAsync(leaseId, includeProperty: false, cancellationToken);
        EnsureDraftForUpdate(lease);
        if (lease.Origin != LeaseOrigin.Imported)
            return new LeaseDraftFillResult(false, false, lease.DraftVersion, []);

        var fill = LeaseDraftContractFill.Merge(LeaseDraftProjection.ReadValues(lease), reading, lease.Origin, Guid.NewGuid);
        if (fill.Changed)
        {
            var evaluation = await EvaluateAsync(lease, fill.Values, fullApeCheck: false, cancellationToken);
            await ProjectAsync(lease, fill.Values, evaluation, cancellationToken);
            lease.DraftVersion++;
            lease.UpdatedAt = UtcNow();
            await db.SaveChangesAsync(cancellationToken);
        }

        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);

        logger.LogInformation(
            "Lease draft filled from a contract document. LeaseId={LeaseId} Version={Version} Read={Read} Conflicts={Conflicts} Unusable={Unusable}",
            lease.Id,
            lease.DraftVersion,
            fill.Fields.Count(f => f.State == FieldReadingState.Read),
            fill.Fields.Count(f => f.State == FieldReadingState.Conflict),
            fill.Fields.Count(f => f.State == FieldReadingState.Unusable));
        return new LeaseDraftFillResult(true, fill.Changed, lease.DraftVersion, fill.Fields);
    }

    // ---- evaluation ----------------------------------------------------------------------------------------------

    /// <summary>
    /// What the values typed mean for the lease: how they read, the flow they give, the range of the canone concordato and the other lease
    /// of the property that covers some of the same days (LR-OVERLAP, only a warning here: <see cref="EnsurePeriodFreeAsync"/> is the block).
    /// </summary>
    private sealed record DraftEvaluation(
        LeaseDraftReading Reading,
        LeaseDraftProgressInput Input,
        LeaseDraftFlow Flow,
        ConcordatoAssessmentResult? Concordato,
        LeasePeriodConflict? PeriodConflict);

    /// <param name="fullApeCheck">
    /// Read the energy certificate itself (the review, the confirmation, the download for signature) instead of only checking that
    /// the property has one: a certificate can only be uploaded after it was recognised as official, so the existence is enough to
    /// show the steps while the landlord types, and saves nothing to read on every keystroke.
    /// </param>
    private async Task<DraftEvaluation> EvaluateAsync(
        LeaseContract lease, LeaseDraftValues values, bool fullApeCheck, CancellationToken cancellationToken)
    {
        var reading = LeaseDraftRules.Read(values, lease.Origin);
        var facts = await LoadFactsAsync(values.PropertyId, fullApeCheck, cancellationToken);

        // The range of a canone concordato is for a contract that is going to be signed: an imported one is already signed, so
        // nothing is asked of the calculator and no range is stored (decision LR-D13).
        ConcordatoAssessmentResult? concordato = null;
        LeaseDraftConcordatoOutcome? outcome = null;
        if (lease.Origin != LeaseOrigin.Imported
            && facts.PropertyExists
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

        // LR-OVERLAP, the soft warning: once the property and both dates are there, the lease that has some of the same days (no lock: it is
        // told, not enforced; the steps that make this lease a contract check again under the lock of the property).
        LeasePeriodConflict? conflict = null;
        if (facts.PropertyExists && values.PropertyId is { } propertyId && reading is { Start: { } start, End: { } end })
        {
            conflict = await LeasePeriodGuard.FindAsync(
                db, propertyId, lease.Id, LeaseDraftRules.ToStoredDate(start), LeaseDraftRules.ToStoredDate(end), cancellationToken);
        }

        return new DraftEvaluation(reading, input, LeaseDraftProgress.Compute(input) with { PeriodConflict = conflict }, concordato, conflict);
    }

    /// <summary>
    /// LR-OVERLAP, the hard rule: 409 <c>lease_period_overlaps</c> when another lease of the property covers some of the days of this one,
    /// asked under the lock of the property, in the transaction of the step that makes the lease a contract. Nothing to check while the
    /// property or a date is missing (the data steps say so).
    /// </summary>
    private Task EnsurePeriodFreeAsync(LeaseContract lease, DraftEvaluation evaluation, CancellationToken cancellationToken) =>
        evaluation.Reading is { Start: { } start, End: { } end }
            ? LeasePeriodGuard.EnsureFreeAsync(
                db, lease.PropertyId, lease.Id, LeaseDraftRules.ToStoredDate(start), LeaseDraftRules.ToStoredDate(end), cancellationToken)
            : Task.CompletedTask;

    private async Task<LeaseDraftFacts> LoadFactsAsync(Guid? propertyId, bool fullApeCheck, CancellationToken cancellationToken)
    {
        if (propertyId is not { } id)
            return new LeaseDraftFacts { PropertyExists = false };

        var property = await db.Properties.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (property is null)
            return new LeaseDraftFacts { PropertyExists = false };

        // Code and energy class of the certificates of the property: the contract prints the last one that has them (LeaseContractDocument),
        // so the draft needs one that has them, whatever the reading of the file said (LR-APE).
        var apes = await db.PropertyDocuments
            .AsNoTracking()
            .Where(d => d.PropertyId == id && d.DocumentType == DocumentType.Ape)
            .Select(d => new { d.ApeCode, d.ApeEnergyClass })
            .ToListAsync(cancellationToken);
        var hasApeIdentification = apes.Any(d => PropertyDocument.IsApeIdentified(d.ApeCode, d.ApeEnergyClass));

        bool hasApe;
        if (fullApeCheck)
        {
            try
            {
                await apeCompliance.EnsurePropertyHasValidApeAsync(id);
                hasApe = true;
            }
            catch (ApeComplianceException ex)
            {
                // A certificate that is there but could not be read whole asks for its code and class: that is the need of the
                // identification, not "the APE is missing" (there is nothing to upload).
                hasApe = ex.Code == ApeComplianceException.IdentificationRequiredCode;
            }
        }
        else
        {
            hasApe = apes.Count > 0;
        }

        return new LeaseDraftFacts
        {
            PropertyExists = true,
            HasApe = hasApe,
            HasApeIdentification = hasApeIdentification,
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
        LeaseDraftProgress.Compute(FlowInput(evaluation, lease)) with { PeriodConflict = evaluation.PeriodConflict };

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
            await LeaseDraftLocking.LockRowAsync(db, leaseId, cancellationToken);
            db.ChangeTracker.Clear();
            return transaction;
        }
        catch (Exception ex) when (LeaseDraftLocking.IsContention(ex))
        {
            // Somebody else holds the draft for longer than a few seconds, or the database stopped this change in a race: the same answer
            // as an old version (nothing was written), not a timeout of the connection.
            if (transaction is not null)
                await transaction.DisposeAsync();
            logger.LogInformation("Lease draft busy: the change was refused. LeaseId={LeaseId}", leaseId);
            throw new DomainConflictException(LeaseDraftErrorCodes.Conflict, "LeaseDraftConflict");
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
        {
            // With its energy certificate, which the template of the contract prints (see GetReviewAsync).
            query = query.Include(l => l.Property).ThenInclude(p => p.PropertyDocuments.Where(d => d.DocumentType == DocumentType.Ape));
        }

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
