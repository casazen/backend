using Casazen.Core.Authorization;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

/// <summary>
/// The data of the detail card of an entry of the marketplace (E09/F01/US01/T04, NF-55): what it does and for whom, what is needed to activate
/// it, what it costs and who invoices, what data is shared, how to deactivate it, the questions people ask. Read from the same sources as the
/// catalog (the portal cache, the partner catalog, the resource files), for the org and the scope of the caller. Nothing calls Octorate.
/// </summary>
public interface IMarketplaceEntryDetailService
{
    /// <summary>
    /// The detail of the entry <paramref name="code"/> of <paramref name="kind"/>, with its status for the org or, with a
    /// <paramref name="propertyId"/>, for that property (a channel or a tool). The texts are in <paramref name="language"/> (<c>it</c> or
    /// <c>en</c>; a partner falls back to its Italian text). A code that does not exist, a partner that is hidden or that the plan or the country
    /// of the org does not allow, and an entry that goes through Octorate while the connector is off, are all the same 404: guessing a code
    /// learns nothing.
    /// </summary>
    /// <exception cref="Casazen.Core.Exceptions.NotFoundException">
    /// <c>marketplace_entry_not_found</c> for the entry, <c>marketplace_property_not_found</c> for a property that is not one of the org inside
    /// the caller's scope.
    /// </exception>
    Task<MarketplaceEntryDetail> GetAsync(
        Guid orgId,
        HostScope scope,
        MarketplaceActivationKind kind,
        string code,
        Guid? propertyId,
        string language,
        CancellationToken ct = default);
}

/// <summary>How an entry is activated.</summary>
public static class MarketplaceActivationTypes
{
    /// <summary>Through the channel manager's API: a channel or a tool.</summary>
    public const string Api = "api";

    /// <summary>With a request to the channel manager, until the function appears in the license.</summary>
    public const string LicenseRequest = "licenseRequest";

    /// <summary>On the partner's own site.</summary>
    public const string PartnerSite = "partnerSite";
}

/// <summary>One thing the host needs to activate an entry: a stable <see cref="Code"/> for the client and the words.</summary>
public sealed record MarketplaceRequirement(string Code, string Text);

/// <summary>How to activate: the <see cref="Type"/> (<see cref="MarketplaceActivationTypes"/>), what is needed, and whether the host gives access in the channel manager's panel.</summary>
public sealed record MarketplaceActivationInfo(string Type, IReadOnlyList<MarketplaceRequirement> Requirements, bool NeedsOctoratePanel);

/// <summary>How to deactivate: the words, and whether the host must do it on a site (<see cref="Guided"/>).</summary>
public sealed record MarketplaceDeactivation(string Text, bool Guided);

/// <summary>A question and its answer.</summary>
public sealed record MarketplaceFaq(string Q, string A);

/// <summary>
/// The detail card. <see cref="DataShared"/> is a list of plain sentences. <see cref="Cost"/> never invents a figure: its text is <c>null</c> when it
/// is not known. <see cref="WebsiteUrl"/> is the partner's site, <c>null</c> for any other entry.
/// </summary>
public sealed record MarketplaceEntryDetail(
    MarketplaceActivationKind Kind,
    string Code,
    string Name,
    string? LogoUrl,
    MarketplaceCardStatus Status,
    string WhatItDoes,
    string Audience,
    MarketplaceActivationInfo Activation,
    MarketplaceCost Cost,
    IReadOnlyList<string> DataShared,
    MarketplaceDeactivation Deactivation,
    IReadOnlyList<MarketplaceFaq> Faq,
    MarketplaceAction Actions,
    string? WebsiteUrl);
