using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

public readonly record struct MarketplaceAction(bool CanActivate, string? Reason);

/// <summary>
/// The rules of a card of the marketplace that the catalog and the detail card share (E09/F01/US01/T03, T04): pure, so that the two can
/// never answer differently about the same card.
/// </summary>
public static class MarketplaceCardRules
{
    public const int MaxPortalCodeDigits = 18;

    /// <summary>
    /// «Collega da CasaZen» (decision of the product owner, 14/11/2026): the host types in CasaZen the id of its property on the portal, CasaZen
    /// creates the connection on the channel manager, and the host's only step left is to approve the channel manager on the extranet of the OTA.
    /// True only for a sales channel (an OTA) of the catalog that is listed and enabled, that the channel manager lets be created through its API
    /// with a portal code that is a number, and whose connection needs the id of the property and nothing else: no username and password
    /// (<see cref="MarketplacePortal.RequiresPassword"/>) and no access given on the channel manager's own panel
    /// (<see cref="MarketplacePortal.RequiresTokenAuthentication"/>, as for Airbnb). Every other portal keeps the hand-over to the panel.
    /// </summary>
    /// <remarks>
    /// One rule for the catalog (<c>connectByPropertyId</c> of a card) and for the import (<c>POST .../channels/{code}/connect</c>), so the button
    /// and the endpoint never disagree. The code must be a number because the channel manager takes it as one: a code that is not cannot be sent.
    /// </remarks>
    public static bool ConnectsByPropertyId(MarketplacePortal portal)
    {
        ArgumentNullException.ThrowIfNull(portal);
        return portal.Category == MarketplacePortalCategory.Ota
               && portal.IsListed
               && portal.Enabled
               && portal.Creatable
               && portal.RequiresHotelId
               && !portal.RequiresPassword
               && !portal.RequiresTokenAuthentication
               && IsNumericPortalCode(portal.PortalCode);
    }

    public static bool IsNumericPortalCode(string? portalCode) =>
        portalCode is { Length: > 0 and <= MaxPortalCodeDigits } && portalCode.All(char.IsAsciiDigit);

    public static MarketplaceCardStatus CardStatusOf(MarketplaceActivationStatus status) => status switch
    {
        MarketplaceActivationStatus.Active => MarketplaceCardStatus.Active,
        MarketplaceActivationStatus.Requested or MarketplaceActivationStatus.Pending => MarketplaceCardStatus.Pending,
        MarketplaceActivationStatus.ToFix => MarketplaceCardStatus.ToFix,
        _ => MarketplaceCardStatus.NotActive,
    };

    /// <summary>
    /// The status of a card over the activations of several properties: Active when any property of the scope has it, otherwise To fix,
    /// otherwise Pending, otherwise Not active.
    /// </summary>
    public static MarketplaceCardStatus Summarize(IEnumerable<MarketplaceCardStatus> statuses)
    {
        ArgumentNullException.ThrowIfNull(statuses);

        var found = new HashSet<MarketplaceCardStatus>(statuses);
        if (found.Contains(MarketplaceCardStatus.Active))
            return MarketplaceCardStatus.Active;

        if (found.Contains(MarketplaceCardStatus.ToFix))
            return MarketplaceCardStatus.ToFix;

        return found.Contains(MarketplaceCardStatus.Pending) ? MarketplaceCardStatus.Pending : MarketplaceCardStatus.NotActive;
    }

    /// <summary>
    /// Whether a card can be activated. What goes through Octorate (a channel, a tool, a licensed function) needs a connected Octorate account
    /// first; a partner is activated on its own site and never depends on it. A card that is already on, under way or to fix is not activated
    /// again, and a portal Octorate does not let be created through the API (<paramref name="creatable"/>) cannot be.
    /// </summary>
    public static MarketplaceAction ActionOf(
        MarketplaceActivationKind kind, MarketplaceCardStatus status, bool octorateConnected, bool creatable = true)
    {
        if (kind != MarketplaceActivationKind.Partner && !octorateConnected)
            return new MarketplaceAction(false, MarketplaceReasons.OctorateNotConnected);

        return status switch
        {
            MarketplaceCardStatus.Active => new MarketplaceAction(false, MarketplaceReasons.AlreadyActive),
            MarketplaceCardStatus.Pending => new MarketplaceAction(false, MarketplaceReasons.InProgress),
            MarketplaceCardStatus.ToFix => new MarketplaceAction(false, MarketplaceReasons.NeedsFixing),
            _ when !creatable => new MarketplaceAction(false, MarketplaceReasons.NotAvailable),
            _ => new MarketplaceAction(true, null),
        };
    }
}

public static class MarketplaceLicensedFunctions
{
    public static IReadOnlyList<string> Codes { get; } =
    [
        "chat",
        "invoice",
        "fiscalPrinter",
        "smartOffer",
        "octoDaily",
        "rms",
        "businessIntelligence",
        "bookingEngine",
        "octoSite",
        "webConcierge",
        "metaSearch",
        "rateChecker",
    ];

    public static bool IsKnown(string? code) => code is not null && Codes.Contains(code, StringComparer.Ordinal);
}

public static class MarketplaceLanguages
{
    public const string Italian = "it";
    public const string English = "en";

    /// <summary><c>en</c> for English (any <c>en</c>, <c>en-GB</c>, <c>EN</c>), otherwise <c>it</c>: the default of the product.</summary>
    public static string Normalize(string? language)
    {
        var text = language?.Trim();
        return text is { Length: >= 2 } && text.StartsWith(English, StringComparison.OrdinalIgnoreCase) && (text.Length == 2 || text[2] is '-' or '_')
            ? English
            : Italian;
    }
}
