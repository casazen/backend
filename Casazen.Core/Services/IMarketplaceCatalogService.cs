using Casazen.Core.Authorization;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

/// <summary>
/// What the Marketplace page and its Active tab read (E09/F01/US01/T03, NF-54): one catalog of five groups, fed by real sources (the daily
/// cache of Octorate's portals, the license modules, the partners the platform admin curates, the suppliers the host already asked), and
/// the list of what the org has activated together with the suppliers it used. Nothing here calls Octorate: the portals are read from
/// CasaZen's own database.
/// </summary>
/// <remarks>
/// Every method takes the org and the caller's <see cref="HostScope"/> (decided by the web layer, never read from client input): a
/// <c>propertyId</c> that is not a property of the org inside the scope is a 404 (<c>marketplace_property_not_found</c>), and
/// the activations of another org are never read. The services decide nothing about roles.
/// </remarks>
public interface IMarketplaceCatalogService
{
    /// <summary>
    /// The five groups, always in this order (<see cref="MarketplaceGroupIds.InOrder"/>), each entry with its status for the org or, with a
    /// <paramref name="propertyId"/>, for that property (for the kinds that are per property: a channel and a tool). While the Octorate
    /// connector is off, or the org has no connected Octorate account, the first three groups still answer but say so
    /// (<see cref="MarketplaceCatalog.Octorate"/>) and their entries cannot be activated (<see cref="MarketplaceReasons.OctorateNotConnected"/>);
    /// the partners and the home services never depend on Octorate. The texts that come from the resource files and from the partners are in
    /// <paramref name="language"/> (<c>it</c> or <c>en</c>), Italian when a partner has no text in that language.
    /// </summary>
    /// <exception cref="Casazen.Core.Exceptions.NotFoundException">The property is not one of the org inside the caller's scope.</exception>
    Task<MarketplaceCatalog> GetCatalogAsync(
        Guid orgId, HostScope scope, Guid? propertyId, string language, CancellationToken ct = default);

    /// <summary>
    /// The Active tab: the activations of the org that are active, pending, to fix or requested (<c>Integrations</c>, limited to the properties
    /// of the caller's scope; the org-level ones only to a caller who reaches the whole org) and the suppliers the host already asked for a
    /// job (<c>Providers</c>, from <see cref="IHostSupplierUsageReader"/>). With a <paramref name="propertyId"/> only that property's
    /// activations and the org-level ones.
    /// </summary>
    /// <exception cref="Casazen.Core.Exceptions.NotFoundException">The property is not one of the org inside the caller's scope.</exception>
    Task<MarketplaceActive> GetActiveAsync(Guid orgId, HostScope scope, Guid? propertyId, CancellationToken ct = default);
}

/// <summary>The ids of the five groups of the catalog, in the order the page shows them.</summary>
public static class MarketplaceGroupIds
{
    public const string SalesChannels = "salesChannels";
    public const string ConnectedTools = "connectedTools";
    public const string ChannelManagerFunctions = "channelManagerFunctions";
    public const string HomeServices = "homeServices";
    public const string Partners = "partners";

    public static IReadOnlyList<string> InOrder { get; } =
        [SalesChannels, ConnectedTools, ChannelManagerFunctions, HomeServices, Partners];
}

/// <summary>What a card says about its state (NF-54): the four states of the page.</summary>
public enum MarketplaceCardStatus
{
    /// <summary>On, for the org or for the property asked.</summary>
    Active = 1,

    /// <summary>Not on, and nothing is under way.</summary>
    NotActive = 2,

    /// <summary>Asked for or under way, not finished (a request that waits for the license is shown as Pending).</summary>
    Pending = 3,

    /// <summary>It stopped working and the host has to do something.</summary>
    ToFix = 4,
}

/// <summary>Whether the status of a card is the org's or a property's.</summary>
public enum MarketplaceStatusScope
{
    /// <summary>The org as a whole (a partner, a licensed function, or a channel asked about without a property).</summary>
    Org = 1,

    /// <summary>One property (a channel or a tool asked about with a property).</summary>
    Property = 2,
}

/// <summary>The stable codes of <c>reason</c>, which the client translates; none of them is a sentence.</summary>
public static class MarketplaceReasons
{
    /// <summary>The Octorate connector is off, or the org has no connected Octorate account: the page leads to the connect wizard.</summary>
    public const string OctorateNotConnected = "octorate_not_connected";

    /// <summary>It is already on.</summary>
    public const string AlreadyActive = "already_active";

    /// <summary>Its activation is under way or waits for an answer.</summary>
    public const string InProgress = "activation_in_progress";

    /// <summary>It stopped working: the way forward is fixing it, not activating it again.</summary>
    public const string NeedsFixing = "needs_fixing";

    /// <summary>Octorate does not let a connection to the portal be created through the API.</summary>
    public const string NotAvailable = "not_available";
}

/// <summary>What a card costs, as far as it is known: the words (<c>null</c> when nobody knows, never invented) and who invoices.</summary>
public sealed record MarketplaceCost(string? Text, MarketplaceCostInvoicer InvoicedBy)
{
    /// <summary>Nobody knows what it costs or who invoices.</summary>
    public static MarketplaceCost Unknown { get; } = new(null, MarketplaceCostInvoicer.Unknown);
}

/// <summary>
/// One card of the catalog. <see cref="PropertyCount"/> is set only for a channel or a tool asked about without a property and active on
/// some: the number of properties of the caller's scope that have it on. The last four members are the partner's own.
/// </summary>
public sealed record MarketplaceCatalogEntry(
    MarketplaceActivationKind Kind,
    string Code,
    string Name,
    string? LogoUrl,
    string Benefit,
    MarketplaceCost Cost,
    MarketplaceCardStatus Status,
    MarketplaceStatusScope StatusScope,
    int? PropertyCount,
    bool CanActivate,
    string? Reason,
    MarketplacePartnerJob? Job = null,
    string? WebsiteUrl = null,
    IReadOnlyList<string>? ServiceCategories = null,
    string? OctoratePortalCode = null);

/// <summary>A group of the catalog. The home services have no entries and say that their flow is unchanged (<see cref="UnchangedFlow"/>).</summary>
public sealed record MarketplaceGroup(string Id, bool UnchangedFlow, IReadOnlyList<MarketplaceCatalogEntry> Entries);

/// <summary>Whether the Octorate connector is on, and the org has a connected Octorate account in this environment.</summary>
public sealed record MarketplaceOctorateState(bool Enabled, bool Connected);

/// <summary>The answer of <see cref="IMarketplaceCatalogService.GetCatalogAsync"/>.</summary>
public sealed record MarketplaceCatalog(MarketplaceOctorateState Octorate, IReadOnlyList<MarketplaceGroup> Groups);

/// <summary>The property an integration of the Active tab is about.</summary>
public sealed record MarketplaceIntegrationProperty(Guid Id, string Name);

/// <summary>
/// One activation of the Active tab. <see cref="Name"/> is the name of the portal or of the partner, and the code of the module for a licensed
/// function (the web layer writes its name in the language of the request). <see cref="IsDeclared"/>: the host declared it and Octorate did
/// not prove it.
/// </summary>
public sealed record MarketplaceIntegration(
    MarketplaceActivationKind Kind,
    string Code,
    string Name,
    MarketplaceIntegrationProperty? Property,
    MarketplaceCardStatus Status,
    bool IsDeclared,
    DateTime? LastSyncedAt);

/// <summary>The answer of <see cref="IMarketplaceCatalogService.GetActiveAsync"/>: the integrations and the suppliers used, together.</summary>
public sealed record MarketplaceActive(IReadOnlyList<MarketplaceIntegration> Integrations, IReadOnlyList<HostUsedSupplier> Providers);
