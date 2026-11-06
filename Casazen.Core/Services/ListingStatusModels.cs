using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

/// <summary>The wizard steps a listing can lack data in (<see cref="ListingChannelStatus.Missing"/>): «Fix» opens the step that owns the missing data.</summary>
public static class ListingStatusSteps
{
    /// <summary>The data of the property: address and comune, rooms and beds, the CIN where the channel asks for it.</summary>
    public const string Base = "base";

    public const string Photos = "photos";

    /// <summary>The Italian and English texts.</summary>
    public const string Descriptions = "descriptions";

    public const string Amenities = "amenities";

    public const string Rules = "rules";

    public const string Prices = "prices";
}

/// <summary>
/// The state of the listing of a property on one channel, as the host reads it (E02/F03/US01/T07, NF-09, NF-27, NF-30, NF-38): where its content
/// stands and why, how complete it is for this channel, what lacks and where it is fixed, when the channel last confirmed it, and whether somebody
/// changed it outside CasaZen. No text of the listing and no personal data.
/// </summary>
/// <param name="ChannelCode">The code of the portal.</param>
/// <param name="Status">Where the content stands.</param>
/// <param name="Completeness">0 to 100: the share of what the rule of the channel asks for that the listing has (Italian and English texts, the photos, the rooms and beds, the address and comune, a valid CIN where the channel asks for it). Worked out when read, never stored.</param>
/// <param name="Missing">The wizard steps that lack data (<see cref="ListingStatusSteps"/>).</param>
/// <param name="LastSyncedAt">The last time the channel manager confirmed the content (UTC); <c>null</c> = never.</param>
/// <param name="SyncLagSeconds">Seconds since <paramref name="LastSyncedAt"/>; <c>null</c> = never confirmed.</param>
/// <param name="SyncAlert">The channel manager has not confirmed an accepted or published channel for more than 24 hours.</param>
/// <param name="ErrorCode">The stable code of what is wrong (<see cref="ListingChannelErrors"/>); <c>null</c> while nothing is.</param>
/// <param name="ErrorDetail">What the channel said when it refused the content (its own words, short); <c>null</c> for any other code.</param>
/// <param name="ReasonAmount">The number the reason speaks of (the fewest photos of the channel); <c>null</c> when the reason has none.</param>
/// <param name="ChangedOutside">The groups of the content that changed outside CasaZen; empty = none.</param>
public sealed record ListingChannelStatus(
    string ChannelCode,
    ChannelPublishStatus Status,
    int Completeness,
    IReadOnlyList<string> Missing,
    DateTime? LastSyncedAt,
    long? SyncLagSeconds,
    bool SyncAlert,
    string? ErrorCode,
    string? ErrorDetail,
    int? ReasonAmount,
    IReadOnlyList<string> ChangedOutside);

/// <summary>The channels of one property.</summary>
/// <param name="PropertyId">The property.</param>
/// <param name="Channels">One for each channel row, by code.</param>
/// <param name="CinNotSentTo">The content channels the CIN is not sent to: the CIN stays in CasaZen until D2 is answered, so every content channel says it (AC-NF30-2).</param>
public sealed record ListingStatusView(Guid PropertyId, IReadOnlyList<ListingChannelStatus> Channels, IReadOnlyList<string> CinNotSentTo);

/// <summary>The worst state of a property across its channels, for the property list.</summary>
/// <param name="PropertyId">The property.</param>
/// <param name="WorstStatus">The state that needs the most attention: to fix, then sending, accepted, draft, published, managed on the channel.</param>
/// <param name="SyncAlert">At least one channel was not confirmed for more than 24 hours.</param>
public sealed record ListingPropertySummary(Guid PropertyId, ChannelPublishStatus WorstStatus, bool SyncAlert);

/// <summary>The person's choice about a change made outside CasaZen (NF-27).</summary>
public enum OutsideChangeChoice
{
    /// <summary>Keep the CasaZen version: it is published again over the change (<c>overwrite</c>).</summary>
    Keep = 0,

    /// <summary>Take the descriptions the channel manager holds: they become the texts of the listing in CasaZen.</summary>
    Take = 1,
}
