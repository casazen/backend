using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;

namespace Casazen.Web.DTOs;

public sealed record MarketplacePartnerBriefDto(
    string Code,
    string Name,
    string WebsiteUrl,
    IReadOnlyList<string> Checklist,
    MarketplacePartnerPropertyDataDto? PropertyData)
{
    public static MarketplacePartnerBriefDto From(MarketplacePartnerBrief brief)
    {
        ArgumentNullException.ThrowIfNull(brief);
        return new MarketplacePartnerBriefDto(
            brief.Code,
            brief.Name,
            brief.WebsiteUrl,
            brief.Checklist,
            brief.PropertyData is { } data ? MarketplacePartnerPropertyDataDto.From(data) : null);
    }
}

/// <summary>
/// The property's fields the brief may carry, and no others: <c>name</c>, <c>address</c>, <c>city</c>, <c>postalCode</c>, <c>bedrooms</c>,
/// <c>bathrooms</c>, <c>maxGuests</c> and <c>cinCode</c>, which is present only when the CIN is valid (an invalid or missing one is <c>null</c>).
/// </summary>
public sealed record MarketplacePartnerPropertyDataDto(
    string Name,
    string Address,
    string City,
    string PostalCode,
    int Bedrooms,
    int Bathrooms,
    int MaxGuests,
    string? CinCode)
{
    public static MarketplacePartnerPropertyDataDto From(MarketplacePartnerPropertyData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return new MarketplacePartnerPropertyDataDto(
            data.Name, data.Address, data.City, data.PostalCode, data.Bedrooms, data.Bathrooms, data.MaxGuests, data.CinCode);
    }
}

public sealed class DeclareMarketplacePartnerRequest
{
    public Guid? PropertyId { get; set; }

    public DateOnly? DeclaredOn { get; set; }
}

/// <summary>
/// The answer of <c>POST api/marketplace/partners/{code}/declare</c>: the activation as the declaration left it. <c>status</c> is <c>Active</c> after a
/// declaration; <c>isDeclared</c> is true while only the host's word backs it, and <c>verifiedAt</c> is set (with <c>isDeclared</c> false) only when a
/// connection showed the partner active, so a client never says «verified» on its own. <c>declaredAt</c> is the day the host gave, as midnight UTC of that
/// day (show its date). Declaring again returns the same activation, unchanged.
/// </summary>
public sealed record MarketplacePartnerActivationDto(
    Guid ActivationId,
    string Code,
    Guid? PropertyId,
    MarketplaceActivationStatus Status,
    bool IsDeclared,
    DateTime? DeclaredAt,
    DateTime? VerifiedAt)
{
    public static MarketplacePartnerActivationDto From(MarketplacePartnerActivation activation)
    {
        ArgumentNullException.ThrowIfNull(activation);
        return new MarketplacePartnerActivationDto(
            activation.ActivationId,
            activation.PartnerCode,
            activation.PropertyId,
            activation.Status,
            activation.IsDeclared,
            activation.DeclaredAt,
            activation.VerifiedAt);
    }
}
