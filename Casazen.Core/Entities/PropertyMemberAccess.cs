using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

/// <summary>
/// One property an org member is allowed to reach (AM-03). The rows exist for the members whose
/// <see cref="OrgMember.PropertyScope"/> is <see cref="Enums.PropertyScope.Selected"/> («Solo alcuni»): such a member
/// sees and touches only the properties that have a row for it, in every list and in every service. A member with
/// <see cref="Enums.PropertyScope.All"/> has no rows and reaches every property of the org, the ones added later included.
/// </summary>
/// <remarks>
/// <para>Written by one service only (<c>IOrgPropertyAccessService</c>), under the org's people lock, together with the
/// member's <see cref="OrgMember.PropertyScope"/>; read by the authorization (<c>IHostScopeResolver</c>) and, as an
/// <c>EXISTS</c> in SQL, by every list (<c>InScope</c>). <c>(UserId, PropertyId)</c> is unique.</para>
/// <para>The property is never physically deleted (soft delete), so a row outlives the removal of its property from the
/// lists; it goes with the member (<see cref="OrgMember"/> removal) and with the account (cascade).</para>
/// </remarks>
[Table("PropertyMemberAccesses")]
public class PropertyMemberAccess : ITenantOwned
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Tenant key: the org of the property and of the member. Server-set, never client-supplied.</summary>
    public Guid OrgId { get; set; }

    /// <summary>The member (<c>User.Id</c>).</summary>
    [Required, MaxLength(255)]
    public string UserId { get; set; } = string.Empty;

    public Guid PropertyId { get; set; }

    public virtual Property Property { get; set; } = null!;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Who gave the access (<c>User.Id</c>). No foreign key: the history outlives the account.</summary>
    [MaxLength(255)]
    public string? CreatedByUserId { get; set; }
}
