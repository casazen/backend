using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

/// <summary>
/// One external item of an import (D6, E02/F01/US03): the connection of a listing to the channel, with the stamp of each operation done for
/// it. The stamps are what makes the import idempotent: an operation with a stamp is never asked again, whatever repeats (a command, a
/// callback, a restart of the worker), so each external item is imported and marked at most once.
/// </summary>
/// <remarks>
/// <para>Unique per channel row and connection (<see cref="ChannelConnectionIndexName"/>). The same connection of Octorate in a later batch
/// takes the stamps of the earlier row before any call (the org-wide read of <see cref="OrgConnectionIndexName"/>), so a second batch does
/// not do again what the first did.</para>
/// <para>The ids are the ones Octorate gave (accommodation, connection); nothing else of its answers is kept. The mapping of the listing to a
/// CasaZen property is not here: it is the row of <c>OctorateListingMapping</c> with the same accommodation id.</para>
/// </remarks>
[Table("OctorateInventoryImportItems")]
public class OctorateInventoryImportItem : ITenantOwned
{
    public const string ChannelConnectionIndexName = "UIX_OctorateInventoryImportItems_ChannelId_ConnectionId";

    public const string OrgConnectionIndexName = "IX_OctorateInventoryImportItems_OrgId_ConnectionId";

    public const int IdMaxLength = 64;

    public const int ErrorCodeMaxLength = 60;

    public const int UserIdMaxLength = 255;

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrgId { get; set; }

    public Guid ChannelId { get; set; }

    [Required]
    [MaxLength(IdMaxLength)]
    public string OctorateAccommodationId { get; set; } = string.Empty;

    [Required]
    [MaxLength(IdMaxLength)]
    public string OctorateConnectionId { get; set; } = string.Empty;

    public DateTime? RoomsImportedAt { get; set; }

    public int RoomCount { get; set; }

    public DateTime? OctorateReservationsImportedAt { get; set; }

    public DateTime? ReservationsImportedAt { get; set; }

    public DateTime? CalendarImportedAt { get; set; }

    public DateTime? ConnectedAt { get; set; }

    public DateTime? LeftForLaterAt { get; set; }

    [MaxLength(UserIdMaxLength)]
    public string? LeftForLaterByUserId { get; set; }

    /// <summary>The stable code of why this item stopped (see <see cref="OctorateInventoryErrorCodes"/>); <c>null</c> otherwise.</summary>
    [MaxLength(ErrorCodeMaxLength)]
    public string? ErrorCode { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
