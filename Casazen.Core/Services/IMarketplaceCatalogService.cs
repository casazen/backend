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

    Task<MarketplaceActive> GetActiveAsync(Guid orgId, HostScope scope, Guid? propertyId, CancellationToken ct = default);

    Task<bool> HasActiveIntegrationAsync(Guid orgId, HostScope scope, CancellationToken ct = default);
}

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

public enum MarketplaceCardStatus
{
    Active = 1,

    NotActive = 2,

    Pending = 3,

    ToFix = 4,
}

public enum MarketplaceStatusScope
{
    Org = 1,

    Property = 2,
}

public static class MarketplaceReasons
{
    public const string OctorateNotConnected = "octorate_not_connected";

    public const string AlreadyActive = "already_active";

    public const string InProgress = "activation_in_progress";

    public const string NeedsFixing = "needs_fixing";

    public const string NotAvailable = "not_available";
}

public sealed record MarketplaceCost(string? Text, MarketplaceCostInvoicer InvoicedBy)
{
    public static MarketplaceCost Unknown { get; } = new(null, MarketplaceCostInvoicer.Unknown);
}

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
    string? OctoratePortalCode = null,
    bool ConnectByPropertyId = false,
    bool RequiresCredentials = false);

public sealed record MarketplaceGroup(string Id, bool UnchangedFlow, IReadOnlyList<MarketplaceCatalogEntry> Entries);

public sealed record MarketplaceOctorateState(bool Enabled, bool Connected);

public sealed record MarketplaceCatalog(MarketplaceOctorateState Octorate, IReadOnlyList<MarketplaceGroup> Groups);

public sealed record MarketplaceIntegrationProperty(Guid Id, string Name);

/// <summary>
/// One activation of the Active tab. <see cref="Name"/> is the name of the portal or of the partner, and the code of the module for a licensed
/// function (the web layer writes its name in the language of the request). <see cref="IsDeclared"/>: the host declared it and Octorate did
/// not prove it. <see cref="DeclaredAt"/> is the day the host gave when it declared it (for a partner, midnight UTC of that day: show its date) and
/// <see cref="VerifiedAt"/> is when a connection or the license first showed it (E09/F02/US03/T01): the Active tab reads them from the row, so
/// «declared by you on …» and «verified» survive a reload; either is <c>null</c> when the row has no such fact.
/// </summary>
public sealed record MarketplaceIntegration(
    MarketplaceActivationKind Kind,
    string Code,
    string Name,
    MarketplaceIntegrationProperty? Property,
    MarketplaceCardStatus Status,
    bool IsDeclared,
    DateTime? LastSyncedAt,
    DateTime? DeclaredAt = null,
    DateTime? VerifiedAt = null);

public sealed record MarketplaceActive(IReadOnlyList<MarketplaceIntegration> Integrations, IReadOnlyList<HostUsedSupplier> Providers);
