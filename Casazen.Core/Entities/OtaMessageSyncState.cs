using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

/// <summary>
/// Where the copy of the conversations of one listing stands (E05/F01/US01/T01, ADR-032): the checkpoints that let an interrupted import
/// resume without duplicates and that the recent and the deep passes advance, and the last error of the listing. One row per organization
/// and accommodation, created by the first pass that reaches the listing. No personal data.
/// </summary>
/// <remarks>
/// <see cref="LastError"/> is a stable code, never a message or a name (<c>chat_not_licensed</c> when the <c>chat</c> module is not in the
/// license of the listing): a screen that reads it never shows an empty inbox for a listing whose chat cannot be read. It is cleared by the
/// next pass that reads the listing.
/// </remarks>
[Table("OtaMessageSyncStates")]
public class OtaMessageSyncState : ITenantOwned
{
    /// <summary>Name of the unique index on (<see cref="OrgId"/>, <see cref="OctorateAccommodationId"/>).</summary>
    public const string OrgAccommodationIndexName = "UIX_OtaMessageSyncStates_OrgId_OctorateAccommodationId";

    public const int OctorateAccommodationIdMaxLength = 64;

    public const int ImportCursorMaxLength = 200;

    public const int LastErrorMaxLength = 200;

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Tenant of the row: the organization of the connection.</summary>
    public Guid OrgId { get; set; }

    /// <summary>The unit the Octorate chat is scoped to.</summary>
    [Required]
    [MaxLength(OctorateAccommodationIdMaxLength)]
    public string OctorateAccommodationId { get; set; } = string.Empty;

    /// <summary>The property of the listing, set while its mapping is confirmed and <c>null</c> otherwise. FK, SET NULL.</summary>
    public Guid? PropertyId { get; set; }

    /// <summary>The connection the listing is read through. FK, RESTRICT.</summary>
    public Guid ConnectionId { get; set; }

    /// <summary>When the import of the whole history of the listing finished (UTC); <c>null</c> while it did not.</summary>
    public DateTime? InitialImportCompletedAt { get; set; }

    /// <summary>Where an interrupted import resumes: the position in the list of the threads of the listing (the offset of the next page).</summary>
    [MaxLength(ImportCursorMaxLength)]
    public string? ImportCursor { get; set; }

    /// <summary>The instant of the newest message copied for the listing (UTC): the point from which a read of its messages continues.</summary>
    public DateTime? LastCreatedAfter { get; set; }

    /// <summary>When the last pass over the messages of the last 24 hours (every 10 minutes) read the listing (UTC).</summary>
    public DateTime? LastRecentPassAt { get; set; }

    /// <summary>When the last deep pass (daily, with an overlap of at least two days) read the listing (UTC).</summary>
    public DateTime? LastDeepPassAt { get; set; }

    /// <summary>The code of the last failure of the listing (<c>chat_not_licensed</c>, <c>octorate_unavailable</c>, ...); <c>null</c> after a pass that worked.</summary>
    [MaxLength(LastErrorMaxLength)]
    public string? LastError { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
