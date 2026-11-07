using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

/// <summary>
/// The "general connection model" of the marketplace (E09/F01/US01, NF-54, NF-56, NF-57): what an org has activated, and where it stands. One
/// row per org, property (when the kind is per property), kind and target; a channel, a tool and a licensed function (its license is the
/// accommodation's, D14, E09/F02/US02) are per property, a partner is per org (<see cref="PropertyId"/> is <c>null</c>). The status is what the
/// cards and the Active list show.
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
    /// <summary>Name of the unique index on (<see cref="OrgId"/>, <see cref="Kind"/>, <see cref="TargetCode"/>) of the rows with no property.</summary>
    public const string OrgLevelIndexName = "UIX_MarketplaceActivations_OrgId_Kind_Target_OrgLevel";

    /// <summary>Name of the unique index on (<see cref="OrgId"/>, <see cref="PropertyId"/>, <see cref="Kind"/>, <see cref="TargetCode"/>) of the rows with a property.</summary>
    public const string PropertyLevelIndexName = "UIX_MarketplaceActivations_OrgId_PropertyId_Kind_Target";

    public const int TargetCodeMaxLength = 100;
    public const int OctorateConnectionIdMaxLength = 64;
    public const int LastErrorCodeMaxLength = 64;

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Tenant: the org that activated it.</summary>
    public Guid OrgId { get; set; }

    /// <summary>The property it is activated for; <c>null</c> for an org-level activation (a partner). FK, cascade.</summary>
    public Guid? PropertyId { get; set; }

    public MarketplaceActivationKind Kind { get; set; } = MarketplaceActivationKind.Channel;

    /// <summary>The portal code (a channel or a tool), the code of the license module, or the code of the partner, by <see cref="Kind"/>.</summary>
    [Required]
    [MaxLength(TargetCodeMaxLength)]
    public string TargetCode { get; set; } = string.Empty;

    public MarketplaceActivationStatus Status { get; set; } = MarketplaceActivationStatus.Requested;

    /// <summary>The status was declared by the user and is not proven by Octorate.</summary>
    public bool IsDeclared { get; set; }

    /// <summary>When the user declared it (UTC).</summary>
    public DateTime? DeclaredAt { get; set; }

    /// <summary>When a connection or the license confirmed it (UTC).</summary>
    public DateTime? VerifiedAt { get; set; }

    /// <summary>When the host asked for it (UTC).</summary>
    public DateTime? RequestedAt { get; set; }

    /// <summary>The connection on Octorate this activation made, for a channel or a tool.</summary>
    [MaxLength(OctorateConnectionIdMaxLength)]
    public string? OctorateConnectionId { get; set; }

    /// <summary>When CasaZen last read the state of the activation from Octorate (UTC).</summary>
    public DateTime? LastSyncedAt { get; set; }

    /// <summary>The code of the last problem found, shown in simple words by the client.</summary>
    [MaxLength(LastErrorCodeMaxLength)]
    public string? LastErrorCode { get; set; }

    public DateTime? LastErrorAt { get; set; }

    /// <summary>The open To do item raised for the problem of this activation; written by the active integrations work. No foreign key.</summary>
    public Guid? TodoActivityId { get; set; }

    /// <summary>When the host asked the channel manager for a change of a licensed function (UTC); written by the active integrations work.</summary>
    public DateTime? ChangeRequestedAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Optimistic concurrency token: mapped to PostgreSQL's <c>xmin</c> system column, which changes with every update of the row (no
    /// column of its own), so two writers of the same activation cannot both win.
    /// </summary>
    public uint RowVersion { get; set; }
}
