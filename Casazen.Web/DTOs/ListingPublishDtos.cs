using System.ComponentModel.DataAnnotations;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;

namespace Casazen.Web.DTOs;

public sealed class ListingPublishRequest
{
    /// <summary>
    /// The codes of the portals to publish to (<c>booking_xml</c>, <c>airbnb_xml</c>, <c>homeaway</c>). At least one: an empty list, or none at all, is
    /// refused by the service with 422 <c>listing_channels_required</c>, never read as «all the channels». The body is not validated for it, so the
    /// answer is the 422 the contract names and not a 400 of the model.
    /// </summary>
    [MaxLength(20, ErrorMessage = "ListingChannelUnknown")]
    public List<string>? Channels { get; set; }

    public bool Overwrite { get; set; }
}

public sealed record ListingPublishChannelDto(string Channel, ChannelPublishStatus Status, string? ErrorCode)
{
    public static ListingPublishChannelDto From(ListingChannelResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return new ListingPublishChannelDto(result.ChannelCode, result.Status, result.ErrorCode);
    }
}

public sealed record ListingPublishResponse(Guid PropertyId, IReadOnlyList<ListingPublishChannelDto> Channels);
