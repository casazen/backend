using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

/// <summary>
/// The mapping between a listing (accommodation) the channel manager knows and a CasaZen property of the org (ADR-023, ADR-027,
/// E02/F01/US02/T02): one row per listing found through a connection, with a snapshot of what Octorate says about it, the proposal
/// of the matcher, and the person who confirmed it. Nothing is written to Octorate for a property until its mapping is
/// <see cref="OctorateMappingStatus.Confirmed"/> (OCT-04).
/// </summary>
/// <remarks>
/// <para>Unique per org, connection and accommodation (<see cref="AccommodationIndexName"/>). A property is confirmed to one listing
/// only: the unique index <see cref="ConfirmedPropertyIndexName"/> covers the rows with <c>Status = 2</c>.</para>
/// <para><see cref="ChannelIdsJson"/> keeps the identifiers the listing already has on each channel exactly as Octorate gives them,
/// never regenerated (decision D6): the listing keeps its reviews and history on the channel.</para>
/// <para>No personal data: the snapshot is the listing's public description (name, address, city, guests, rooms).</para>
/// </remarks>
[Table("OctorateListingMappings")]
public class OctorateListingMapping : ITenantOwned
{
    /// <summary>Name of the unique index on (<see cref="OrgId"/>, <see cref="ConnectionId"/>, <see cref="OctorateAccommodationId"/>).</summary>
    public const string AccommodationIndexName = "UIX_OctorateListingMappings_OrgId_ConnectionId_Accommodation";

    /// <summary>Name of the unique index on (<see cref="OrgId"/>, <see cref="PropertyId"/>) of the confirmed rows.</summary>
    public const string ConfirmedPropertyIndexName = "UIX_OctorateListingMappings_OrgId_PropertyId_Confirmed";

    public const int AccommodationIdMaxLength = 64;
    public const int ProductIdMaxLength = 64;
    public const int SnapshotNameMaxLength = 200;
    public const int SnapshotAddressMaxLength = 300;
    public const int SnapshotCityMaxLength = 100;
    public const int UserIdMaxLength = 255;

    /// <summary>An empty JSON array, the value of a list column with nothing in it.</summary>
    public const string EmptyJsonArray = "[]";

    /// <summary>An empty JSON object, the value of <see cref="ChannelIdsJson"/> before the identifiers are read.</summary>
    public const string EmptyJsonObject = "{}";

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Tenant: the org of the connection the listing was found through.</summary>
    public Guid OrgId { get; set; }

    /// <summary>The connection the listing was found through (FK, RESTRICT: a connection with mappings is never deleted).</summary>
    public Guid ConnectionId { get; set; }

    /// <summary>The accommodation id on Octorate.</summary>
    [Required]
    [MaxLength(AccommodationIdMaxLength)]
    public string OctorateAccommodationId { get; set; } = string.Empty;

    /// <summary>The name of the listing as Octorate says it, so the mapping screen does not call Octorate each time.</summary>
    [MaxLength(SnapshotNameMaxLength)]
    public string? SnapshotName { get; set; }

    [MaxLength(SnapshotAddressMaxLength)]
    public string? SnapshotAddress { get; set; }

    [MaxLength(SnapshotCityMaxLength)]
    public string? SnapshotCity { get; set; }

    /// <summary>The most guests a room of the listing takes, when Octorate says it.</summary>
    public int? SnapshotMaxGuests { get; set; }

    /// <summary>
    /// The rooms (products) of the accommodation as JSON (jsonb): <c>[{ "id": "956528", "name": "Double Room", "maxGuests": 2 }]</c>,
    /// the choice of <see cref="OctorateProductId"/>.
    /// </summary>
    [Required]
    public string SnapshotRoomsJson { get; set; } = EmptyJsonArray;

    /// <summary>
    /// The Octorate room (product) of the confirmed listing, which reservations, availability checks and calendar calls need next to
    /// the accommodation id. A property is one unit (PO-13): set automatically when the accommodation has exactly one room, chosen by
    /// the host otherwise; never guessed. <c>null</c> until then.
    /// </summary>
    [MaxLength(ProductIdMaxLength)]
    public string? OctorateProductId { get; set; }

    /// <summary>The CasaZen property: proposed (<see cref="OctorateMappingStatus.Proposed"/>) or confirmed. FK, SET NULL.</summary>
    public Guid? PropertyId { get; set; }

    public OctorateMappingStatus Status { get; set; } = OctorateMappingStatus.Proposed;

    /// <summary>The score of the best candidate, 0 to 100.</summary>
    public int MatchScore { get; set; }

    /// <summary>The candidate properties of an ambiguous row, as a JSON array of ids (jsonb).</summary>
    [Required]
    public string CandidatePropertyIdsJson { get; set; } = EmptyJsonArray;

    /// <summary>
    /// Portal code to the external listing id, exactly as read from Octorate (jsonb, decision D6): <c>{ "142": "1234567" }</c>.
    /// Never regenerated.
    /// </summary>
    [Required]
    public string ChannelIdsJson { get; set; } = EmptyJsonObject;

    /// <summary>Who confirmed the mapping (the user id), <c>null</c> until then.</summary>
    [MaxLength(UserIdMaxLength)]
    public string? ConfirmedByUserId { get; set; }

    /// <summary>When the mapping was confirmed (UTC).</summary>
    public DateTime? ConfirmedAt { get; set; }

    /// <summary>
    /// The instant (UTC) at which the last complete read of the reservations of this listing started (E02/F02/US01/T01): the import and the
    /// reconciliation read again from it, minus an overlap of two days (the retry window of the webhooks). <c>null</c> until a full import of
    /// the listing completed: the next run then reads the whole history. It moves only when a run read everything it had to, so a run
    /// stopped by the rate limit or by an error never leaves a gap behind it.
    /// </summary>
    public DateTime? BookingsImportedThrough { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
