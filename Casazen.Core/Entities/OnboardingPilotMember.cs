using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

/// <summary>
/// A property of the pilot group of an agency's progressive activation (NF-105, H10, E10/F01/US01/T01): the first five to ten properties the
/// flows are activated on before the others. One row per property (unique index <see cref="PropertyIndexName"/>), going with the property
/// (cascade) and carrying its org (TN-2, restrict). The user is the opaque Auth0 subject (<c>string</c>, not a foreign key). No personal data.
/// </summary>
[Table("OnboardingPilotMembers")]
public class OnboardingPilotMember : ITenantOwned
{
    public const string PropertyIndexName = "UIX_OnboardingPilotMembers_PropertyId";

    public const int UserIdMaxLength = 255;

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrgId { get; set; }

    public Guid PropertyId { get; set; }

    public DateTime AddedAt { get; set; } = DateTime.UtcNow;

    [Required]
    [MaxLength(UserIdMaxLength)]
    public string AddedByUserId { get; set; } = string.Empty;
}
