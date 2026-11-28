using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

/// <summary>
/// The "general connection model" of the marketplace (E09/F01/US01, NF-54, NF-56, NF-57): what an org has activated, and where it stands. One
/// row per org, property (when the kind is per property), kind and target; a channel, a tool and a licensed function (its license is the
/// accommodation's, D14, E09/F02/US02) are per property; a partner is activated on its own site, so it is the org's (<see cref="PropertyId"/> is
/// <c>null</c>) or, when the host declares it for one property (E09/F02/US03/T01), that property's (<see cref="PropertyId"/> is the property).
/// The status is what the cards and the Active list show.
/// </summary>
/// <remarks>
/// <para><b>Uniqueness.</b> Two partial unique indexes reject a second activation of the same kind and target: one for the org-level rows
/// (<see cref="OrgLevelIndexName"/>), one for the property-level rows (<see cref="PropertyLevelIndexName"/>). A unique index with a
/// nullable column would not do, because the database takes two NULLs for different values.</para>
/// <para><b>Proof.</b> <see cref="IsDeclared"/> says the status was declared by the user and Octorate has not proven it: a partner activated on
/// its own site is declared (with the date), and only a connection or a license entry verifies it (<see cref="VerifiedAt"/>).</para>
/// <para><b>Links.</b> The link between a CasaZen property and its Octorate accommodation is <c>OctorateListingMapping</c> (a confirmed row
/// has the property and the <c>acc</c> of every Octorate call): this epic reads it and never writes it. <see cref="TodoActivityId"/> has no
/// foreign key, so this table does not wait for the activity center.</para>
/// </remarks>
[Table("MarketplaceActivations")]
public class MarketplaceActivation : ITenantOwned
{
    public const string OrgLevelIndexName = "UIX_MarketplaceActivations_OrgId_Kind_Target_OrgLevel";

    public const string PropertyLevelIndexName = "UIX_MarketplaceActivations_OrgId_PropertyId_Kind_Target";

    public const int TargetCodeMaxLength = 100;
    public const int OctorateConnectionIdMaxLength = 64;
    public const int LastErrorCodeMaxLength = 64;

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrgId { get; set; }

    public Guid? PropertyId { get; set; }

    public MarketplaceActivationKind Kind { get; set; } = MarketplaceActivationKind.Channel;

    [Required]
    [MaxLength(TargetCodeMaxLength)]
    public string TargetCode { get; set; } = string.Empty;

    public MarketplaceActivationStatus Status { get; set; } = MarketplaceActivationStatus.Requested;

    public bool IsDeclared { get; set; }

    public DateTime? DeclaredAt { get; set; }

    public DateTime? VerifiedAt { get; set; }

    public DateTime? RequestedAt { get; set; }

    [MaxLength(OctorateConnectionIdMaxLength)]
    public string? OctorateConnectionId { get; set; }

    public DateTime? LastSyncedAt { get; set; }

    [MaxLength(LastErrorCodeMaxLength)]
    public string? LastErrorCode { get; set; }

    public DateTime? LastErrorAt { get; set; }

    public DateTime? RoomsImportRequestedAt { get; set; }

    public DateTime? ReservationsImportRequestedAt { get; set; }

    public DateTime? ReservationsImportAcceptedAt { get; set; }

    public DateTime? CalendarImportRequestedAt { get; set; }

    public DateTime? MarkConnectedRequestedAt { get; set; }

    public DateTime? MappingCommittedAt { get; set; }

    public Guid? TodoActivityId { get; set; }

    public DateTime? ChangeRequestedAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Optimistic concurrency token: mapped to PostgreSQL's <c>xmin</c> system column, which changes with every update of the row (no
    /// column of its own), so two writers of the same activation cannot both win.
    /// </summary>
    public uint RowVersion { get; set; }
}
