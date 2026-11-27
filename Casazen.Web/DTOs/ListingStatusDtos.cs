using System.ComponentModel.DataAnnotations;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Web.Resources;
using Microsoft.Extensions.Localization;

namespace Casazen.Web.DTOs;

/// <summary>
/// The state of the listing of a property on one channel (<c>GET api/properties/{id}/channel-status</c>, E02/F03/US01/T07, NF-09, NF-38): where its
/// content stands and why in simple words, how complete it is for the channel, the wizard steps that lack data, when the channel manager last
/// confirmed it, the lag and its alert, and the groups that changed outside CasaZen. No text of the listing and no personal data.
/// </summary>
/// <param name="Channel">The code of the portal.</param>
/// <param name="Status">Where the content stands.</param>
/// <param name="Completeness">0 to 100: how much of what the channel asks for the listing has.</param>
/// <param name="Missing">The wizard steps that lack data (<c>base</c>, <c>photos</c>, <c>descriptions</c>, <c>amenities</c>, <c>rules</c>, <c>prices</c>), so «Fix» opens the right step.</param>
/// <param name="LastSyncedAt">The last time the channel manager confirmed the content (UTC); <c>null</c> = never.</param>
/// <param name="SyncLagSeconds">Seconds since that confirmation; <c>null</c> = never confirmed.</param>
/// <param name="SyncAlert">The channel was not confirmed for more than 24 hours.</param>
/// <param name="ErrorCode">The stable code of what is wrong; <c>null</c> while nothing is.</param>
/// <param name="Reason">What is wrong, in simple words, in the language of the request; <c>null</c> while nothing is.</param>
/// <param name="ReasonDetail">What the channel said when it refused the content (its own words); <c>null</c> for any other reason.</param>
/// <param name="ChangedOutside">The groups of the content that changed outside CasaZen (<c>descriptions</c>, <c>rooms</c>, <c>property</c>); empty = none.</param>
public sealed record ListingChannelStatusDto(
    string Channel,
    ChannelPublishStatus Status,
    int Completeness,
    IReadOnlyList<string> Missing,
    DateTime? LastSyncedAt,
    long? SyncLagSeconds,
    bool SyncAlert,
    string? ErrorCode,
    string? Reason,
    string? ReasonDetail,
    IReadOnlyList<string> ChangedOutside)
{
    /// <summary>The key of the message for a code nobody wrote words for: the row still says something, never a bare «error».</summary>
    public const string GenericReasonKey = "ListingChannelErrorGeneric";

    public static ListingChannelStatusDto From(ListingChannelStatus status, IStringLocalizer<SharedResources> localizer)
    {
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(localizer);

        return new ListingChannelStatusDto(
            status.ChannelCode,
            status.Status,
            status.Completeness,
            status.Missing,
            status.LastSyncedAt,
            status.SyncLagSeconds,
            status.SyncAlert,
            status.ErrorCode,
            ReasonOf(status, localizer),
            status.ErrorDetail,
            status.ChangedOutside);
    }

    private static string? ReasonOf(ListingChannelStatus status, IStringLocalizer<SharedResources> localizer)
    {
        if (status.ErrorCode is null)
            return null;

        var key = ListingChannelErrors.MessageKey(status.ErrorCode) ?? GenericReasonKey;
        var text = status.ReasonAmount is { } amount ? localizer[key, amount] : localizer[key];
        return text.ResourceNotFound ? localizer[GenericReasonKey].Value : text.Value;
    }
}

/// <summary>The channels of a property (<c>GET api/properties/{id}/channel-status</c>).</summary>
/// <param name="PropertyId">The property.</param>
/// <param name="Channels">One for each channel the property was published to, or is managed on.</param>
/// <param name="CinNotSentTo">The channels the CIN is not sent to: it stays in CasaZen until the field that carries it is settled with the channel manager.</param>
public sealed record ListingStatusResponse(Guid PropertyId, IReadOnlyList<ListingChannelStatusDto> Channels, IReadOnlyList<string> CinNotSentTo)
{
    public static ListingStatusResponse From(ListingStatusView view, IStringLocalizer<SharedResources> localizer)
    {
        ArgumentNullException.ThrowIfNull(view);
        return new ListingStatusResponse(
            view.PropertyId,
            [.. view.Channels.Select(channel => ListingChannelStatusDto.From(channel, localizer))],
            view.CinNotSentTo);
    }
}

/// <summary>The worst state of a property across its channels (<c>GET api/properties/channel-status</c>), for the property list.</summary>
/// <param name="PropertyId">The property.</param>
/// <param name="WorstStatus">The state that needs the most attention.</param>
/// <param name="SyncAlert">At least one channel was not confirmed for more than 24 hours.</param>
public sealed record ListingPropertySummaryDto(Guid PropertyId, ChannelPublishStatus WorstStatus, bool SyncAlert)
{
    public static ListingPropertySummaryDto From(ListingPropertySummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        return new ListingPropertySummaryDto(summary.PropertyId, summary.WorstStatus, summary.SyncAlert);
    }
}

/// <summary>The person's choice about a change made outside CasaZen (<c>POST api/properties/{id}/channels/{code}/outside-change/resolve</c>).</summary>
public sealed class ResolveOutsideChangeRequest
{
    /// <summary><c>keep</c>: publish CasaZen's version again over the change; <c>take</c>: adopt the descriptions the channel manager holds.</summary>
    [Required(ErrorMessage = "OutsideChangeChoiceInvalid")]
    [RegularExpression("^(?i:keep|take)$", ErrorMessage = "OutsideChangeChoiceInvalid")]
    public string? Choice { get; set; }

    /// <summary>The choice as the service takes it.</summary>
    public OutsideChangeChoice ToChoice() =>
        string.Equals(Choice?.Trim(), "take", StringComparison.OrdinalIgnoreCase) ? OutsideChangeChoice.Take : OutsideChangeChoice.Keep;
}
