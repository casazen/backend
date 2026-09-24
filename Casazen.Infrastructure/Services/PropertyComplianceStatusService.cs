using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Options;
using Casazen.Core.Regulatory;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Email.Templates;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// Single evaluation of the activation blockers and of the compliance status of a property (CO-06, A5-20, A5-36); rules
/// and transitions in <see cref="IPropertyComplianceStatusService"/>, operations in
/// <c>docs/runbooks/compliance.md</c>.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Every transition of one property runs under the PostgreSQL advisory lock
/// <see cref="PostgresAdvisoryLocks.Scope.PropertyComplianceStatus"/> and reads the row again inside it: two concurrent
/// evaluations (a host request and the nightly job) suspend the property once and email the host once.</item>
/// <item>The email is queued (<see cref="IEmailQueue"/>, FD-13) after the commit, to <c>Org.ContactEmail</c>.</item>
/// <item>The nightly run holds the session lock <see cref="PostgresAdvisoryLocks.Scope.PropertyComplianceCheckRun"/>
/// on top of Hangfire's own lock, so the one-shot command and the job never run together.</item>
/// </list>
/// </remarks>
public class PropertyComplianceStatusService(
    AppDbContext db,
    IConfiguration configuration,
    IEmailQueue emailQueue,
    PublicSiteLinks links,
    IOptions<ComplianceOptions> complianceOptions,
    ILogger<PropertyComplianceStatusService> logger,
    TimeProvider? timeProvider = null) : IPropertyComplianceStatusService
{
    private const string RunLockKey = "property-compliance-check";

    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<IReadOnlyList<ComplianceActivationStep>> GetBlockingStepsAsync(
        Property property,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(property);

        var cinStatus = CinComplianceRules.ResolveStatus(property.CinCode);
        var cinGuidanceUrl = configuration["Compliance:CinGuidanceUrl"]
            ?? ComplianceOptions.DefaultCinGuidanceUrl;

        // Bedrooms are not checked: 0 is a studio flat (monolocale, A2-27).
        var baseComplete = !string.IsNullOrWhiteSpace(property.Name)
            && !string.IsNullOrWhiteSpace(property.Address)
            && !string.IsNullOrWhiteSpace(property.City)
            && property.MaxGuests > 0
            && property.NightlyRate > 0;

        var requiredDocs = ResolveRequiredDocuments(property);
        var uploadedTypes = (await db.PropertyDocuments
                .AsNoTracking()
                .Where(d => d.PropertyId == property.Id)
                .Select(d => d.DocumentType)
                .ToListAsync(cancellationToken))
            .Select(type => type.ToString())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missingDocs = requiredDocs.Where(d => !uploadedTypes.Contains(d)).ToList();
        var docsComplete = missingDocs.Count == 0;

        // D.L. 145/2023 art. 13-ter (CO-07): only the required items of the checklist block.
        var safetyChecklist = await db.PropertySafetyChecklists
            .AsNoTracking()
            .Include(c => c.Items)
            .FirstOrDefaultAsync(c => c.PropertyId == property.Id, cancellationToken);
        var safety = SafetyChecklistRules.Evaluate(safetyChecklist);
        var safetyComplete = safety.IsComplete;

        return
        [
            new ComplianceActivationStep(
                "base-data",
                "Dati base proprietà",
                baseComplete ? "complete" : "pending",
                true,
                baseComplete ? null : "Completa nome, indirizzo, città e tariffe")
            {
                Blockers = baseComplete ? [] : [new("activation_base_data_incomplete", "ActivationBaseDataIncomplete")],
            },
            new ComplianceActivationStep(
                "cin",
                "Codice CIN",
                cinStatus == "valid" ? "complete" : "pending",
                true,
                cinStatus == "valid" ? null : cinStatus == "missing"
                    ? $"Inserisci il CIN (guida: {cinGuidanceUrl})"
                    : $"Formato CIN non valido (guida: {cinGuidanceUrl})")
            {
                LinkUrl = cinGuidanceUrl,
                Blockers = cinStatus switch
                {
                    "valid" => [],
                    "missing" => [new("activation_cin_missing", "ActivationCinMissing")],
                    _ => [new("activation_cin_invalid", "ActivationCinInvalid")],
                },
            },
            new ComplianceActivationStep(
                "documents",
                "Documenti richiesti",
                docsComplete ? "complete" : "pending",
                true,
                docsComplete ? null : $"Documenti mancanti: {string.Join(", ", missingDocs)}")
            {
                Blockers = docsComplete
                    ? []
                    : [new("activation_documents_missing", "ActivationDocumentsMissing", [string.Join(", ", missingDocs)])],
            },
            new ComplianceActivationStep(
                "safety",
                "Checklist sicurezza",
                safetyComplete ? "complete" : "pending",
                true)
            {
                MessageKey = safetyComplete ? null : "ActivationSafetyIncomplete",
                MessageArgs = safetyComplete ? null : [safety.Blockers.Count],
                Blockers = safety.Blockers.Select(b => new ActivationBlocker(b.Code, b.MessageKey, b.MessageArgs)).ToList(),
            },
        ];
    }

    public Task<PropertyComplianceCheck> ReevaluateAsync(Guid propertyId, CancellationToken cancellationToken = default) =>
        EvaluateAsync(propertyId, activate: false, cancellationToken);

    public Task<PropertyComplianceCheck> ActivateAsync(Guid propertyId, CancellationToken cancellationToken = default) =>
        EvaluateAsync(propertyId, activate: true, cancellationToken);

    public async Task<PropertyComplianceRecalculation?> RecalculateAllAsync(
        bool dryRun,
        CancellationToken cancellationToken = default)
    {
        await using var runLock = await PostgresAdvisoryLocks.TryAcquireSessionLockAsync(
            db, PostgresAdvisoryLocks.Scope.PropertyComplianceCheckRun, RunLockKey, cancellationToken);
        if (runLock is null)
        {
            logger.LogInformation("Property compliance check skipped: another run is in progress");
            return null;
        }

        // Background run: no tenant context, every org's properties. Only an active property (suspension) or a suspended
        // one (reactivation) can change here; a pending property waits for the host's activation.
        var propertyIds = await db.Properties
            .AsNoTracking()
            .Where(p => p.ComplianceStatus == PropertyComplianceStatus.Active
                        || p.ComplianceStatus == PropertyComplianceStatus.Suspended)
            .OrderBy(p => p.Id)
            .Select(p => p.Id)
            .ToListAsync(cancellationToken);

        int suspended = 0, reactivated = 0, notified = 0, failed = 0;
        var byBlocker = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var propertyId in propertyIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var outcome = dryRun
                    ? await PreviewAsync(propertyId, cancellationToken)
                    : await ApplyAsync(propertyId, cancellationToken);
                if (outcome.Reactivates)
                {
                    reactivated++;
                    continue;
                }

                if (!outcome.Suspends)
                    continue;

                suspended++;
                if (outcome.Notifies)
                    notified++;
                foreach (var code in outcome.Codes)
                    byBlocker[code] = byBlocker.GetValueOrDefault(code) + 1;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                logger.LogError(ex, "Compliance check of property {PropertyId} failed; continuing with the next property", propertyId);
            }
            finally
            {
                db.ChangeTracker.Clear();
            }
        }

        var report = new PropertyComplianceRecalculation(
            dryRun, propertyIds.Count, suspended, reactivated, notified, failed, byBlocker);
        logger.LogInformation(
            "Property compliance check {Mode}: {Checked} active or suspended properties checked, {Suspended} suspended, {Reactivated} reactivated, {Notified} hosts notified, {Failed} failed, blockers {Blockers}",
            dryRun ? "(dry run, nothing changed)" : "completed",
            report.Checked,
            report.Suspended,
            report.Reactivated,
            report.HostsNotified,
            report.Failed,
            string.Join(", ", byBlocker.Select(b => $"{b.Key}={b.Value}")));
        return report;
    }

    private sealed record Outcome(bool Suspends, bool Reactivates, bool Notifies, IReadOnlyList<string> Codes)
    {
        public static readonly Outcome None = new(false, false, false, []);
    }

    private async Task<Outcome> PreviewAsync(Guid propertyId, CancellationToken cancellationToken)
    {
        var property = await db.Properties.AsNoTracking().FirstOrDefaultAsync(p => p.Id == propertyId, cancellationToken);
        if (property is null || property.ComplianceStatus == PropertyComplianceStatus.Pending)
            return Outcome.None;

        var incomplete = Incomplete(await GetBlockingStepsAsync(property, cancellationToken));
        return property.ComplianceStatus switch
        {
            PropertyComplianceStatus.Active when incomplete.Count > 0 => new Outcome(
                true, false, ShouldNotify(firstCheck: property.ComplianceCheckedAt is null), BlockerCodes(incomplete)),
            PropertyComplianceStatus.Suspended when incomplete.Count == 0 => new Outcome(false, true, false, []),
            _ => Outcome.None,
        };
    }

    private async Task<Outcome> ApplyAsync(Guid propertyId, CancellationToken cancellationToken)
    {
        var check = await EvaluateAsync(propertyId, activate: false, cancellationToken);
        return new Outcome(check.Suspended, check.Reactivated, check.HostNotified, BlockerCodes(check.IncompleteSteps));
    }

    private async Task<PropertyComplianceCheck> EvaluateAsync(
        Guid propertyId,
        bool activate,
        CancellationToken cancellationToken)
    {
        Property property;
        PropertyComplianceStatus previous;
        IReadOnlyList<ComplianceActivationStep> incomplete = [];
        var notify = false;
        await using (var transaction = await PostgresAdvisoryLocks.BeginLockedTransactionAsync(
                         db, cancellationToken, (PostgresAdvisoryLocks.Scope.PropertyComplianceStatus, propertyId.ToString("N"))))
        {
            property = await LoadCurrentAsync(propertyId, cancellationToken)
                ?? throw new NotFoundException($"Property {propertyId} not found");
            previous = property.ComplianceStatus;

            // A pending property is published only by the host's activation (terms accepted), never by a re-evaluation.
            if (!activate && previous == PropertyComplianceStatus.Pending)
                return new PropertyComplianceCheck(propertyId, previous, previous, [], false);

            incomplete = Incomplete(await GetBlockingStepsAsync(property, cancellationToken));
            var now = _clock.GetUtcNow().UtcDateTime;
            var firstCheck = property.ComplianceCheckedAt is null;

            if (incomplete.Count == 0)
            {
                // The activation, or the reactivation of a suspended property whose requirements are complete again.
                if (activate || previous == PropertyComplianceStatus.Suspended)
                {
                    property.ComplianceStatus = PropertyComplianceStatus.Active;
                    property.ComplianceCompletedAt = now;
                    property.ComplianceSuspendedAt = null;
                    property.ComplianceSuspensionReasons = null;
                    property.UpdatedAt = now;
                }
            }
            else if (previous == PropertyComplianceStatus.Active)
            {
                property.ComplianceStatus = PropertyComplianceStatus.Suspended;
                property.ComplianceSuspendedAt = now;
                property.ComplianceSuspensionReasons = BlockerCodes(incomplete).ToList();
                property.UpdatedAt = now;
                notify = ShouldNotify(firstCheck);
            }

            property.ComplianceCheckedAt = now;
            await db.SaveChangesAsync(cancellationToken);
            if (transaction is not null)
                await transaction.CommitAsync(cancellationToken);
        }

        if (property.ComplianceStatus != previous)
        {
            logger.LogInformation(
                "Property {PropertyId} compliance status {Previous} -> {Status} (blockers: {Blockers})",
                propertyId,
                previous,
                property.ComplianceStatus,
                string.Join(", ", BlockerCodes(incomplete)));
        }

        var notified = notify && await NotifyHostAsync(property, incomplete, cancellationToken);
        return new PropertyComplianceCheck(propertyId, previous, property.ComplianceStatus, incomplete, notified);
    }

    /// <summary>
    /// The row as it is in the database now (inside the lock): a copy already tracked by the caller's context is
    /// reloaded, since it may hold the values read before another request changed them.
    /// </summary>
    private async Task<Property?> LoadCurrentAsync(Guid propertyId, CancellationToken cancellationToken)
    {
        var tracked = db.Properties.Local.FirstOrDefault(p => p.Id == propertyId);
        if (tracked is null)
            return await db.Properties.FirstOrDefaultAsync(p => p.Id == propertyId, cancellationToken);

        await db.Entry(tracked).ReloadAsync(cancellationToken);
        return db.Entry(tracked).State == EntityState.Detached ? null : tracked;
    }

    private bool ShouldNotify(bool firstCheck) =>
        !firstCheck || complianceOptions.Value.StatusCheck.NotifyOnFirstCheck;

    private async Task<bool> NotifyHostAsync(
        Property property,
        IReadOnlyList<ComplianceActivationStep> incomplete,
        CancellationToken cancellationToken)
    {
        try
        {
            // Orgs carry no tenant filter; the org is the property's own.
            var contactEmail = await db.Orgs
                .AsNoTracking()
                .Where(o => o.Id == property.OrgId)
                .Select(o => o.ContactEmail)
                .FirstOrDefaultAsync(cancellationToken);

            // Validated at startup outside Development/Testing (FD-13); without it the email has no button.
            var activationUrl = links.IsConfigured ? links.HostPropertyActivation(property.Id) : null;
            var email = EmailTemplates.PropertyComplianceSuspended(
                EmailTemplates.DefaultCulture,
                property.Name,
                incomplete.Select(s => s.Id).ToList(),
                activationUrl);

            if (emailQueue.Enqueue(contactEmail, email, EmailTemplates.Names.PropertyComplianceSuspended))
                return true;

            logger.LogWarning(
                "Suspension email of property {PropertyId} not queued (org {OrgId})", property.Id, property.OrgId);
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The suspension is committed: a failed email never undoes it (the host sees it in the console).
            logger.LogError(ex, "Suspension email of property {PropertyId} could not be prepared", property.Id);
            return false;
        }
    }

    private static List<ComplianceActivationStep> Incomplete(IEnumerable<ComplianceActivationStep> steps) =>
        steps.Where(s => s.Blocker && s.Status != "complete").ToList();

    private static IReadOnlyList<string> BlockerCodes(IEnumerable<ComplianceActivationStep> steps) =>
        steps.SelectMany(s => s.Blockers.Select(b => b.Code)).Distinct(StringComparer.Ordinal).ToList();

    /// <summary>
    /// Documents required by <c>Compliance:RequiredDocuments</c>. The keys are region codes but are still compared with
    /// the city (A5-19, open in SU-04), so the <c>default</c> list applies in practice.
    /// </summary>
    private IReadOnlyList<string> ResolveRequiredDocuments(Property property)
    {
        var section = configuration.GetSection("Compliance:RequiredDocuments");
        var regionCode = section.GetChildren()
            .Select(c => c.Key)
            .FirstOrDefault(k => k.Equals(property.City, StringComparison.OrdinalIgnoreCase));

        regionCode ??= section.GetChildren()
            .Select(c => c.Key)
            .FirstOrDefault(k => k.Equals("default", StringComparison.OrdinalIgnoreCase))
            ?? "default";

        var docs = section.GetSection(regionCode).Get<string[]>();
        // No safety certificate is required by D.L. 145/2023 art. 13-ter: proofs are optional on the checklist (CO-07).
        return docs is { Length: > 0 } ? docs : ["CinCertificate"];
    }
}
