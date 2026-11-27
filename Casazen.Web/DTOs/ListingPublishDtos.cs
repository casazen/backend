using System.ComponentModel.DataAnnotations;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;

namespace Casazen.Web.DTOs;

/// <summary>Publishes a property to the channels (<c>POST api/properties/{id}/publish</c>, E02/F03/US01/T02).</summary>
public sealed class ListingPublishRequest
{
    /// <summary>
    /// The codes of the portals to publish to (<c>booking_xml</c>, <c>airbnb_xml</c>, <c>homeaway</c>). At least one: an empty list, or none at all, is
    /// refused by the service with 422 <c>listing_channels_required</c>, never read as «all the channels». The body is not validated for it, so the
    /// answer is the 422 the contract names and not a 400 of the model.
    /// </summary>
    [MaxLength(20, ErrorMessage = "ListingChannelUnknown")]
    public List<string>? Channels { get; set; }

    /// <summary>
    /// The person's choice to write CasaZen's version over a change made outside CasaZen (NF-27). Without it a channel that was changed outside
    /// CasaZen is refused with 409 <c>listing_modified_outside</c>.
    /// </summary>
    public bool Overwrite { get; set; }
}

/// <summary>One channel of a publication that was queued: its code and where its content stands now (<c>Sending</c>).</summary>
/// <param name="Channel">The code of the portal.</param>
/// <param name="Status">Where the content stands on the channel.</param>
/// <param name="ErrorCode">The stable code of what is wrong; <c>null</c> while nothing is.</param>
public sealed record ListingPublishChannelDto(string Channel, ChannelPublishStatus Status, string? ErrorCode)
{
    public static ListingPublishChannelDto From(ListingChannelResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return new ListingPublishChannelDto(result.ChannelCode, result.Status, result.ErrorCode);
    }
}

/// <summary>The answer of the publication (202): the channel rows now <c>Sending</c>; the wizard polls the status endpoint for the rest.</summary>
/// <param name="PropertyId">The property.</param>
/// <param name="Channels">The channels asked for.</param>
public sealed record ListingPublishResponse(Guid PropertyId, IReadOnlyList<ListingPublishChannelDto> Channels);
