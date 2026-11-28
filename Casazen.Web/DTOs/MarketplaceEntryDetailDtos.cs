using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;

namespace Casazen.Web.DTOs;

/// <summary>
/// <c>GET api/marketplace/entries/{kind}/{code}</c> (E09/F01/US01/T04, NF-55): the detail card of an entry. <c>whatItDoes</c> and <c>audience</c>
/// say what it does and for whom; <c>activation</c> how it is activated and what is needed; <c>cost</c> what it costs and who invoices (<c>text</c> is
/// <c>null</c> when nobody knows: the page says "not specified", a figure is never invented); <c>dataShared</c> plain sentences about the data that is
/// shared; <c>deactivation</c> how to turn it off; <c>faq</c> the questions; <c>actions</c> whether it can be activated now and, when not, why (a
/// code). <c>websiteUrl</c> is the partner's site and <c>null</c> for any other entry. The texts are in the language asked for.
/// </summary>
public sealed record MarketplaceEntryDetailDto(
    MarketplaceActivationKind Kind,
    string Code,
    string Name,
    string? LogoUrl,
    MarketplaceCardStatus Status,
    string WhatItDoes,
    string Audience,
    MarketplaceActivationInfoDto Activation,
    MarketplaceCostDto Cost,
    IReadOnlyList<string> DataShared,
    MarketplaceDeactivationDto Deactivation,
    IReadOnlyList<MarketplaceFaqDto> Faq,
    MarketplaceDetailActionsDto Actions,
    string? WebsiteUrl)
{
    public static MarketplaceEntryDetailDto From(MarketplaceEntryDetail detail)
    {
        ArgumentNullException.ThrowIfNull(detail);
        return new MarketplaceEntryDetailDto(
            detail.Kind,
            detail.Code,
            detail.Name,
            detail.LogoUrl,
            detail.Status,
            detail.WhatItDoes,
            detail.Audience,
            new MarketplaceActivationInfoDto(
                detail.Activation.Type,
                [.. detail.Activation.Requirements.Select(requirement => new MarketplaceRequirementDto(requirement.Code, requirement.Text))],
                detail.Activation.NeedsOctoratePanel),
            new MarketplaceCostDto(detail.Cost.Text, detail.Cost.InvoicedBy),
            detail.DataShared,
            new MarketplaceDeactivationDto(detail.Deactivation.Text, detail.Deactivation.Guided),
            [.. detail.Faq.Select(faq => new MarketplaceFaqDto(faq.Q, faq.A))],
            new MarketplaceDetailActionsDto(detail.Actions.CanActivate, detail.Actions.Reason),
            detail.WebsiteUrl);
    }
}

public sealed record MarketplaceActivationInfoDto(string Type, IReadOnlyList<MarketplaceRequirementDto> Requirements, bool NeedsOctoratePanel);

public sealed record MarketplaceRequirementDto(string Code, string Text);

public sealed record MarketplaceDeactivationDto(string Text, bool Guided);

public sealed record MarketplaceFaqDto(string Q, string A);

public sealed record MarketplaceDetailActionsDto(bool CanActivate, string? Reason);
