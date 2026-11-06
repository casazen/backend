using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Octorate;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// The reads that the services of the marketplace share (E09/F01/US01/T03, T04): the checks of the org and of the property, the plan and
/// the country of the org, whether the Octorate connector is on and connected, and the errors that answer 404. Kept in one place so that the
/// catalog and the detail card cannot disagree about the same org.
/// </summary>
internal static class MarketplaceReads
{
    /// <summary>The facts about the org that decide what its marketplace shows.</summary>
    public sealed record OrgFacts(PlanTier PlanTier, string? BillingCountry);

    /// <summary>
    /// The scope is the one of the org the service is asked about: a scope of another org is a mistake of the caller, refused before anything is
    /// read (fail closed), because a scope would otherwise say "the whole org" of the wrong one.
    /// </summary>
    public static void EnsureSameOrg(Guid orgId, HostScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (orgId == Guid.Empty || scope.OrgId != orgId)
            throw new ArgumentException("The scope of the caller is not the scope of the organization asked about.", nameof(scope));
    }

    /// <summary>
    /// A <paramref name="propertyId"/> must be a property of the org inside the caller's scope, otherwise the answer is the 404 of a property
    /// that does not exist (the same for another org's, for one outside the scope and for one that was deleted). Nothing without an id.
    /// </summary>
    public static async Task EnsurePropertyInScopeAsync(
        AppDbContext db, Guid orgId, HostScope scope, Guid? propertyId, CancellationToken ct)
    {
        if (propertyId is not { } id)
            return;

        var reachable = await db.Properties.AsNoTracking()
            .Where(p => p.OrgId == orgId && p.Id == id)
            .InScope(scope)
            .AnyAsync(ct);
        if (!reachable)
            throw PropertyNotFound();
    }

    /// <summary>The plan and the billing country of the org (a country is <c>null</c> until the org gives one).</summary>
    public static async Task<OrgFacts> LoadOrgAsync(AppDbContext db, Guid orgId, CancellationToken ct)
    {
        var facts = await db.Orgs.AsNoTracking()
            .Where(o => o.Id == orgId)
            .Select(o => new OrgFacts(o.PlanTier, o.BillingCountry))
            .FirstOrDefaultAsync(ct);
        return facts ?? throw EntryNotFound();
    }

    /// <summary>
    /// Whether the Octorate connector is on and the org has a connected account in the environment of this CasaZen. With the flag off nothing
    /// is read: the connector does not exist for anybody.
    /// </summary>
    public static async Task<MarketplaceOctorateState> OctorateStateAsync(
        AppDbContext db, IFeatureFlags featureFlags, IHostEnvironment hostEnvironment, Guid orgId, CancellationToken ct)
    {
        if (!featureFlags.IsEnabled(FeatureFlags.OctorateConnector))
            return new MarketplaceOctorateState(false, false);

        var environment = OctorateEnvironments.Of(hostEnvironment);
        var connected = await db.OctorateConnections.AsNoTracking()
            .AnyAsync(
                c => c.OrgId == orgId && c.Environment == environment && c.Status == OctorateConnectionStatus.Connected,
                ct);
        return new MarketplaceOctorateState(true, connected);
    }

    /// <summary>
    /// The texts of a partner in <paramref name="language"/>, else in Italian (the default of the product), else <c>null</c>: the fallback is
    /// the whole row, not field by field, because a cost that is missing in one language is not the cost of the other.
    /// </summary>
    public static MarketplacePartnerText? TextOf(IEnumerable<MarketplacePartnerText> partnerTexts, string language)
    {
        var rows = partnerTexts.ToList();
        return rows.FirstOrDefault(t => t.Language == language)
               ?? rows.FirstOrDefault(t => t.Language == MarketplacePartnerText.Italian);
    }

    /// <summary>The public address of the logo of a partner (a key of the public bucket), or <c>null</c> without one or with a key that is not valid.</summary>
    public static string? PartnerLogoUrl(IFileStorage storage, MarketplacePartner partner)
    {
        return partner.LogoKey is { Length: > 0 } key && StorageKeys.IsValid(key) ? storage.GetPublicUrl(key) : null;
    }

    /// <summary>404 for a property that is not one of the org inside the caller's scope.</summary>
    public static NotFoundException PropertyNotFound() =>
        new("The property is not one of the organization inside the scope of the caller.")
        {
            Code = "marketplace_property_not_found",
            MessageKey = "MarketplacePropertyNotFound",
        };

    /// <summary>404 for an entry that does not exist, is hidden, or that the plan or the country of the org does not allow: one answer for all.</summary>
    public static NotFoundException EntryNotFound() =>
        new("The marketplace entry does not exist for this organization.")
        {
            Code = "marketplace_entry_not_found",
            MessageKey = "MarketplaceEntryNotFound",
        };
}
