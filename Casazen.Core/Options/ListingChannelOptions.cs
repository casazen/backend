namespace Casazen.Core.Options;

/// <summary>
/// The <c>ListingChannels</c> section (E02/F03/US01/T02, NF-27, NF-29, ADR-034): the content channels of the channel manager, keyed by the code of the
/// portal (<c>booking_xml</c>, <c>airbnb_xml</c>, <c>homeaway</c>), each with the rules a listing must meet to be sent to it. Defaults are committed in
/// <c>appsettings.json</c> for the three portals that have a push queue; the others are added after the sandbox read of
/// <c>GET /rest/v1/meta/portals</c>. A channel the property is connected to that has no key here is managed on the channel itself (Expedia): its row is
/// <c>ManagedOnChannel</c> and nothing is sent to it.
/// </summary>
/// <remarks>
/// The CIN is never sent to a channel until the field that carries it is settled with Octorate (D2: <c>legalId</c>, <c>legalIdIssuer</c> and
/// <c>taxId</c> are the candidates). <see cref="ListingChannelRule.RequiresCin"/> only says that a valid CIN must exist in CasaZen before the listing
/// is sent to the channel, because the channel will ask for it.
/// </remarks>
public sealed class ListingChannelOptions
{
    public const string SectionName = "ListingChannels";

    /// <summary>The channels that receive content, by the code of the portal, compared without regard to case.</summary>
    public Dictionary<string, ListingChannelRule> Channels { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Whether the amenities are sent (<c>POST .../amenities</c>). Off: the codes of <c>AmenityCatalogMapper</c> were not compared with the answer of
    /// the sandbox yet (<c>AmenityCatalogMapper.ConfirmedOnSandbox</c>), so nothing is sent for them. Turn it on only after that comparison.
    /// </summary>
    public bool SendAmenities { get; set; }

    /// <summary>
    /// Whether the cancellation policy is sent (<c>POST .../cancellation</c>). Off: the members of that part of the API were not proven on the
    /// sandbox (S25), so nothing is invented for them. Turn it on only after a run on a licensed structure.
    /// </summary>
    public bool SendCancellationPolicy { get; set; }

    /// <summary>The rule of a channel, or <c>null</c> when the code is not a content channel.</summary>
    public ListingChannelRule? RuleOf(string? channelCode) =>
        !string.IsNullOrWhiteSpace(channelCode) && Channels.TryGetValue(channelCode.Trim(), out var rule) ? rule : null;

    /// <summary>The portal code that goes with the numeric channel id of a connection (<c>142</c> is <c>booking_xml</c>); <c>null</c> when no content channel has it.</summary>
    public string? PortalCodeOfChannelId(string? channelId)
    {
        if (string.IsNullOrWhiteSpace(channelId))
            return null;

        foreach (var (code, rule) in Channels)
        {
            if (string.Equals(rule.ChannelId?.Trim(), channelId.Trim(), StringComparison.Ordinal))
                return code.Trim().ToLowerInvariant();
        }

        return null;
    }
}

/// <summary>What a listing must meet to be sent to one content channel.</summary>
public sealed class ListingChannelRule
{
    /// <summary>The id of the channel in the connections of the channel manager (<c>142</c> for Booking.com), to find the connection of a property.</summary>
    public string? ChannelId { get; set; }

    /// <summary>The fewest photos the channel takes; a listing with fewer is left <c>ToFix</c> for this channel.</summary>
    public int MinPhotos { get; set; }

    /// <summary>The channel asks for the CIN of the property: a listing without a valid one is left <c>ToFix</c> for this channel.</summary>
    public bool RequiresCin { get; set; }

    /// <summary>The portal has a push queue of its own (<c>pushQueue/{portal}</c>); otherwise the push goes through the connection.</summary>
    public bool UsesPushQueue { get; set; }
}
