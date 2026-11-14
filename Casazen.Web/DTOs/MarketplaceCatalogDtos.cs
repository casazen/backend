using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Web.DTOs.ServiceRequests;

namespace Casazen.Web.DTOs;

/// <summary>
/// <c>GET api/marketplace/catalog</c> (E09/F01/US01/T03, NF-54): the five groups of the Marketplace page, always in the same order
/// (<c>salesChannels</c>, <c>connectedTools</c>, <c>channelManagerFunctions</c>, <c>homeServices</c>, <c>partners</c>). <c>octorate</c> says
/// whether the connector is on and the org connected: when it is not, the first three groups still answer, with entries that cannot be activated
/// (<c>reason</c> <c>octorate_not_connected</c>), so that the page can lead to the connect wizard.
/// </summary>
public sealed record MarketplaceCatalogResponse(MarketplaceOctorateDto Octorate, IReadOnlyList<MarketplaceGroupDto> Groups)
{
    public static MarketplaceCatalogResponse From(MarketplaceCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return new MarketplaceCatalogResponse(
            new MarketplaceOctorateDto(catalog.Octorate.Enabled, catalog.Octorate.Connected),
            [.. catalog.Groups.Select(MarketplaceGroupDto.From)]);
    }
}

/// <summary>Whether Octorate is usable for the org: the connector is on (<c>enabled</c>) and the org has a connected account (<c>connected</c>).</summary>
public sealed record MarketplaceOctorateDto(bool Enabled, bool Connected);

/// <summary>A group of the catalog. <c>unchangedFlow</c> is true for the home services, which have no entries here: the page keeps its own flow.</summary>
public sealed record MarketplaceGroupDto(string Id, bool UnchangedFlow, IReadOnlyList<MarketplaceCatalogEntryDto> Entries)
{
    public static MarketplaceGroupDto From(MarketplaceGroup group)
    {
        ArgumentNullException.ThrowIfNull(group);
        return new MarketplaceGroupDto(group.Id, group.UnchangedFlow, [.. group.Entries.Select(MarketplaceCatalogEntryDto.From)]);
    }
}

/// <summary>What a card costs: the words, <c>null</c> when nobody knows (never invented), and who invoices.</summary>
public sealed record MarketplaceCostDto(string? Text, MarketplaceCostInvoicer InvoicedBy);

/// <summary>
/// A card. <c>status</c> is <c>Active</c>, <c>NotActive</c>, <c>Pending</c> or <c>ToFix</c>; <c>statusScope</c> says whether it is the org's or a
/// property's; <c>propertyCount</c> is how many properties have it on, only for a channel or a tool asked about without a property and
/// active on some. <c>reason</c> is a code, not a sentence. <c>job</c>, <c>websiteUrl</c>, <c>serviceCategories</c> and
/// <c>octoratePortalCode</c> are the partner's own and <c>null</c> on every other card. <c>connectByPropertyId</c> is true for a sales channel whose
/// connection CasaZen creates from the id of the property on the portal («Collega da CasaZen»: the host types the id, then approves the channel
/// manager on the extranet of the OTA), and false on every other card, which keeps the hand-over to the panel of the channel manager.
/// </summary>
public sealed record MarketplaceCatalogEntryDto(
    MarketplaceActivationKind Kind,
    string Code,
    string Name,
    string? LogoUrl,
    string Benefit,
    MarketplaceCostDto Cost,
    MarketplaceCardStatus Status,
    MarketplaceStatusScope StatusScope,
    int? PropertyCount,
    bool CanActivate,
    string? Reason,
    MarketplacePartnerJob? Job,
    string? WebsiteUrl,
    IReadOnlyList<string>? ServiceCategories,
    string? OctoratePortalCode,
    bool ConnectByPropertyId)
{
    public static MarketplaceCatalogEntryDto From(MarketplaceCatalogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return new MarketplaceCatalogEntryDto(
            entry.Kind,
            entry.Code,
            entry.Name,
            entry.LogoUrl,
            entry.Benefit,
            new MarketplaceCostDto(entry.Cost.Text, entry.Cost.InvoicedBy),
            entry.Status,
            entry.StatusScope,
            entry.PropertyCount,
            entry.CanActivate,
            entry.Reason,
            entry.Job,
            entry.WebsiteUrl,
            entry.ServiceCategories,
            entry.OctoratePortalCode,
            entry.ConnectByPropertyId);
    }
}

/// <summary>
/// <c>GET api/marketplace/active</c> (E09/F01/US01/T03, NF-54): the Active tab. <c>integrations</c> are what the org has activated (active, pending,
/// to fix or requested) inside the caller's scope; <c>providers</c> are the CasaZen suppliers the host already asked for a job. Together they are
/// the tab.
/// </summary>
public sealed record MarketplaceActiveResponse(IReadOnlyList<MarketplaceIntegrationDto> Integrations, IReadOnlyList<UsedSupplierDto> Providers)
{
    /// <summary>The answer, with the names of the licensed functions written in <paramref name="language"/>.</summary>
    public static MarketplaceActiveResponse From(MarketplaceActive active, IMarketplaceTexts texts, string language)
    {
        ArgumentNullException.ThrowIfNull(active);
        ArgumentNullException.ThrowIfNull(texts);
        return new MarketplaceActiveResponse(
            [.. active.Integrations.Select(integration => MarketplaceIntegrationDto.From(integration, texts, language))],
            [.. active.Providers.Select(UsedSupplierDto.From)]);
    }
}

/// <summary>
/// One activation of the Active tab: what it is (<c>kind</c>, <c>code</c>, <c>name</c>), the property it is for (<c>null</c> for an org-level
/// one), where it stands (<c>status</c>), whether the host declared it and Octorate did not prove it (<c>isDeclared</c>), and when CasaZen last read
/// its state (<c>lastSyncedAt</c>). <c>declaredAt</c> is the day the host gave when it declared it (a partner: midnight UTC of that day, show its
/// date) and <c>verifiedAt</c> is when a connection or the license first showed it; each is <c>null</c> when the row has no such fact, and a client
/// says «declared by you on …» from the first and «verified» only when the second is set.
/// </summary>
public sealed record MarketplaceIntegrationDto(
    MarketplaceActivationKind Kind,
    string Code,
    string Name,
    MarketplaceIntegrationPropertyDto? Property,
    MarketplaceCardStatus Status,
    bool IsDeclared,
    DateTime? LastSyncedAt,
    DateTime? DeclaredAt = null,
    DateTime? VerifiedAt = null)
{
    public static MarketplaceIntegrationDto From(MarketplaceIntegration integration, IMarketplaceTexts texts, string language)
    {
        ArgumentNullException.ThrowIfNull(integration);
        ArgumentNullException.ThrowIfNull(texts);

        // A licensed function has no name of its own on a row: its words are in the resource files, in the language asked.
        var name = integration.Kind == MarketplaceActivationKind.LicensedFunction && MarketplaceLicensedFunctions.IsKnown(integration.Code)
            ? texts.Get($"Marketplace.Licensed.{integration.Code}.Name", language)
            : integration.Name;
        return new MarketplaceIntegrationDto(
            integration.Kind,
            integration.Code,
            name,
            integration.Property is { } property ? new MarketplaceIntegrationPropertyDto(property.Id, property.Name) : null,
            integration.Status,
            integration.IsDeclared,
            integration.LastSyncedAt,
            integration.DeclaredAt,
            integration.VerifiedAt);
    }
}

/// <summary>The property an integration is about.</summary>
public sealed record MarketplaceIntegrationPropertyDto(Guid Id, string Name);
