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
    public const string OrgAccommodationIndexName = "UIX_OtaMessageSyncStates_OrgId_OctorateAccommodationId";

    public const int OctorateAccommodationIdMaxLength = 64;

    public const int ImportCursorMaxLength = 200;

    public const int LastErrorMaxLength = 200;

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrgId { get; set; }

    [Required]
    [MaxLength(OctorateAccommodationIdMaxLength)]
    public string OctorateAccommodationId { get; set; } = string.Empty;

    /// <summary>The property of the listing, set while its mapping is confirmed and <c>null</c> otherwise. FK, SET NULL.</summary>
    public Guid? PropertyId { get; set; }

    public Guid ConnectionId { get; set; }

    public DateTime? InitialImportCompletedAt { get; set; }

    [MaxLength(ImportCursorMaxLength)]
    public string? ImportCursor { get; set; }

    public DateTime? LastCreatedAfter { get; set; }

    public DateTime? LastRecentPassAt { get; set; }

    public DateTime? LastDeepPassAt { get; set; }

    [MaxLength(LastErrorMaxLength)]
    public string? LastError { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
