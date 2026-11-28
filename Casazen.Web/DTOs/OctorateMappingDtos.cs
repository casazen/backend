using System.ComponentModel.DataAnnotations;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;

namespace Casazen.Web.DTOs;

/// <summary>
/// A listing found on the channel manager and its mapping (<c>GET api/octorate/properties</c>): what Octorate says about it, the
/// proposal or the confirmed property, the candidates, the rooms and the channels. No personal data.
/// </summary>
/// <param name="OctorateAccommodationId">The accommodation id on Octorate, the key of the mapping endpoints.</param>
/// <param name="Name">The name of the listing on Octorate.</param>
/// <param name="Address">The address of the listing on Octorate.</param>
/// <param name="City">The city of the listing on Octorate.</param>
/// <param name="MaxGuests">The most guests a room of the listing takes, when known.</param>
/// <param name="Status">Proposed, Confirmed, Ambiguous or Rejected.</param>
/// <param name="Score">The score of the best candidate, 0 to 100.</param>
/// <param name="ProposedProperty">The proposed property, or the confirmed one; <c>null</c> for an ambiguous row or an import offer.</param>
/// <param name="Candidates">The candidates of an ambiguous row the caller reaches.</param>
/// <param name="Channels">The channels the listing is on, with its id on each and the state.</param>
/// <param name="Rooms">The rooms of the listing (product id and name), when read.</param>
/// <param name="OctorateProductId">The chosen Octorate room, <c>null</c> until set.</param>
/// <param name="OfferedForImport">True when no CasaZen property matches: the host can import the listing.</param>
/// <param name="ConfirmedAt">When a person confirmed the mapping (UTC).</param>
public sealed record OctorateListingDto(
    string OctorateAccommodationId,
    string? Name,
    string? Address,
    string? City,
    int? MaxGuests,
    OctorateMappingStatus Status,
    int Score,
    OctoratePropertySummaryDto? ProposedProperty,
    IReadOnlyList<OctoratePropertySummaryDto> Candidates,
    IReadOnlyList<OctorateChannelDto> Channels,
    IReadOnlyList<OctorateRoomDto> Rooms,
    string? OctorateProductId,
    bool OfferedForImport,
    DateTime? ConfirmedAt)
{
    public static OctorateListingDto From(ChannelMappingRow row, Func<Guid, bool> reaches)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(reaches);
        return new OctorateListingDto(
            row.OctorateAccommodationId,
            row.Name,
            row.Address,
            row.City,
            row.MaxGuests,
            row.Status,
            row.MatchScore,
            row.Property is { } property ? OctoratePropertySummaryDto.From(property) : null,
            [.. row.Candidates.Where(candidate => reaches(candidate.Id)).Select(OctoratePropertySummaryDto.From)],
            [.. row.Channels.Select(channel => new OctorateChannelDto(channel.ChannelId, channel.ExternalId, OctorateChannelDto.ConnectedState))],
            [.. row.Rooms.Select(room => new OctorateRoomDto(room.Id, room.Name, room.MaxGuests))],
            row.OctorateProductId,
            row.OfferedForImport,
            row.ConfirmedAt);
    }
}

public sealed record OctoratePropertySummaryDto(Guid Id, string Name, string Address, string City)
{
    public static OctoratePropertySummaryDto From(ChannelMappingProperty property) =>
        new(property.Id, property.Name, property.Address, property.City);
}

public sealed record OctorateChannelDto(string ChannelId, string ExternalId, string State)
{
    public const string ConnectedState = "Connected";
}

public sealed record OctorateRoomDto(string Id, string? Name, int? MaxGuests);

public sealed class ConfirmMappingRequest
{
    [Required(ErrorMessage = "OctorateMappingPropertyRequired")]
    public Guid? PropertyId { get; set; }

    [MaxLength(64, ErrorMessage = "OctorateRoomInvalid")]
    public string? OctorateProductId { get; set; }
}

public sealed class SetRoomRequest
{
    [Required(ErrorMessage = "OctorateRoomRequired")]
    [MaxLength(64, ErrorMessage = "OctorateRoomInvalid")]
    public string OctorateProductId { get; set; } = string.Empty;
}

public sealed record ImportListingResponse(Guid PropertyId);
