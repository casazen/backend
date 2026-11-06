using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Octorate;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// <see cref="IMarketplacePortalCatalogService"/> on <see cref="MarketplacePortal"/> (E09/F01/US01/T02, NF-54): the daily cache of
/// Octorate's portal catalog. Only Octorate's own data is stored and no portal name is written in code.
/// </summary>
/// <remarks>
/// <para><b>Order.</b> Octorate is called first and the database is touched only afterwards, so an error of Octorate leaves the cache as it
/// was. The write is one <c>SaveChanges</c>, so it is one transaction: either the whole answer is applied or none of it.</para>
/// <para><b>Logs.</b> Counts and the code of an error: never the name of a portal, an answer or a token.</para>
/// </remarks>
public sealed class MarketplacePortalCatalogService(
    IOctorateApiClient client,
    AppDbContext db,
    TimeProvider timeProvider,
    ILogger<MarketplacePortalCatalogService> logger) : IMarketplacePortalCatalogService
{
    public async Task<MarketplacePortalRefreshResult> RefreshAsync(CancellationToken ct)
    {
        OctoratePortal[] received;
        try
        {
            received = await client.GetPortalsAsync(ct);
        }
        catch (OctorateException ex)
        {
            logger.LogWarning(
                "Marketplace portal refresh failed ({Code}, HTTP {StatusCode}): the cache is kept as it was",
                ex.Code, ex.OctorateStatusCode);
            throw;
        }

        // The portals of a known category, once each. The ones with no category (Octorate sends a few) are counted, never listed.
        var offered = new Dictionary<string, (OctoratePortal Portal, MarketplacePortalCategory Category)>(StringComparer.Ordinal);
        var withoutCategory = 0;
        foreach (var portal in received)
        {
            if (CategoryOf(portal.PortalCategory) is not { } category)
            {
                withoutCategory++;
                continue;
            }

            offered.TryAdd(portal.PortalCode, (portal, category));
        }

        if (offered.Count == 0)
        {
            // An empty answer is not a decision to hide everything: it is more likely a fault, and an empty cache is worse than a stale one.
            logger.LogWarning(
                "Marketplace portal refresh: Octorate returned {Received} portals and none of a known category, {WithoutCategory} without one: the cache is kept as it was",
                received.Length, withoutCategory);
            return new MarketplacePortalRefreshResult(false, received.Length, 0, 0, 0, withoutCategory);
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var cached = await db.MarketplacePortals.ToDictionaryAsync(p => p.PortalCode, StringComparer.Ordinal, ct);

        var created = 0;
        foreach (var (code, (portal, category)) in offered)
        {
            if (!cached.TryGetValue(code, out var row))
            {
                row = new MarketplacePortal { PortalCode = code };
                db.MarketplacePortals.Add(row);
                created++;
            }

            Apply(row, portal, category, now);
        }

        var hidden = 0;
        foreach (var row in cached.Values)
        {
            if (row.IsListed && !offered.ContainsKey(row.PortalCode))
            {
                row.IsListed = false;
                hidden++;
            }
        }

        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Marketplace portal cache refreshed: {Stored} portals ({Created} new), {Hidden} no longer listed, {WithoutCategory} skipped for having no known category",
            offered.Count, created, hidden, withoutCategory);
        return new MarketplacePortalRefreshResult(true, received.Length, offered.Count, created, hidden, withoutCategory);
    }

    /// <summary>The category the marketplace shows for what Octorate calls <paramref name="portalCategory"/>; <c>null</c> for any other.</summary>
    internal static MarketplacePortalCategory? CategoryOf(string? portalCategory)
    {
        if (string.Equals(portalCategory, OctoratePortalCategories.Ota, StringComparison.OrdinalIgnoreCase))
            return MarketplacePortalCategory.Ota;

        if (string.Equals(portalCategory, OctoratePortalCategories.ExternalTool, StringComparison.OrdinalIgnoreCase))
            return MarketplacePortalCategory.ExternalTool;

        return null;
    }

    private static void Apply(MarketplacePortal row, OctoratePortal portal, MarketplacePortalCategory category, DateTime now)
    {
        row.Name = Truncate(portal.Name, MarketplacePortal.NameMaxLength);
        row.Category = category;
        row.Creatable = portal.Creatable;
        row.Enabled = portal.Enabled;
        row.Preferred = portal.Preferred;
        row.Website = portal.Website;
        row.LogoUrl = portal.Logo;
        row.RequiresHotelId = portal.RequiresHotelId;
        row.RequiresPassword = portal.RequiresPassword;
        row.RequiresTokenAuthentication = portal.RequiresTokenAuthentication;
        row.FeatureContent = portal.FeatureContent;
        row.OctorateManageable = portal.OctorateManageable;
        row.CommissionIncludedInPayments = portal.CommissionIncludedInPayments;
        row.CommissionsInvoiced = portal.CommissionsInvoiced;
        row.IsListed = true;
        row.RefreshedAt = now;
    }

    private static string Truncate(string text, int maxLength) => text.Length <= maxLength ? text : text[..maxLength];
}
