using Casazen.Core.Assistant;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IAssistantRulesService"/>
/// <remarks>
/// <para><b>Tenant.</b> Every query names the organization on top of the tenant filter of the context, so the rules, the exceptions and the properties of another
/// organization are never read or written; a property that is not the organization's is 404 <c>property_not_found</c> (<see cref="AssistantStore.EnsurePropertyAsync"/>).</para>
/// <para><b>Checked first.</b> A request is validated before anything is read (<see cref="AssistantRulesValidator"/>): an invalid one writes nothing.</para>
/// <para><b>One unit of work.</b> The version of the instructions grows only when a value really changed, in the same save as the change
/// (<see cref="BumpVersionAsync"/>); a save that changes nothing does not move it. Nothing the host wrote is logged: the log lines carry ids and the version.</para>
/// </remarks>
public sealed class AssistantRulesService(
    AppDbContext db,
    ILogger<AssistantRulesService> logger,
    TimeProvider? timeProvider = null) : IAssistantRulesService
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<EffectiveAssistantRules> GetAsync(Guid orgId, CancellationToken ct = default)
    {
        var row = await db.OrgAssistantRules.AsNoTracking().FirstOrDefaultAsync(r => r.OrgId == orgId, ct);
        return row is null ? EffectiveAssistantRules.Defaults : EffectiveAssistantRules.Of(row);
    }

    public async Task<EffectiveAssistantRules> SaveAsync(Guid orgId, AssistantRulesInput rules, string userId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        // Everything is checked before the database is touched.
        var (hoursFrom, hoursTo) = AssistantRulesValidator.CleanHours(rules.HoursFrom, rules.HoursTo);
        var wanted = new EffectiveAssistantRules(
            Enabled: rules.Enabled ?? false,
            Tone: ToneOf(rules.Tone),
            Languages: rules.Languages is null ? OrgAssistantRules.DefaultLanguages : AssistantRulesValidator.CleanLanguages(rules.Languages),
            Signature: AssistantRulesValidator.CleanSignature(rules.Signature),
            HoursFrom: hoursFrom,
            HoursTo: hoursTo,
            AllowedTopics: AssistantRulesValidator.CleanTopics(rules.AllowedTopics),
            ForbiddenTopics: AssistantRulesValidator.CleanTopics(rules.ForbiddenTopics),
            ConfidenceThreshold: AssistantRulesValidator.CleanThreshold(rules.ConfidenceThreshold),
            InstructionVersion: 0);

        var row = await FindRulesAsync(orgId, ct);
        var changed = !Same(row is null ? EffectiveAssistantRules.Defaults : EffectiveAssistantRules.Of(row), wanted);
        if (row is null)
        {
            row = new OrgAssistantRules { OrgId = orgId };
            db.OrgAssistantRules.Add(row);
        }

        row.Enabled = wanted.Enabled;
        row.Tone = wanted.Tone;
        row.Languages = [.. wanted.Languages];
        row.Signature = wanted.Signature;
        row.HoursFrom = wanted.HoursFrom;
        row.HoursTo = wanted.HoursTo;
        row.AllowedTopics = [.. wanted.AllowedTopics];
        row.ForbiddenTopics = [.. wanted.ForbiddenTopics];
        row.ConfidenceThreshold = wanted.ConfidenceThreshold;
        row.UpdatedAt = Now();
        row.UpdatedByUserId = userId;
        if (changed)
            row.InstructionVersion = checked(row.InstructionVersion + 1);

        await AssistantStore.SaveAsync(db, ct);

        logger.LogInformation("Assistant rules of org {OrgId} saved by {UserId}, instruction version {Version}", orgId, userId, row.InstructionVersion);
        return EffectiveAssistantRules.Of(row);
    }

    public async Task<PropertyAssistantSettingsItem> GetPropertySettingsAsync(Guid orgId, Guid propertyId, CancellationToken ct = default)
    {
        await AssistantStore.EnsurePropertyAsync(db, orgId, propertyId, ct);

        var row = await db.PropertyAssistantSettings.AsNoTracking()
            .FirstOrDefaultAsync(s => s.OrgId == orgId && s.PropertyId == propertyId, ct);
        return ItemOf(propertyId, row);
    }

    public async Task<PropertyAssistantSettingsItem> SavePropertySettingsAsync(
        Guid orgId,
        Guid propertyId,
        PropertyAssistantSettingsInput settings,
        string userId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        // Everything is checked before the database is touched. A field not given is «as the organization»: null, never a value.
        var (hoursFrom, hoursTo) = AssistantRulesValidator.CleanHours(settings.HoursFrom, settings.HoursTo);
        var wanted = new PropertyAssistantSettings
        {
            Enabled = settings.Enabled,
            Tone = settings.Tone is { } tone ? ToneOf(tone) : null,
            Signature = AssistantRulesValidator.CleanSignature(settings.Signature),
            HoursFrom = hoursFrom,
            HoursTo = hoursTo,
            ConfidenceThreshold = AssistantRulesValidator.CleanOptionalThreshold(settings.ConfidenceThreshold),
            ForbiddenTopics = settings.ForbiddenTopics is null ? null : AssistantRulesValidator.CleanTopics(settings.ForbiddenTopics),
        };

        await AssistantStore.EnsurePropertyAsync(db, orgId, propertyId, ct);
        var row = db.PropertyAssistantSettings.Local.FirstOrDefault(s => s.OrgId == orgId && s.PropertyId == propertyId)
                  ?? await db.PropertyAssistantSettings.FirstOrDefaultAsync(s => s.OrgId == orgId && s.PropertyId == propertyId, ct);

        // Nothing to say differently is no exception at all: no row is made, and the one that exists goes (the property follows the organization again).
        if (!HasExceptions(wanted))
        {
            if (row is not null)
            {
                db.PropertyAssistantSettings.Remove(row);
                await BumpVersionAsync(orgId, ct);
                await AssistantStore.SaveAsync(db, ct);
                logger.LogInformation("Assistant exceptions of property {PropertyId} of org {OrgId} removed", propertyId, orgId);
            }

            return ItemOf(propertyId, null);
        }

        var changed = row is null || !Same(row, wanted);
        if (row is null)
        {
            row = new PropertyAssistantSettings { OrgId = orgId, PropertyId = propertyId };
            db.PropertyAssistantSettings.Add(row);
        }

        row.Enabled = wanted.Enabled;
        row.Tone = wanted.Tone;
        row.Signature = wanted.Signature;
        row.HoursFrom = wanted.HoursFrom;
        row.HoursTo = wanted.HoursTo;
        row.ConfidenceThreshold = wanted.ConfidenceThreshold;
        row.ForbiddenTopics = wanted.ForbiddenTopics is null ? null : [.. wanted.ForbiddenTopics];
        row.UpdatedAt = Now();
        row.UpdatedByUserId = userId;
        if (changed)
            await BumpVersionAsync(orgId, ct);

        await AssistantStore.SaveAsync(db, ct);

        logger.LogInformation("Assistant exceptions of property {PropertyId} of org {OrgId} saved by {UserId}", propertyId, orgId, userId);
        return ItemOf(propertyId, row);
    }

    public async Task DeletePropertySettingsAsync(Guid orgId, Guid propertyId, CancellationToken ct = default)
    {
        await AssistantStore.EnsurePropertyAsync(db, orgId, propertyId, ct);

        var row = await db.PropertyAssistantSettings.FirstOrDefaultAsync(s => s.OrgId == orgId && s.PropertyId == propertyId, ct);
        if (row is null)
            return;

        db.PropertyAssistantSettings.Remove(row);
        await BumpVersionAsync(orgId, ct);
        await AssistantStore.SaveAsync(db, ct);

        logger.LogInformation("Assistant exceptions of property {PropertyId} of org {OrgId} removed", propertyId, orgId);
    }

    public async Task<EffectiveAssistantRules> ResolveEffectiveAsync(Guid orgId, Guid propertyId, CancellationToken ct = default)
    {
        var organization = await GetAsync(orgId, ct);
        var exceptions = await db.PropertyAssistantSettings.AsNoTracking()
            .FirstOrDefaultAsync(s => s.OrgId == orgId && s.PropertyId == propertyId, ct);
        return organization.WithExceptionsOf(exceptions);
    }

    public async Task BumpVersionAsync(Guid orgId, CancellationToken ct = default)
    {
        var row = await FindRulesAsync(orgId, ct);
        if (row is null)
        {
            // An organization that never saved its rules has the defaults, at version 1: the first change takes it to 2.
            row = new OrgAssistantRules { OrgId = orgId, UpdatedAt = Now() };
            db.OrgAssistantRules.Add(row);
        }

        row.InstructionVersion = checked(row.InstructionVersion + 1);
    }

    /// <summary>The rules row of the organization: the one this unit of work already tracks (an added row is not in the database yet), else the stored one.</summary>
    private async Task<OrgAssistantRules?> FindRulesAsync(Guid orgId, CancellationToken ct) =>
        db.OrgAssistantRules.Local.FirstOrDefault(r => r.OrgId == orgId)
        ?? await db.OrgAssistantRules.FirstOrDefaultAsync(r => r.OrgId == orgId, ct);

    private DateTime Now() => _clock.GetUtcNow().UtcDateTime;

    /// <summary>The tone as a request gives it: none is neutral, a value no member declares is 422 <c>assistant_rules_invalid</c>.</summary>
    private static AssistantTone ToneOf(AssistantTone? tone) => tone switch
    {
        null => AssistantTone.Neutral,
        { } declared when Enum.IsDefined(declared) => declared,
        _ => throw new DomainRuleException(AssistantRulesValidator.RulesInvalidCode, "AssistantRulesInvalid"),
    };

    private static bool Same(EffectiveAssistantRules before, EffectiveAssistantRules after) =>
        before.Enabled == after.Enabled
        && before.Tone == after.Tone
        && before.Signature == after.Signature
        && before.HoursFrom == after.HoursFrom
        && before.HoursTo == after.HoursTo
        && before.ConfidenceThreshold == after.ConfidenceThreshold
        && before.Languages.SequenceEqual(after.Languages)
        && before.AllowedTopics.SequenceEqual(after.AllowedTopics)
        && before.ForbiddenTopics.SequenceEqual(after.ForbiddenTopics);

    private static bool Same(PropertyAssistantSettings stored, PropertyAssistantSettings wanted) =>
        stored.Enabled == wanted.Enabled
        && stored.Tone == wanted.Tone
        && stored.Signature == wanted.Signature
        && stored.HoursFrom == wanted.HoursFrom
        && stored.HoursTo == wanted.HoursTo
        && stored.ConfidenceThreshold == wanted.ConfidenceThreshold
        && (stored.ForbiddenTopics is null
            ? wanted.ForbiddenTopics is null
            : wanted.ForbiddenTopics is not null && stored.ForbiddenTopics.SequenceEqual(wanted.ForbiddenTopics));

    private static bool HasExceptions(PropertyAssistantSettings settings) =>
        settings.Enabled is not null
        || settings.Tone is not null
        || settings.Signature is not null
        || settings.HoursFrom is not null
        || settings.HoursTo is not null
        || settings.ConfidenceThreshold is not null
        || settings.ForbiddenTopics is not null;

    private static PropertyAssistantSettingsItem ItemOf(Guid propertyId, PropertyAssistantSettings? row) => row is null
        ? new PropertyAssistantSettingsItem(propertyId, null, null, null, null, null, null, null, null)
        : new PropertyAssistantSettingsItem(
            propertyId,
            row.Enabled,
            row.Tone,
            row.Signature,
            row.HoursFrom,
            row.HoursTo,
            row.ConfidenceThreshold,
            row.ForbiddenTopics is null ? null : [.. row.ForbiddenTopics],
            row.UpdatedAt);
}
