using System.Globalization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IBiometricSettingsService" />
/// <remarks>
/// <para><b>One org, always named.</b> Every read and write goes through the organization the caller names, with its id in the query (the tenant filter of
/// the context is lifted by name: a platform administrator works on the org of its request, under another tenant). Nothing here reads another org's row, and
/// the callers (<c>BiometricSettingsController</c> for the caller's own org, <c>AdminBiometricsController</c> for the staff) decide which org that is.</para>
/// <para><b>Provisional.</b> The entity has no flag: a row is provisional while <see cref="BiometricSettings.CalibratedAt"/> is null. A change made by the
/// organization sets it back to null, so a calibration never outlives the values it was made on; only <see cref="MarkCalibratedAsync"/> writes it.</para>
/// <para><b>Audit.</b> A real change writes one line in the activity log (<see cref="OrgActivityType.OrgSettingsChanged"/>: the fact, never the values, like
/// the other settings of the org) in the same save, and one line in the application log with the new numbers (they are not personal data) and who changed
/// them: the thresholds decide who gets a door code without a person, so they are worth a trail.</para>
/// <para>The table has no unique index on the org (the model is not changed here): two first saves at the same millisecond could write two rows, and every
/// reader takes the oldest.</para>
/// </remarks>
public sealed class BiometricSettingsService(
    AppDbContext db,
    TimeProvider clock,
    ILogger<BiometricSettingsService> logger,
    IActivityLog? activityLog = null) : IBiometricSettingsService
{
    public async Task<BiometricSettingsView> GetAsync(Guid orgId, CancellationToken cancellationToken = default)
    {
        var row = await FindAsync(orgId, tracked: false, cancellationToken);
        return row is null ? Defaults(await TierAsync(orgId, cancellationToken)) : ViewOf(row);
    }

    public async Task<BiometricSettingsView> UpdateAsync(
        Guid orgId,
        BiometricSettingsUpdate update,
        string? actorUserId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);

        var row = await FindAsync(orgId, tracked: true, cancellationToken);
        PlanTier? tier = null;
        if (row is null)
            tier = await TierAsync(orgId, cancellationToken);

        var current = row is null ? Defaults(tier) : ViewOf(row);
        var enabled = update.Enabled ?? current.Enabled;
        var accept = Normalize(update.AcceptThreshold ?? current.AcceptThreshold);
        var escalate = Normalize(update.EscalateThreshold ?? current.EscalateThreshold);
        var attempts = update.MaxAttempts ?? current.MaxAttempts;

        // Bounds first: nothing is saved, nothing is logged, when the values are out of them.
        Validate(accept, escalate, attempts);

        // The values in force again: nothing changes, so no calibration is lost and no line is written.
        if (enabled == current.Enabled && accept == current.AcceptThreshold && escalate == current.EscalateThreshold && attempts == current.MaxAttempts)
            return current;

        var now = clock.GetUtcNow().UtcDateTime;
        if (row is null)
        {
            if (tier is null)
                throw new NotFoundException("The organization of the biometric settings does not exist.");

            row = new BiometricSettings { OrgId = orgId, CreatedAt = now };
            db.BiometricSettings.Add(row);
        }

        row.Enabled = enabled;
        row.AcceptThreshold = accept;
        row.EscalateThreshold = escalate;
        row.MaxAttempts = attempts;
        row.CalibratedAt = null;
        row.UpdatedAt = now;
        activityLog?.Record(OrgActivity.Of(orgId, OrgActivityType.OrgSettingsChanged, actorUserId, orgId.ToString()));
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Biometric settings of org {OrgId} changed by {UserId}: enabled={Enabled} accept={Accept} escalate={Escalate} maxAttempts={MaxAttempts}; the thresholds are provisional",
            orgId, actorUserId ?? "none", enabled, accept, escalate, attempts);
        return ViewOf(row);
    }

    public async Task<BiometricSettingsView?> MarkCalibratedAsync(Guid orgId, string actorUserId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorUserId);

        var tier = await TierAsync(orgId, cancellationToken);
        if (tier is null)
            return null;

        var now = clock.GetUtcNow().UtcDateTime;
        var row = await FindAsync(orgId, tracked: true, cancellationToken);
        if (row is null)
        {
            // An organization that never saved its settings is on today's defaults: the calibration says they are the right ones, so they get a row.
            row = new BiometricSettings
            {
                OrgId = orgId,
                Enabled = BiometricSettingsRules.EnabledByDefault(tier),
                AcceptThreshold = BiometricSettingsRules.DefaultAcceptThreshold,
                EscalateThreshold = BiometricSettingsRules.DefaultEscalateThreshold,
                MaxAttempts = BiometricSettingsRules.DefaultMaxAttempts,
                CreatedAt = now,
            };
            db.BiometricSettings.Add(row);
        }

        row.CalibratedAt = now;
        row.UpdatedAt = now;
        activityLog?.Record(OrgActivity.Of(orgId, OrgActivityType.OrgSettingsChanged, actorUserId, orgId.ToString()));
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Biometric thresholds of org {OrgId} marked as calibrated by {UserId}", orgId, actorUserId);
        return ViewOf(row);
    }

    /// <summary>The oldest row of the org (one is what is written), or null. The tenant filter is lifted by name: the org is in the query.</summary>
    private async Task<BiometricSettings?> FindAsync(Guid orgId, bool tracked, CancellationToken cancellationToken)
    {
        var query = db.BiometricSettings.IgnoreQueryFilters().Where(s => s.OrgId == orgId);
        if (!tracked)
            query = query.AsNoTracking();

        return await query.OrderBy(s => s.CreatedAt).ThenBy(s => s.Id).FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>The plan of the org, null when the org does not exist.</summary>
    private async Task<PlanTier?> TierAsync(Guid orgId, CancellationToken cancellationToken) =>
        await db.Orgs
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(o => o.Id == orgId)
            .Select(o => (PlanTier?)o.PlanTier)
            .FirstOrDefaultAsync(cancellationToken);

    private static BiometricSettingsView Defaults(PlanTier? tier) => new(
        BiometricSettingsRules.EnabledByDefault(tier),
        BiometricSettingsRules.DefaultAcceptThreshold,
        BiometricSettingsRules.DefaultEscalateThreshold,
        BiometricSettingsRules.DefaultMaxAttempts,
        Provisional: true,
        CalibratedAt: null,
        IsDefault: true);

    private static BiometricSettingsView ViewOf(BiometricSettings row) => new(
        row.Enabled,
        row.AcceptThreshold,
        row.EscalateThreshold,
        row.MaxAttempts,
        row.CalibratedAt is null,
        row.CalibratedAt,
        IsDefault: false);

    /// <summary>The value as the column keeps it: the decimals of <c>decimal(5,4)</c>, so what is checked is what is stored.</summary>
    private static decimal Normalize(decimal value) => Math.Round(value, BiometricSettingsRules.Decimals, MidpointRounding.AwayFromZero);

    private static void Validate(decimal accept, decimal escalate, int attempts)
    {
        if (escalate < BiometricSettingsRules.MinEscalateThreshold || accept > BiometricSettingsRules.MaxAcceptThreshold)
        {
            throw new DomainRuleException(
                BiometricSettingsErrors.ThresholdsOutOfRange,
                "BiometricSettingsThresholdsOutOfRange",
                BiometricSettingsRules.MinEscalateThreshold.ToString("0.00", CultureInfo.InvariantCulture),
                BiometricSettingsRules.MaxAcceptThreshold.ToString("0.00", CultureInfo.InvariantCulture));
        }

        if (escalate >= accept)
            throw new DomainRuleException(BiometricSettingsErrors.ThresholdsOrderInvalid, "BiometricSettingsThresholdsOrder");

        if (attempts is < BiometricSettingsRules.MinAttempts or > BiometricSettingsRules.MaxAttempts)
        {
            throw new DomainRuleException(
                BiometricSettingsErrors.AttemptsOutOfRange,
                "BiometricSettingsAttemptsOutOfRange",
                BiometricSettingsRules.MinAttempts,
                BiometricSettingsRules.MaxAttempts);
        }
    }
}
