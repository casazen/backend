using Casazen.Core.Authorization;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

public sealed partial class OwnerOnboardingService
{
    public async Task<OwnerReadinessReport> GetReadinessAsync(HostScope scope, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var today = Today();
        var now = timeProvider.GetUtcNow().UtcDateTime;

        // The properties inside the reach, then what hangs on them: the periods, the owners of the org that hold one, the mandates, the invitations.
        var properties = await db.Properties
            .AsNoTracking()
            .Where(p => p.OrgId == scope.OrgId)
            .InScope(scope)
            .Select(p => new { p.Id, p.Name })
            .ToListAsync(ct);
        var propertyIds = properties.Select(p => p.Id).ToHashSet();
        var periods = await LoadPeriodsAsync(scope, propertyIds, ct);
        var owners = await LoadVisibleOwnersAsync(scope, ct);

        var ownershipIds = periods.Select(p => p.Id).ToHashSet();
        var mandates = (await db.OwnerMandates
                .AsNoTracking()
                .Where(m => m.OrgId == scope.OrgId)
                .Select(m => new { m.PropertyOwnershipId, m.EffectiveFrom, m.EffectiveUntil, m.MandatePdfUrl })
                .ToListAsync(ct))
            .Where(m => ownershipIds.Contains(m.PropertyOwnershipId))
            .Select(m => new OwnerMandateFact(
                m.PropertyOwnershipId,
                RomeCalendar.DateInRome(m.EffectiveFrom),
                m.EffectiveUntil is { } until ? RomeCalendar.DateInRome(until) : null,
                !string.IsNullOrWhiteSpace(m.MandatePdfUrl)))
            .ToList();
        var invitations = (await db.OwnerInvitations
                .AsNoTracking()
                .Where(i => i.OrgId == scope.OrgId)
                .Select(i => new { i.PropertyOwnerId, i.Id, i.Status, i.ExpiresAt, i.CreatedAt })
                .ToListAsync(ct))
            .ToLookup(i => i.PropertyOwnerId, i => new OwnerInvitationFact(i.Id, i.Status, i.ExpiresAt, i.CreatedAt));

        // The shares of every property, once: the same rule the import applies before it writes.
        var issuesByProperty = new Dictionary<Guid, IReadOnlyList<OwnershipIssue>>();
        foreach (var group in periods.GroupBy(p => p.PropertyId))
        {
            var list = group.Select(ToPeriod).ToList();
            var issues = OwnerOwnershipTimeline.Analyze(list, list.Min(p => p.Start), today);
            if (issues.Count > 0)
                issuesByProperty[group.Key] = issues;
        }

        var periodsByOwner = periods.ToLookup(p => p.OwnerId);
        var ownerRows = new List<OwnerReadinessOwner>(owners.Count);
        foreach (var owner in owners)
        {
            var facts = periodsByOwner[owner.Id]
                .Select(p => new OwnerOwnershipFact(
                    p.Id,
                    p.PropertyId,
                    RomeCalendar.DateInRome(p.Start),
                    p.End is { } end ? RomeCalendar.DateInRome(end) : null,
                    p.SharePercent))
                .ToList();

            // The overlap of another owner with itself is that owner's: only this owner's own is its reason.
            var ownIssues = issuesByProperty.ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlyList<OwnershipIssue>)[.. pair.Value.Where(i => i.Kind != OwnershipIssueKind.OwnerOverlap || i.OwnerId == owner.Id)]);

            var registry = OwnerReadinessRules.Registry(owner);
            var ownership = OwnerReadinessRules.Ownership(facts, ownIssues);
            var mandate = OwnerReadinessRules.Mandate(facts, mandates, today);
            var fiscal = OwnerReadinessRules.FiscalProfile(owner);
            var payment = OwnerReadinessRules.Payment(owner);
            // The portal needs a name to greet the owner by, not the whole registry: the kind of the owner can wait.
            var named = registry.All(r => r.Code != OwnerReadinessReasons.LegalNameMissing);
            var portal = OwnerReadinessRules.Portal(owner, named, ownership.Count == 0);

            var flags = new OwnerReadinessFlags(
                registry.Count == 0,
                ownership.Count == 0,
                mandate.Count == 0,
                fiscal.Count == 0,
                payment.Count == 0,
                portal.Count == 0);
            var state = OwnerPortalStateRules.StateOf([.. invitations[owner.Id]], now, flags.Portal);
            ownerRows.Add(new OwnerReadinessOwner(
                owner.Id,
                owner.ExternalKey,
                OwnerFieldRules.DisplayName(owner),
                owner.EntityKind is { } kind ? KindCode(kind) : null,
                flags,
                flags.Registry && flags.Ownership && flags.Mandate && flags.FiscalProfile && flags.Payment,
                state,
                [.. registry, .. ownership, .. mandate, .. fiscal, .. payment, .. portal]));
        }

        var propertyRows = new List<OwnerReadinessProperty>();
        var withoutOwner = 0;
        foreach (var property in properties)
        {
            var hasPeriods = periods.Any(p => p.PropertyId == property.Id);
            issuesByProperty.TryGetValue(property.Id, out var issues);
            if (!hasPeriods)
                withoutOwner++;

            if (hasPeriods && issues is null)
                continue;

            var reasons = hasPeriods
                ? issues!.Select(OwnerReadinessRules.IssueCode).Distinct(StringComparer.Ordinal)
                    .Select(code => new OwnerReadinessReason(OwnerReadinessDimensions.Ownership, code, PropertyId: property.Id))
                    .ToList()
                : [new OwnerReadinessReason(OwnerReadinessDimensions.Ownership, OwnerReadinessReasons.OwnershipMissing, PropertyId: property.Id)];
            propertyRows.Add(new OwnerReadinessProperty(property.Id, property.Name, false, reasons, issues ?? []));
        }

        var accountingNotReady = ownerRows.Count(o => !o.AccountingReady);
        var summary = new OwnerReadinessSummary(
            ownerRows.Count,
            properties.Count,
            Count(ownerRows, o => o.Ready.Registry),
            Count(ownerRows, o => o.Ready.Ownership),
            Count(ownerRows, o => o.Ready.Mandate),
            Count(ownerRows, o => o.Ready.FiscalProfile),
            Count(ownerRows, o => o.Ready.Payment),
            Count(ownerRows, o => o.Ready.Portal),
            ownerRows.Count - accountingNotReady,
            withoutOwner,
            propertyRows.Count,
            ownerRows.Count > 0 && accountingNotReady == 0 && propertyRows.Count == 0);

        var orderedOwners = ownerRows
            .OrderBy(o => o.AccountingReady)
            .ThenBy(o => o.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(o => o.OwnerId)
            .ToList();
        var orderedProperties = propertyRows
            .OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(p => p.PropertyId)
            .ToList();
        var truncated = orderedOwners.Count > OwnerOnboardingLimits.MaxListed || orderedProperties.Count > OwnerOnboardingLimits.MaxListed;

        // Counts only: no name, no identifier, no address.
        logger.LogInformation(
            "Owner readiness for org {OrgId}: {Owners} owners, {Properties} properties, {AccountingReady} ready for the accounting, {PropertiesToComplete} properties to complete",
            scope.OrgId,
            summary.Owners,
            summary.Properties,
            summary.AccountingReadyOwners,
            summary.PropertiesToComplete);
        return new OwnerReadinessReport(
            today,
            summary,
            [.. orderedOwners.Take(OwnerOnboardingLimits.MaxListed)],
            [.. orderedProperties.Take(OwnerOnboardingLimits.MaxListed)],
            truncated);
    }

    /// <summary>The code of a kind in the API: <c>person</c> or <c>company</c>.</summary>
    internal static string KindCode(OwnerEntityKind kind) => kind == OwnerEntityKind.LegalEntity ? "company" : "person";

    private static OwnerReadinessCount Count(IReadOnlyCollection<OwnerReadinessOwner> owners, Func<OwnerReadinessOwner, bool> ready)
    {
        var yes = owners.Count(ready);
        return new OwnerReadinessCount(yes, owners.Count - yes);
    }
}
