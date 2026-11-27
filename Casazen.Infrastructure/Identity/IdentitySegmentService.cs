using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
using Casazen.Core.Identity;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Identity;

/// <inheritdoc cref="IIdentitySegmentService" />
/// <remarks>
/// <para><b>The conditions of the automatic approval</b>, checked in this order, the first that fails is the reason (<see cref="IdentitySegmentReasonCodes"/>): the platform
/// permission (<c>CheckInIdentityAutoApproval</c> with <c>CheckInIdentitySelfHosted</c> and <c>CheckInIdentityVerification</c>, all three on) → a segment in
/// <see cref="IdentitySegmentMode.Automatic"/> (none, or observation: <c>segment_observation</c>; suspended: <c>segment_suspended</c>) → a qualification recorded → the
/// qualification equal to the policy version, the canonical policy hash and the weights hash in force (<see cref="IdentityConfigurationFingerprint"/>; models not loaded is no
/// match) → a calibrated policy → a usable policy → the format giving, on the channel, the assurance the segment requires (<see cref="DocumentFormatRegistry.AssuranceFor"/>,
/// <c>consistency</c> below <c>cryptographic</c>). Nothing is written to follow a new policy or new weights: the comparison is made at every resolution.</para>
/// <para><b>No decision number here</b>: the thresholds live in the policy file, the segment only says what the policy may do. The tenant filter of the context is lifted
/// by name: every query names the org.</para>
/// <para><b>Privacy</b>: the rows hold ids, codes, versions, hashes and times; the reason of a suspension is the staff's text and is never logged.</para>
/// </remarks>
public sealed class IdentitySegmentService(
    AppDbContext db,
    IdentityPolicyProvider policies,
    IdentityConfigurationFingerprint fingerprints,
    DocumentFormatRegistry formats,
    IFeatureFlags featureFlags,
    TimeProvider clock,
    ILogger<IdentitySegmentService> logger,
    IActivityLog? activityLog = null) : IIdentitySegmentService
{
    public async Task<IdentitySegmentOverview> ListAsync(Guid orgId, CancellationToken cancellationToken = default)
    {
        var rows = await db.IdentitySegmentSettings
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(s => s.OrgId == orgId)
            .ToListAsync(cancellationToken);
        var names = await PropertyNamesAsync(orgId, rows, cancellationToken);
        var configuration = Current();
        var segments = rows
            .Select(row => ViewOf(row, names, configuration))
            .OrderBy(view => view.PropertyId is null ? 0 : 1)
            .ThenBy(view => view.PropertyName ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(view => view.FormatId, StringComparer.Ordinal)
            .ThenBy(view => view.Channel, StringComparer.Ordinal)
            .ToList();

        return new IdentitySegmentOverview(
            segments,
            formats.All.Select(format => new IdentitySegmentFormat(format.Id, format.Label)).ToList(),
            IdentityChannels.All,
            configuration.Policy.IsReady ? configuration.Policy.Policy.Version : null,
            configuration.Policy.Provisional,
            configuration.AutoApprovalEnabled);
    }

    public async Task<IdentitySegmentView> CreateAsync(
        Guid orgId,
        Guid? propertyId,
        string? formatId,
        string? channel,
        string? actorUserId,
        CancellationToken cancellationToken = default)
    {
        var format = formats.Get(formatId) ?? throw Invalid();
        var code = channel?.Trim();
        if (code is null || !IdentityChannels.All.Contains(code, StringComparer.Ordinal))
            throw Invalid();

        // A property of the org, not deleted: another org's property, or one that does not exist, is the same answer.
        if (propertyId is { } property
            && !await db.Properties.AsNoTracking().IgnoreQueryFilters().AnyAsync(p => p.Id == property && p.OrgId == orgId && !p.IsDeleted, cancellationToken))
        {
            throw Invalid();
        }

        var existing = await FindByKeyAsync(orgId, propertyId, format.Id, code, cancellationToken);
        if (existing is not null)
            return await ViewAsync(existing, cancellationToken);

        var now = UtcNow();
        var row = new IdentitySegmentSetting
        {
            OrgId = orgId,
            PropertyId = propertyId,
            FormatId = format.Id,
            Channel = code,
            Mode = IdentitySegmentMode.Observation,
            RequiredAssurance = IdentityAssuranceLevels.Consistency,
            CreatedAt = now,
            UpdatedAt = now,
            UpdatedByUserId = Cut(actorUserId),
        };
        db.IdentitySegmentSettings.Add(row);
        Record(orgId, actorUserId);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Two first saves of one key at the same time: the unique index kept one, and that one is the answer. Anything else is not this race and goes up.
            db.Entry(row).State = EntityState.Detached;
            var winner = await FindByKeyAsync(orgId, propertyId, format.Id, code, cancellationToken);
            if (winner is null)
                throw;

            return await ViewAsync(winner, cancellationToken);
        }

        logger.LogInformation(
            "Identity segment {SegmentId} of org {OrgId} created by {UserId}: format={FormatId} channel={Channel} property={PropertyId} mode=observation",
            row.Id, orgId, actorUserId ?? "none", row.FormatId, row.Channel, propertyId?.ToString() ?? "all");
        return await ViewAsync(row, cancellationToken);
    }

    public async Task<IdentitySegmentView?> SetModeAsync(
        Guid orgId,
        Guid segmentId,
        IdentitySegmentMode mode,
        string? reason,
        string? actorUserId,
        CancellationToken cancellationToken = default)
    {
        var row = await FindAsync(orgId, segmentId, cancellationToken);
        if (row is null)
            return null;

        string? suspendedReason = null;
        switch (mode)
        {
            case IdentitySegmentMode.Automatic:
                // Automatic is a claim about the policy and the weights in force: only a qualification for them makes it.
                if (QualificationReasonOf(row, Current()) is not null)
                    throw new DomainRuleException(IdentityApiErrors.IdentitySegmentNotQualified, "IdentitySegmentNotQualified");

                break;
            case IdentitySegmentMode.Suspended:
                suspendedReason = reason?.Trim();
                if (string.IsNullOrEmpty(suspendedReason) || suspendedReason.Length > IdentitySegmentRules.SuspendedReasonMaxLength)
                    throw new DomainRuleException(IdentityApiErrors.IdentitySegmentInvalid, "IdentitySegmentReasonRequired", IdentitySegmentRules.SuspendedReasonMaxLength);

                break;
            case IdentitySegmentMode.Observation:
                break;
            default:
                throw Invalid();
        }

        if (row.Mode == mode && string.Equals(row.SuspendedReason, suspendedReason, StringComparison.Ordinal))
            return await ViewAsync(row, cancellationToken);

        var previous = row.Mode;
        row.Mode = mode;
        row.SuspendedReason = suspendedReason;
        Touch(row, actorUserId);
        Record(orgId, actorUserId);
        await db.SaveChangesAsync(cancellationToken);

        // The mode and who; never the text of a suspension.
        logger.LogInformation(
            "Identity segment {SegmentId} of org {OrgId}: mode {Previous} -> {Mode} by {UserId}",
            row.Id, orgId, IdentitySegmentModeCodes.CodeOf(previous), IdentitySegmentModeCodes.CodeOf(mode), actorUserId ?? "none");
        return await ViewAsync(row, cancellationToken);
    }

    public async Task<IdentitySegmentView?> QualifyAsync(
        Guid orgId,
        Guid segmentId,
        string? policyVersion,
        string? policyHash,
        string? weightsHash,
        string? reportSha256,
        string? requiredAssurance,
        string adminUserId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(adminUserId);

        var row = await FindAsync(orgId, segmentId, cancellationToken);
        if (row is null)
            return null;

        var version = policyVersion?.Trim();
        var assurance = requiredAssurance?.Trim();
        if (string.IsNullOrEmpty(version)
            || !IsSha256(policyHash) || !IsSha256(weightsHash) || !IsSha256(reportSha256)
            || assurance is null || !IdentityAssuranceLevels.All.Contains(assurance, StringComparer.Ordinal))
        {
            throw new DomainRuleException(IdentityApiErrors.IdentitySegmentInvalid, "IdentitySegmentQualificationInvalid");
        }

        var configuration = Current();
        if (!configuration.Policy.IsReady || configuration.Fingerprint is not { } fingerprint)
            throw new DomainRuleException(IdentityApiErrors.IdentitySegmentInvalid, "IdentitySegmentFingerprintUnavailable");

        // A provisional policy is never qualified: nothing is approved automatically until a calibrated policy file is committed (NF-194).
        if (configuration.Policy.Provisional)
            throw new DomainRuleException(IdentityApiErrors.IdentitySegmentInvalid, "IdentitySegmentPolicyProvisional");

        if (!string.Equals(version, fingerprint.PolicyVersion, StringComparison.Ordinal)
            || !SameHash(policyHash, fingerprint.PolicyHash)
            || !SameHash(weightsHash, fingerprint.WeightsHash))
        {
            throw new DomainRuleException(IdentityApiErrors.IdentitySegmentInvalid, "IdentitySegmentQualificationMismatch");
        }

        var now = UtcNow();
        row.QualifiedPolicyVersion = fingerprint.PolicyVersion;
        row.QualifiedPolicyHash = fingerprint.PolicyHash;
        row.QualifiedWeightsHash = fingerprint.WeightsHash;
        row.QualificationReportSha256 = reportSha256!.Trim().ToLowerInvariant();
        row.QualifiedAt = now;
        row.QualifiedByUserId = Cut(adminUserId);
        row.RequiredAssurance = assurance;
        Touch(row, adminUserId);
        Record(orgId, adminUserId);
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Identity segment {SegmentId} of org {OrgId} qualified by {UserId}: policy={PolicyVersion} policyHash={PolicyHash} weightsHash={WeightsHash} assurance={Assurance}",
            row.Id, orgId, adminUserId, row.QualifiedPolicyVersion, row.QualifiedPolicyHash, row.QualifiedWeightsHash, row.RequiredAssurance);
        return await ViewAsync(row, cancellationToken);
    }

    public async Task<IdentitySegmentView?> RevokeQualificationAsync(Guid orgId, Guid segmentId, string adminUserId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(adminUserId);

        var row = await FindAsync(orgId, segmentId, cancellationToken);
        if (row is null)
            return null;

        if (!HasQualification(row) && row.QualificationReportSha256 is null && row.QualifiedAt is null && row.Mode != IdentitySegmentMode.Automatic)
            return await ViewAsync(row, cancellationToken);

        row.QualifiedPolicyVersion = null;
        row.QualifiedPolicyHash = null;
        row.QualifiedWeightsHash = null;
        row.QualificationReportSha256 = null;
        row.QualifiedAt = null;
        row.QualifiedByUserId = null;
        if (row.Mode == IdentitySegmentMode.Automatic)
            row.Mode = IdentitySegmentMode.Observation;

        Touch(row, adminUserId);
        Record(orgId, adminUserId);
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Identity segment {SegmentId} of org {OrgId}: qualification revoked by {UserId}, mode {Mode}",
            row.Id, orgId, adminUserId, IdentitySegmentModeCodes.CodeOf(row.Mode));
        return await ViewAsync(row, cancellationToken);
    }

    public async Task<SegmentResolution> ResolveAsync(Guid orgId, Guid propertyId, string? formatId, string? channel, CancellationToken cancellationToken = default)
    {
        var format = formatId?.Trim();
        var code = channel?.Trim();
        IdentitySegmentSetting? row = null;
        if (!string.IsNullOrEmpty(format) && !string.IsNullOrEmpty(code))
        {
            var rows = await db.IdentitySegmentSettings
                .AsNoTracking()
                .IgnoreQueryFilters()
                .Where(s => s.OrgId == orgId && s.FormatId == format && s.Channel == code && (s.PropertyId == propertyId || s.PropertyId == null))
                .ToListAsync(cancellationToken);

            // The segment of the property first, then the one of every property of the org.
            row = rows.FirstOrDefault(s => s.PropertyId == propertyId) ?? rows.FirstOrDefault(s => s.PropertyId is null);
        }

        var evaluation = Evaluate(row, Current());
        return new SegmentResolution(
            row?.Id,
            row?.Mode ?? IdentitySegmentMode.Observation,
            evaluation.Qualified,
            evaluation.AutoApprovalReason is null,
            row?.RequiredAssurance ?? IdentityAssuranceLevels.Consistency,
            evaluation.AutoApprovalReason);
    }

    /// <summary>
    /// Why a segment is not qualified for what is in force, null when it is: no qualification recorded, no usable policy, or a qualification for another policy version,
    /// policy hash or weights hash (the models not loaded is no match).
    /// </summary>
    private static string? QualificationReasonOf(IdentitySegmentSetting? row, Configuration configuration)
    {
        if (row is null || !HasQualification(row))
            return IdentitySegmentReasonCodes.SegmentNotQualified;

        if (!configuration.Policy.IsReady)
            return IdentitySegmentReasonCodes.PolicyUnavailable;

        return configuration.Fingerprint is { } fingerprint
            && string.Equals(row.QualifiedPolicyVersion, fingerprint.PolicyVersion, StringComparison.Ordinal)
            && SameHash(row.QualifiedPolicyHash, fingerprint.PolicyHash)
            && SameHash(row.QualifiedWeightsHash, fingerprint.WeightsHash)
            ? null
            : IdentitySegmentReasonCodes.QualificationMismatch;
    }

    /// <summary>The conditions of the automatic approval for a segment (null: none), in their order (see the remarks of the class).</summary>
    private Evaluation Evaluate(IdentitySegmentSetting? row, Configuration configuration)
    {
        var qualificationReason = QualificationReasonOf(row, configuration);
        var qualified = qualificationReason is null;
        string? reason;
        if (!configuration.AutoApprovalEnabled)
            reason = IdentitySegmentReasonCodes.AutoApprovalDisabled;
        else if (row is null || row.Mode == IdentitySegmentMode.Observation)
            reason = IdentitySegmentReasonCodes.SegmentObservation;
        else if (row.Mode == IdentitySegmentMode.Suspended)
            reason = IdentitySegmentReasonCodes.SegmentSuspended;
        else if (row.Mode != IdentitySegmentMode.Automatic)
            reason = IdentitySegmentReasonCodes.SegmentObservation;
        else if (!HasQualification(row))
            reason = IdentitySegmentReasonCodes.SegmentNotQualified;
        else if (configuration.Policy.IsReady && !qualified)
            reason = IdentitySegmentReasonCodes.QualificationMismatch;
        else if (configuration.Policy.IsReady && configuration.Policy.Provisional)
            reason = IdentitySegmentReasonCodes.PolicyProvisional;
        else if (!configuration.Policy.IsReady)
            reason = IdentitySegmentReasonCodes.PolicyUnavailable;
        else if (!AssuranceSatisfied(row.FormatId, row.Channel, row.RequiredAssurance))
            reason = IdentitySegmentReasonCodes.AssuranceRequiresChip;
        else
            reason = null;

        return new Evaluation(qualified, qualificationReason, reason);
    }

    /// <summary>The format gives, on the channel, at least the assurance the segment requires; a format or a channel the registry does not admit gives none.</summary>
    private bool AssuranceSatisfied(string formatId, string channel, string requiredAssurance)
    {
        var required = RankOf(requiredAssurance);
        return required >= 0 && RankOf(formats.AssuranceFor(formatId, channel)) >= required;
    }

    /// <summary>The order of the levels of assurance: <c>consistency</c> below <c>cryptographic</c>; -1 for a code that is not a level (it satisfies nothing and nothing satisfies it).</summary>
    private static int RankOf(string? level)
    {
        return level switch
        {
            IdentityAssuranceLevels.Consistency => 0,
            IdentityAssuranceLevels.Cryptographic => 1,
            _ => -1,
        };
    }

    private Configuration Current()
    {
        var autoApproval = featureFlags.IsEnabled(FeatureFlags.CheckInIdentityVerification)
            && featureFlags.IsEnabled(FeatureFlags.CheckInIdentitySelfHosted)
            && featureFlags.IsEnabled(FeatureFlags.CheckInIdentityAutoApproval);
        return new Configuration(autoApproval, policies.Snapshot, fingerprints.Current());
    }

    private IdentitySegmentView ViewOf(IdentitySegmentSetting row, IReadOnlyDictionary<Guid, string> propertyNames, Configuration configuration)
    {
        var evaluation = Evaluate(row, configuration);
        return new IdentitySegmentView(
            row.Id,
            row.PropertyId,
            row.PropertyId is { } property && propertyNames.TryGetValue(property, out var name) ? name : null,
            row.FormatId,
            row.Channel,
            row.Mode,
            evaluation.Qualified,
            evaluation.QualificationReason,
            row.QualifiedPolicyVersion,
            row.QualifiedAt,
            evaluation.AutoApprovalReason is null,
            evaluation.AutoApprovalReason,
            row.RequiredAssurance,
            row.SuspendedReason);
    }

    private async Task<IdentitySegmentView> ViewAsync(IdentitySegmentSetting row, CancellationToken cancellationToken)
    {
        return ViewOf(row, await PropertyNamesAsync(row.OrgId, [row], cancellationToken), Current());
    }

    /// <summary>The names of the properties of the org the rows name (a deleted property keeps its name: its segment is still listed).</summary>
    private async Task<IReadOnlyDictionary<Guid, string>> PropertyNamesAsync(Guid orgId, IReadOnlyList<IdentitySegmentSetting> rows, CancellationToken cancellationToken)
    {
        var ids = rows.Where(row => row.PropertyId is not null).Select(row => row.PropertyId!.Value).Distinct().ToList();
        if (ids.Count == 0)
            return new Dictionary<Guid, string>();

        return await db.Properties
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(p => p.OrgId == orgId && ids.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, p => p.Name, cancellationToken);
    }

    /// <summary>The segment of the org with this id, tracked; null when it is not of the org (a uniform not found).</summary>
    private Task<IdentitySegmentSetting?> FindAsync(Guid orgId, Guid segmentId, CancellationToken cancellationToken)
    {
        return db.IdentitySegmentSettings.IgnoreQueryFilters().FirstOrDefaultAsync(s => s.Id == segmentId && s.OrgId == orgId, cancellationToken);
    }

    private Task<IdentitySegmentSetting?> FindByKeyAsync(Guid orgId, Guid? propertyId, string formatId, string channel, CancellationToken cancellationToken)
    {
        return db.IdentitySegmentSettings
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.OrgId == orgId && s.PropertyId == propertyId && s.FormatId == formatId && s.Channel == channel, cancellationToken);
    }

    private void Touch(IdentitySegmentSetting row, string? actorUserId)
    {
        row.UpdatedAt = UtcNow();
        row.UpdatedByUserId = Cut(actorUserId);
    }

    /// <summary>One line in the activity log of the org, in the same save as the change: the fact, never a value (like the biometric settings).</summary>
    private void Record(Guid orgId, string? actorUserId)
    {
        activityLog?.Record(OrgActivity.Of(orgId, OrgActivityType.OrgSettingsChanged, actorUserId, orgId.ToString()));
    }

    private static bool HasQualification(IdentitySegmentSetting row)
    {
        return row.QualifiedPolicyVersion is not null && row.QualifiedPolicyHash is not null && row.QualifiedWeightsHash is not null;
    }

    private static bool SameHash(string? given, string? inForce)
    {
        return given is not null && inForce is not null && string.Equals(given.Trim(), inForce, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSha256(string? value)
    {
        var trimmed = value?.Trim();
        return trimmed is { Length: 64 } && trimmed.All(char.IsAsciiHexDigit);
    }

    private static string? Cut(string? userId)
    {
        return userId is { Length: > IdentitySegmentSetting.UserIdMaxLength } ? userId[..IdentitySegmentSetting.UserIdMaxLength] : userId;
    }

    private static DomainRuleException Invalid()
    {
        return new DomainRuleException(IdentityApiErrors.IdentitySegmentInvalid, "IdentitySegmentInvalid");
    }

    private DateTime UtcNow() => clock.GetUtcNow().UtcDateTime;

    /// <summary>What a resolution reads once: the platform permission, the policy and the fingerprint in force.</summary>
    private sealed record Configuration(bool AutoApprovalEnabled, IdentityPolicySnapshot Policy, IdentityFingerprint? Fingerprint);

    /// <summary>Whether a segment is qualified for what is in force (and why not), and the first condition of the automatic approval that fails (null: allowed).</summary>
    private sealed record Evaluation(bool Qualified, string? QualificationReason, string? AutoApprovalReason);
}
