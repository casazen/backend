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
    public const string PropertyFlowIndexName = "UIX_PropertyFlowActivations_PropertyId_Flow";

    public const int UserIdMaxLength = 255;

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrgId { get; set; }

    public Guid PropertyId { get; set; }

    public OnboardingFlow Flow { get; set; }

    public DateTime ActivatedAt { get; set; } = DateTime.UtcNow;

    [Required]
    [MaxLength(UserIdMaxLength)]
    public string ActivatedByUserId { get; set; } = string.Empty;

    public DateTime? DeactivatedAt { get; set; }

    [MaxLength(UserIdMaxLength)]
    public string? DeactivatedByUserId { get; set; }
}
