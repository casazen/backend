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
    public const string AccommodationIndexName = "UIX_OctorateListingMappings_OrgId_ConnectionId_Accommodation";

    public const string ConfirmedPropertyIndexName = "UIX_OctorateListingMappings_OrgId_PropertyId_Confirmed";

    public const int AccommodationIdMaxLength = 64;
    public const int ProductIdMaxLength = 64;
    public const int SnapshotNameMaxLength = 200;
    public const int SnapshotAddressMaxLength = 300;
    public const int SnapshotCityMaxLength = 100;
    public const int UserIdMaxLength = 255;

    public const string EmptyJsonArray = "[]";

    public const string EmptyJsonObject = "{}";

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrgId { get; set; }

    public Guid ConnectionId { get; set; }

    [Required]
    [MaxLength(AccommodationIdMaxLength)]
    public string OctorateAccommodationId { get; set; } = string.Empty;

    [MaxLength(SnapshotNameMaxLength)]
    public string? SnapshotName { get; set; }

    [MaxLength(SnapshotAddressMaxLength)]
    public string? SnapshotAddress { get; set; }

    [MaxLength(SnapshotCityMaxLength)]
    public string? SnapshotCity { get; set; }

    public int? SnapshotMaxGuests { get; set; }

    [Required]
    public string SnapshotRoomsJson { get; set; } = EmptyJsonArray;

    /// <summary>
    /// The Octorate room (product) of the confirmed listing, which reservations, availability checks and calendar calls need next to
    /// the accommodation id. A property is one unit (PO-13): set automatically when the accommodation has exactly one room, chosen by
    /// the host otherwise; never guessed. <c>null</c> until then.
    /// </summary>
    [MaxLength(ProductIdMaxLength)]
    public string? OctorateProductId { get; set; }

    public Guid? PropertyId { get; set; }

    public OctorateMappingStatus Status { get; set; } = OctorateMappingStatus.Proposed;

    public int MatchScore { get; set; }

    [Required]
    public string CandidatePropertyIdsJson { get; set; } = EmptyJsonArray;

    /// <summary>
    /// Portal code to the external listing id, exactly as read from Octorate (jsonb, decision D6): <c>{ "142": "1234567" }</c>.
    /// Never regenerated.
    /// </summary>
    [Required]
    public string ChannelIdsJson { get; set; } = EmptyJsonObject;

    [MaxLength(UserIdMaxLength)]
    public string? ConfirmedByUserId { get; set; }

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
