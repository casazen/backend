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

public static class MarketplaceActivationTypes
{
    public const string Api = "api";

    public const string LicenseRequest = "licenseRequest";

    public const string PartnerSite = "partnerSite";
}

public sealed record MarketplaceRequirement(string Code, string Text);

public sealed record MarketplaceActivationInfo(string Type, IReadOnlyList<MarketplaceRequirement> Requirements, bool NeedsOctoratePanel);

public sealed record MarketplaceDeactivation(string Text, bool Guided);

public sealed record MarketplaceFaq(string Q, string A);

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
