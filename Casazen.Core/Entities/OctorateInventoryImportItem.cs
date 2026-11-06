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
    /// <summary>Name of the unique index on (<see cref="ChannelId"/>, <see cref="OctorateConnectionId"/>).</summary>
    public const string ChannelConnectionIndexName = "UIX_OctorateInventoryImportItems_ChannelId_ConnectionId";

    /// <summary>Name of the index on (<see cref="OrgId"/>, <see cref="OctorateConnectionId"/>) that finds what was done for a connection in any batch.</summary>
    public const string OrgConnectionIndexName = "IX_OctorateInventoryImportItems_OrgId_ConnectionId";

    /// <summary>Longest accommodation or connection id: the length of the id column of the mappings.</summary>
    public const int IdMaxLength = 64;

    /// <summary>Longest <see cref="ErrorCode"/>.</summary>
    public const int ErrorCodeMaxLength = 60;

    /// <summary>Longest <see cref="LeftForLaterByUserId"/>: the column length of <c>User.Id</c>, the Auth0 subject.</summary>
    public const int UserIdMaxLength = 255;

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Tenant of the row (TN-2). Server-set, never client-supplied.</summary>
    public Guid OrgId { get; set; }

    /// <summary>The channel row the item belongs to (FK, CASCADE).</summary>
    public Guid ChannelId { get; set; }

    /// <summary>The accommodation on Octorate the connection belongs to: the key of the mapping row of the listing.</summary>
    [Required]
    [MaxLength(IdMaxLength)]
    public string OctorateAccommodationId { get; set; } = string.Empty;

    /// <summary>The connection of the accommodation to the channel on Octorate (a number, as Octorate gives it).</summary>
    [Required]
    [MaxLength(IdMaxLength)]
    public string OctorateConnectionId { get; set; } = string.Empty;

    /// <summary><c>IMPORT_ROOMS</c> was done for the connection (UTC).</summary>
    public DateTime? RoomsImportedAt { get; set; }

    /// <summary>How many rooms <c>IMPORT_ROOMS</c> reported; a count, never their content.</summary>
    public int RoomCount { get; set; }

    /// <summary><c>IMPORT_RESERVATIONS</c> was done for the connection (UTC): the reservations are inside Octorate.</summary>
    public DateTime? OctorateReservationsImportedAt { get; set; }

    /// <summary>The reservations of the listing were read into CasaZen completely (UTC).</summary>
    public DateTime? ReservationsImportedAt { get; set; }

    /// <summary><c>IMPORT_CALENDAR</c> was done for the connection (UTC).</summary>
    public DateTime? CalendarImportedAt { get; set; }

    /// <summary><c>MARK_CONNECTED</c> was done for the connection (UTC).</summary>
    public DateTime? ConnectedAt { get; set; }

    /// <summary>The person left the listing for later (UTC): it stays write-blocked and is not marked connected; the other listings go on.</summary>
    public DateTime? LeftForLaterAt { get; set; }

    /// <summary>Who left it for later (the Auth0 subject).</summary>
    [MaxLength(UserIdMaxLength)]
    public string? LeftForLaterByUserId { get; set; }

    /// <summary>The stable code of why this item stopped (see <see cref="OctorateInventoryErrorCodes"/>); <c>null</c> otherwise.</summary>
    [MaxLength(ErrorCodeMaxLength)]
    public string? ErrorCode { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
