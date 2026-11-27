using Casazen.Core.Entities.Enums;
using Casazen.Core.Models;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// The keys of the items the owner ledger adds to the billing setup (E13/F01/US02/T03, NF-118): stable codes, never renamed, next to the contributors that
/// give them (as <see cref="BillingSetupItemKeys"/> says epics do). The client maps each to its step of the wizard «Fatturazione»: the first to
/// <see cref="BillingSetupSections.Channels"/>, the second to <see cref="BillingSetupSections.Mandates"/>.
/// </summary>
public static class OwnerBillingSetupItemKeys
{
    /// <summary>The agency never saved its commission rules of the channels (reason <see cref="BillingSetupReasons.Unconfirmed"/>): the defaults of the configuration are shown but nobody confirmed them.</summary>
    public const string ChannelCommissionsUnconfirmed = "channel_commissions_unconfirmed";

    /// <summary>Some owners with an ownership not ended have no mandate in force (reason <see cref="BillingSetupReasons.Required"/>, with the count of those owners).</summary>
    public const string OwnersWithoutMandate = "owners_without_mandate";
}

/// <summary>What the two contributors of the owner ledger ask of the database: both are for agencies only, and both name the org in every query.</summary>
internal static class OwnerBillingSetupReads
{
    /// <summary>True when the org is on the Agency plan (<c>Org.PlanTier</c>, the definition of the agency tier): any other organization gets no item from them.</summary>
    public static Task<bool> IsAgencyAsync(AppDbContext db, Guid orgId, CancellationToken ct) =>
        db.Orgs.AsNoTracking().AnyAsync(org => org.Id == orgId && org.PlanTier == PlanTier.Agency, ct);
}

/// <summary>
/// The section «Commissioni dei canali» (E13/F01/US02/T03), for an agency: the item <see cref="OwnerBillingSetupItemKeys.ChannelCommissionsUnconfirmed"/> is
/// missing until the organization saved its channel commission rules once (<c>PUT api/owner-accounting/channel-commission-rules</c>). The defaults the
/// configuration ships (⚖ R2) alone do not count: an agency that accepts them saves them as its rules, and the step is then done; a list saved empty leaves
/// the defaults, so it does not count either. A non-agency organization gets no item.
/// </summary>
/// <remarks>A read only: it never writes, names the org in every query (it may run in a job with no tenant of its own) and returns a code, never a rule.</remarks>
public sealed class ChannelCommissionsSectionContributor(AppDbContext db) : IBillingSetupContributor
{
    public string Section => BillingSetupSections.Channels;

    public async Task<IReadOnlyList<BillingSetupItem>> EvaluateAsync(Guid orgId, CancellationToken ct)
    {
        if (!await OwnerBillingSetupReads.IsAgencyAsync(db, orgId, ct))
            return [];

        var saved = await db.OrgChannelCommissionRules
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .AsNoTracking()
            .AnyAsync(rule => rule.OrgId == orgId, ct);
        return saved
            ? []
            : [new BillingSetupItem(OwnerBillingSetupItemKeys.ChannelCommissionsUnconfirmed, Section, BillingSetupReasons.Unconfirmed)];
    }
}

/// <summary>
/// The section «Mandato standard» (E13/F01/US02/T03), for an agency: the item <see cref="OwnerBillingSetupItemKeys.OwnersWithoutMandate"/> carries the count
/// of the owners with an active ownership and no active mandate, and is missing while the count is above 0. A non-agency organization gets no item.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Active ownership.</b> One that has not ended on the Rome day of today (in force or to come): the same definition the standard mandate of
/// <see cref="IOwnerMandateService.ApplyTemplateAsync"/> uses to decide which owners it is for, so the count is what that step still has to fix. An owner
/// that is anonymized is not an owner of the agency any more and is not counted.</item>
/// <item><b>Active mandate.</b> A version in force today for the share of every active ownership of the owner, by <see cref="OwnerMandateRules.InForce"/>:
/// the one definition the ledger, the readiness and the statements use (a mandate of the owner covers all its ownerships; one of a single ownership, the
/// shape of before E13, covers that ownership first). An owner with one share that no mandate covers has no active mandate: the ledger would hold its stays
/// back (<c>mandate_missing_for_stay</c>).</item>
/// <item>A read only; the count is a number, never a name.</item>
/// </list>
/// </remarks>
public sealed class OwnerMandatesSectionContributor(AppDbContext db, TimeProvider time) : IBillingSetupContributor
{
    public string Section => BillingSetupSections.Mandates;

    public async Task<IReadOnlyList<BillingSetupItem>> EvaluateAsync(Guid orgId, CancellationToken ct)
    {
        if (!await OwnerBillingSetupReads.IsAgencyAsync(db, orgId, ct))
            return [];

        var today = time.TodayInRomeAsDateOnly();
        var ownerships = await db.PropertyOwnerships
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .AsNoTracking()
            .Where(ownership => ownership.OrgId == orgId)
            .Select(ownership => new { ownership.Id, ownership.PropertyOwnerId, ownership.OwnershipEnd })
            .ToListAsync(ct);
        var active = ownerships
            .Where(ownership => ownership.OwnershipEnd is not { } end || RomeCalendar.DateInRome(end) >= today)
            .ToList();
        if (active.Count == 0)
            return [];

        var managed = active.Select(ownership => ownership.PropertyOwnerId).Distinct().ToList();
        var owners = await db.PropertyOwners
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .AsNoTracking()
            .Where(owner => owner.OrgId == orgId && owner.AnonymizedAt == null && managed.Contains(owner.Id))
            .Select(owner => owner.Id)
            .ToListAsync(ct);
        var mandates = (await db.OwnerMandates
                .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
                .AsNoTracking()
                .Where(mandate => mandate.OrgId == orgId)
                .ToListAsync(ct))
            .ToLookup(mandate => mandate.PropertyOwnerId);

        var without = 0;
        foreach (var ownerId in owners)
        {
            var own = mandates[ownerId].ToList();
            var uncovered = active
                .Where(ownership => ownership.PropertyOwnerId == ownerId)
                .Any(ownership => OwnerMandateRules.InForce(own, ownerId, ownership.Id, today) is null);
            if (uncovered)
                without++;
        }

        return without == 0
            ? []
            : [new BillingSetupItem(OwnerBillingSetupItemKeys.OwnersWithoutMandate, Section, BillingSetupReasons.Required, without)];
    }
}
