using System.Reflection;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IOrgEmptinessChecker" />
/// <remarks>
/// <para><b>The tables are read from the model.</b> Every entity of <see cref="AppDbContext"/> that implements
/// <see cref="ITenantOwned"/> is checked, so a new tenant table is part of the "org is empty" rule from the day it exists,
/// without anyone remembering to add it here. Only <see cref="OnboardingRows"/> are left out (what the onboarding itself
/// writes for a person who has not done anything yet) and <see cref="OrgMember"/> (the person's own owner row is expected,
/// anything else is another member). <c>IgnoreQueryFilters()</c> with no argument: the soft-deleted properties count too,
/// and the filter of the request's tenant is not the org being asked about.</para>
/// <para>This is the guard of a destructive-looking move (the person leaves the org and the org is deactivated), so it
/// fails closed: any doubt is a blocker and the acceptance is refused.</para>
/// </remarks>
public sealed class OrgEmptinessChecker(AppDbContext db) : IOrgEmptinessChecker
{
    /// <summary>
    /// Tenant tables that do not make an org "used": the consents and the signup attribution the onboarding records, and the
    /// previous slugs of the org (a person can rename the org before doing anything else). They stay with the org that is left.
    /// </summary>
    internal static readonly IReadOnlySet<Type> OnboardingRows = new HashSet<Type>
    {
        typeof(ConsentRecord),
        typeof(SignupAttribution),
        typeof(OrgSlugAlias),
    };

    private static readonly MethodInfo HasRowsMethod = typeof(OrgEmptinessChecker)
        .GetMethod(nameof(HasRowsAsync), BindingFlags.Instance | BindingFlags.NonPublic)!;

    /// <summary>
    /// The tenant-owned entity types whose rows block the move: every <see cref="ITenantOwned"/> entity of the model but the
    /// <see cref="OnboardingRows"/> and <see cref="OrgMember"/>. Exposed for the test that proves a new tenant table is checked.
    /// </summary>
    internal static IReadOnlyList<Type> CheckedTenantTables(AppDbContext context) =>
        context.Model.GetEntityTypes()
            .Where(e => e.BaseType is null
                        && !e.IsOwned()
                        && !e.HasSharedClrType
                        && typeof(ITenantOwned).IsAssignableFrom(e.ClrType)
                        && !OnboardingRows.Contains(e.ClrType)
                        && e.ClrType != typeof(OrgMember))
            .Select(e => e.ClrType)
            .OrderBy(t => t.Name, StringComparer.Ordinal)
            .ToList();

    public async Task<OrgEmptiness> CheckAsync(Guid orgId, string userId, CancellationToken cancellationToken = default)
    {
        var org = await db.Orgs.AsNoTracking().FirstOrDefaultAsync(o => o.Id == orgId, cancellationToken);
        if (org is null)
            return OrgEmptiness.Blocked("not_found");

        var blockers = new List<string>();
        if (org.OrgType != OrgType.Host)
            blockers.Add("not_a_host_org");

        AddBillingBlockers(org, blockers);

        // The person is the only one in it.
        if (await db.Users.AsNoTracking().AnyAsync(u => u.OrgId == orgId && u.Id != userId, cancellationToken))
            blockers.Add("other_users");
        if (await db.OrgMembers.IgnoreQueryFilters().AsNoTracking()
                .AnyAsync(m => m.OrgId == orgId && m.UserId != userId, cancellationToken))
            blockers.Add("other_members");

        // Nothing in any tenant table.
        foreach (var table in CheckedTenantTables(db))
        {
            var task = (Task<bool>)HasRowsMethod.MakeGenericMethod(table).Invoke(this, [orgId, cancellationToken])!;
            if (await task)
                blockers.Add($"data:{table.Name}");
        }

        return blockers.Count == 0 ? OrgEmptiness.Empty : new OrgEmptiness(false, blockers);
    }

    /// <summary>The plan and everything a customer sets up around it: Starter, no Stripe, no Connect, no domain, no branding.</summary>
    private static void AddBillingBlockers(Org org, List<string> blockers)
    {
        if (org.PlanTier != PlanTier.Starter)
            blockers.Add("plan");
        if (!string.IsNullOrWhiteSpace(org.StripeCustomerId))
            blockers.Add("stripe_customer");
        if (!string.IsNullOrWhiteSpace(org.SubscriptionId) || org.SubscriptionStatus != SubscriptionStatus.None)
            blockers.Add("subscription");
        if (!string.IsNullOrWhiteSpace(org.StripeConnectedAccountId))
            blockers.Add("connect_account");
        if (!string.IsNullOrWhiteSpace(org.CustomDomain)
            || !string.IsNullOrWhiteSpace(org.Subdomain)
            || org.PublicHostMode != PublicHostMode.CasazenPath)
            blockers.Add("domain");
        if (!string.IsNullOrWhiteSpace(org.LogoUrl)
            || !string.IsNullOrWhiteSpace(org.ThemeColor)
            || !string.IsNullOrWhiteSpace(org.PublicThemeId)
            || !string.IsNullOrWhiteSpace(org.HeroImageUrl)
            || !string.IsNullOrWhiteSpace(org.Tagline))
            blockers.Add("branding");
    }

    private async Task<bool> HasRowsAsync<TEntity>(Guid orgId, CancellationToken cancellationToken)
        where TEntity : class, ITenantOwned =>
        await db.Set<TEntity>().IgnoreQueryFilters().AsNoTracking().AnyAsync(e => e.OrgId == orgId, cancellationToken);
}
