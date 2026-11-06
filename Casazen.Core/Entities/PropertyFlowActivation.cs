using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

/// <summary>
/// The host's decision to run one flow (<see cref="OnboardingFlow"/>) on one property (NF-106, E10/F01/US01/T01), taken after the server
/// verified its prerequisites. One row per property and flow (unique index <see cref="PropertyFlowIndexName"/>): an activation after a
/// deactivation reuses the row and clears <see cref="DeactivatedAt"/>, so the row says who activated first and who last.
/// </summary>
/// <remarks>
/// <para><b>The row is not the state.</b> A flow is «Active» only while a row without <see cref="DeactivatedAt"/> exists <b>and</b> every
/// prerequisite still holds (connection, mapping, single sender...): the status is derived on every read, so a flow that was activated stops
/// being active the moment a prerequisite stops holding, and the row stays to resume from.</para>
/// <para>Rows follow their property (cascade) and carry its <see cref="OrgId"/> (TN-2). The users are opaque Auth0 subjects (<c>string</c>, not
/// foreign keys). No personal data.</para>
/// </remarks>
[Table("PropertyFlowActivations")]
public class PropertyFlowActivation : ITenantOwned
{
    /// <summary>Name of the unique index on (<see cref="PropertyId"/>, <see cref="Flow"/>).</summary>
    public const string PropertyFlowIndexName = "UIX_PropertyFlowActivations_PropertyId_Flow";

    /// <summary>Longest user id: the column length of <c>User.Id</c>, the Auth0 subject.</summary>
    public const int UserIdMaxLength = 255;

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Tenant of the row, copied from the property when it is created (TN-2).</summary>
    public Guid OrgId { get; set; }

    /// <summary>The property (FK, CASCADE: the activation goes with the property).</summary>
    public Guid PropertyId { get; set; }

    public OnboardingFlow Flow { get; set; }

    /// <summary>When the flow was activated on the property (the last activation, UTC).</summary>
    public DateTime ActivatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Who activated it: the Auth0 subject of the account.</summary>
    [Required]
    [MaxLength(UserIdMaxLength)]
    public string ActivatedByUserId { get; set; } = string.Empty;

    /// <summary>When the flow was deactivated by the host (UTC); <c>null</c> while the activation stands.</summary>
    public DateTime? DeactivatedAt { get; set; }

    /// <summary>Who deactivated it: the Auth0 subject of the account; <c>null</c> while the activation stands.</summary>
    [MaxLength(UserIdMaxLength)]
    public string? DeactivatedByUserId { get; set; }
}
