using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// <see cref="IMarketplaceCatalogService"/> (E09/F01/US01/T03, NF-54): the five groups of the Marketplace page and the Active tab, read from
/// CasaZen's own database. The portals are the daily cache of Octorate's catalog (<see cref="MarketplacePortal"/>), the partners are the rows
/// the platform admin curates (<see cref="MarketplacePartner"/>, filtered by <see cref="MarketplaceVisibility"/>), the license modules are a
/// fixed list whose words live in the resource files, and the suppliers are the ones the host already asked for a job. What an org has
/// activated is <see cref="MarketplaceActivation"/>, read for the org asked about and no other.
/// </summary>
/// <remarks>
/// No name of a portal or of a partner is written here: the names come from the rows, and the words of the cards from the rows and the
/// resource files. Nothing calls Octorate while a page loads.
/// </remarks>
public sealed class MarketplaceCatalogService(
    AppDbContext db,
    IHostSupplierUsageReader suppliers,
    IFeatureFlags featureFlags,
    IFileStorage storage,
    IMarketplaceTexts texts,
    IHostEnvironment hostEnvironment) : IMarketplaceCatalogService
{
    public async Task<MarketplaceCatalog> GetCatalogAsync(
        Guid orgId, HostScope scope, Guid? propertyId, string language, CancellationToken ct = default)
    {
        MarketplaceReads.EnsureSameOrg(orgId, scope);
        var lang = MarketplaceLanguages.Normalize(language);
        await MarketplaceReads.EnsurePropertyInScopeAsync(db, orgId, scope, propertyId, ct);

        var org = await MarketplaceReads.LoadOrgAsync(db, orgId, ct);
        var octorate = await MarketplaceReads.OctorateStateAsync(db, featureFlags, hostEnvironment, orgId, ct);
        var states = await MarketplaceCardStates.LoadAsync(db, orgId, scope, propertyId, ct);

        // Preferred portals first, then by name: the order of the page, decided here and not by the database's collation.
        var portals = (await db.MarketplacePortals.AsNoTracking().Where(p => p.IsListed && p.Enabled).ToListAsync(ct))
            .OrderByDescending(p => p.Preferred)
            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.PortalCode, StringComparer.Ordinal)
            .ToList();

        IReadOnlyList<MarketplaceGroup> groups =
        [
            new(MarketplaceGroupIds.SalesChannels, false, PortalEntries(portals, MarketplacePortalCategory.Ota, lang, octorate, states)),
            new(MarketplaceGroupIds.ConnectedTools, false, PortalEntries(portals, MarketplacePortalCategory.ExternalTool, lang, octorate, states)),
            new(MarketplaceGroupIds.ChannelManagerFunctions, false, LicensedEntries(lang, octorate, states)),
            new(MarketplaceGroupIds.HomeServices, true, []),
            new(MarketplaceGroupIds.Partners, false, await PartnerEntriesAsync(org, lang, states, ct)),
        ];
        return new MarketplaceCatalog(octorate, groups);
    }

    public async Task<MarketplaceActive> GetActiveAsync(Guid orgId, HostScope scope, Guid? propertyId, CancellationToken ct = default)
    {
        MarketplaceReads.EnsureSameOrg(orgId, scope);
        await MarketplaceReads.EnsurePropertyInScopeAsync(db, orgId, scope, propertyId, ct);

        // The org's own rows: the properties inside the caller's scope, and the org-level ones only for a caller who reaches the whole org.
        var orgWide = scope.IsOrgWide;
        var reachable = db.Properties.Where(p => p.OrgId == orgId).InScope(scope).Select(p => p.Id);
        var query = db.MarketplaceActivations.AsNoTracking()
            .Where(a => a.OrgId == orgId && a.Status != MarketplaceActivationStatus.Deactivated);
        query = propertyId is { } only
            ? query.Where(a => a.PropertyId == only || (a.PropertyId == null && orgWide))
            : query.Where(a => (a.PropertyId == null && orgWide) || (a.PropertyId != null && reachable.Contains(a.PropertyId.Value)));
        var rows = await query.ToListAsync(ct);

        var portalNames = await PortalNamesAsync(rows, ct);
        var partnerNames = await PartnerNamesAsync(rows, ct);
        var propertyNames = await PropertyNamesAsync(orgId, rows, ct);

        var integrations = rows
            .Select(row => new MarketplaceIntegration(
                row.Kind,
                row.TargetCode,
                row.Kind switch
                {
                    MarketplaceActivationKind.Channel or MarketplaceActivationKind.Tool => portalNames.GetValueOrDefault(row.TargetCode) ?? row.TargetCode,
                    MarketplaceActivationKind.Partner => partnerNames.GetValueOrDefault(row.TargetCode) ?? row.TargetCode,
                    _ => row.TargetCode,
                },
                row.PropertyId is { } id && propertyNames.TryGetValue(id, out var propertyName)
                    ? new MarketplaceIntegrationProperty(id, propertyName)
                    : null,
                MarketplaceCardRules.CardStatusOf(row.Status),
                row.IsDeclared,
                row.LastSyncedAt,
                row.DeclaredAt,
                row.VerifiedAt))
            .OrderBy(integration => integration.Kind)
            .ThenBy(integration => integration.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(integration => integration.Property?.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // The suppliers the host already asked for a job, in the same scope: together with the integrations they are the Active tab.
        var providers = await suppliers.ListAsync(scope, ct);
        return new MarketplaceActive(integrations, providers);
    }

    private IReadOnlyList<MarketplaceCatalogEntry> PortalEntries(
        List<MarketplacePortal> portals,
        MarketplacePortalCategory category,
        string lang,
        MarketplaceOctorateState octorate,
        MarketplaceCardStates states)
    {
        var kind = category == MarketplacePortalCategory.Ota ? MarketplaceActivationKind.Channel : MarketplaceActivationKind.Tool;
        var benefitKey = category == MarketplacePortalCategory.Ota ? "Marketplace.Channel.Benefit" : "Marketplace.Tool.Benefit";
        return
        [
            .. portals.Where(p => p.Category == category).Select(portal =>
            {
                var state = states.Of(kind, portal.PortalCode);
                var action = MarketplaceCardRules.ActionOf(kind, state.Status, octorate.Connected, portal.Creatable);
                return new MarketplaceCatalogEntry(
                    kind,
                    portal.PortalCode,
                    portal.Name,
                    portal.LogoUrl,
                    texts.Get(benefitKey, lang, portal.Name),
                    MarketplaceCost.Unknown,
                    state.Status,
                    state.Scope,
                    state.PropertyCount,
                    action.CanActivate,
                    action.Reason);
            }),
        ];
    }

    private IReadOnlyList<MarketplaceCatalogEntry> LicensedEntries(string lang, MarketplaceOctorateState octorate, MarketplaceCardStates states)
    {
        return
        [
            .. MarketplaceLicensedFunctions.Codes.Select(code =>
            {
                var state = states.Of(MarketplaceActivationKind.LicensedFunction, code);
                var action = MarketplaceCardRules.ActionOf(MarketplaceActivationKind.LicensedFunction, state.Status, octorate.Connected);
                return new MarketplaceCatalogEntry(
                    MarketplaceActivationKind.LicensedFunction,
                    code,
                    texts.Get($"Marketplace.Licensed.{code}.Name", lang),
                    null,
                    texts.Get($"Marketplace.Licensed.{code}.Benefit", lang),
                    MarketplaceCost.Unknown,
                    state.Status,
                    state.Scope,
                    null,
                    action.CanActivate,
                    action.Reason);
            }),
        ];
    }

    private async Task<IReadOnlyList<MarketplaceCatalogEntry>> PartnerEntriesAsync(
        MarketplaceReads.OrgFacts org, string lang, MarketplaceCardStates states, CancellationToken ct)
    {
        // Hidden partners never leave the database; of the visible ones, only those the plan and the country of the org allow.
        var shown = (await db.MarketplacePartners.AsNoTracking().Where(p => p.IsVisible).ToListAsync(ct))
            .Where(p => MarketplaceVisibility.IsVisible(p, org.PlanTier, org.BillingCountry))
            .OrderBy(p => p.Job)
            .ThenBy(p => p.SortOrder)
            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (shown.Count == 0)
            return [];

        var ids = shown.Select(p => p.Id).ToList();
        var textsByPartner = (await db.MarketplacePartnerTexts.AsNoTracking().Where(t => ids.Contains(t.PartnerId)).ToListAsync(ct))
            .ToLookup(t => t.PartnerId);

        return
        [
            .. shown.Select(partner =>
            {
                var text = MarketplaceReads.TextOf(textsByPartner[partner.Id], lang);
                var state = states.Of(MarketplaceActivationKind.Partner, partner.Code);
                var action = MarketplaceCardRules.ActionOf(MarketplaceActivationKind.Partner, state.Status, octorateConnected: false);
                return new MarketplaceCatalogEntry(
                    MarketplaceActivationKind.Partner,
                    partner.Code,
                    partner.Name,
                    MarketplaceReads.PartnerLogoUrl(storage, partner),
                    text?.Benefit ?? string.Empty,
                    new MarketplaceCost(text?.CostText, partner.InvoicedBy),
                    state.Status,
                    state.Scope,
                    null,
                    action.CanActivate,
                    action.Reason,
                    partner.Job,
                    partner.WebsiteUrl,
                    [.. partner.ServiceCategories],
                    partner.OctoratePortalCode);
            }),
        ];
    }

    private async Task<Dictionary<string, string>> PortalNamesAsync(List<MarketplaceActivation> rows, CancellationToken ct)
    {
        var codes = rows
            .Where(row => row.Kind is MarketplaceActivationKind.Channel or MarketplaceActivationKind.Tool)
            .Select(row => row.TargetCode)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (codes.Count == 0)
            return new Dictionary<string, string>(StringComparer.Ordinal);

        return await db.MarketplacePortals.AsNoTracking()
            .Where(p => codes.Contains(p.PortalCode))
            .ToDictionaryAsync(p => p.PortalCode, p => p.Name, StringComparer.Ordinal, ct);
    }

    private async Task<Dictionary<string, string>> PartnerNamesAsync(List<MarketplaceActivation> rows, CancellationToken ct)
    {
        var codes = rows
            .Where(row => row.Kind == MarketplaceActivationKind.Partner)
            .Select(row => row.TargetCode)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (codes.Count == 0)
            return new Dictionary<string, string>(StringComparer.Ordinal);

        return await db.MarketplacePartners.AsNoTracking()
            .Where(p => codes.Contains(p.Code))
            .ToDictionaryAsync(p => p.Code, p => p.Name, StringComparer.Ordinal, ct);
    }

    private async Task<Dictionary<Guid, string>> PropertyNamesAsync(Guid orgId, List<MarketplaceActivation> rows, CancellationToken ct)
    {
        var ids = rows.Where(row => row.PropertyId is not null).Select(row => row.PropertyId!.Value).Distinct().ToList();
        if (ids.Count == 0)
            return new Dictionary<Guid, string>();

        return await db.Properties.AsNoTracking()
            .Where(p => p.OrgId == orgId && ids.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, p => p.Name, ct);
    }
}
