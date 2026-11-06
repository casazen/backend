using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

/// <summary>What a person can do with a card: whether it can be activated now and, when not, the code of why (<see cref="MarketplaceReasons"/>).</summary>
public readonly record struct MarketplaceAction(bool CanActivate, string? Reason);

/// <summary>
/// The rules of a card of the marketplace that the catalog and the detail card share (E09/F01/US01/T03, T04): pure, so that the two can
/// never answer differently about the same card.
/// </summary>
public static class MarketplaceCardRules
{
    /// <summary>
    /// How the card shows an activation: Active as Active, Requested and Pending as Pending, To fix as To fix, and Deactivated as Not active.
    /// </summary>
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

/// <summary>
/// The functions of the channel manager's license the marketplace offers, a fixed list (the codes are the modules of the license). Their
/// names and one-line benefits are texts of the resource files, <c>Marketplace.Licensed.{code}.Name</c> and <c>.Benefit</c>, in Italian and
/// English; the cost of none of them is specified.
/// </summary>
public static class MarketplaceLicensedFunctions
{
    /// <summary>The codes, in the order the catalog lists them.</summary>
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

    /// <summary>True for a code of the list (exact, case-sensitive).</summary>
    public static bool IsKnown(string? code) => code is not null && Codes.Contains(code, StringComparer.Ordinal);
}

/// <summary>The two languages of the marketplace texts.</summary>
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
