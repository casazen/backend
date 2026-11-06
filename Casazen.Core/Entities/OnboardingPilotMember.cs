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
    /// <summary>Name of the unique index on <see cref="PropertyId"/>: a property is in the pilot group once.</summary>
    public const string PropertyIndexName = "UIX_OnboardingPilotMembers_PropertyId";

    /// <summary>Longest <see cref="AddedByUserId"/>: the column length of <c>User.Id</c>, the Auth0 subject.</summary>
    public const int UserIdMaxLength = 255;

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Tenant of the row, copied from the property when it is created (TN-2).</summary>
    public Guid OrgId { get; set; }

    /// <summary>The property (FK, CASCADE: the membership goes with the property).</summary>
    public Guid PropertyId { get; set; }

    public DateTime AddedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Who added the property to the pilot group: the Auth0 subject of the account.</summary>
    [Required]
    [MaxLength(UserIdMaxLength)]
    public string AddedByUserId { get; set; } = string.Empty;
}
