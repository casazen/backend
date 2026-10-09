using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

/// <summary>
/// A person who belongs to an org, with the role they have in it (AM-01). The source of truth of the org membership:
/// who the org's people are, what each of them is and whether they may still use it. A user belongs to <b>one</b> org
/// (<c>UserId</c> is unique, and <c>User.OrgId</c> is the same org), so the org of a request is still read from the
/// user; this row says what the user is in it.
/// </summary>
/// <remarks>
/// <para>The <see cref="UserContextMembership"/> rows stay the <b>projection</b> of the role into permissions: the
/// authorization reads them, never this row. They are written in the same <c>SaveChanges</c> as this row by one service
/// only (<c>IOrgMembershipService</c>), and re-aligned by its reconcile command when they drift
/// (<c>docs/runbooks/org-team.md</c>).</para>
/// <para>Every owner of an org has one (<see cref="OrgRole.Owner"/>, all properties); it is written at the onboarding and
/// backfilled for the owners that existed before it (migration <c>AddOrgMembership</c>).</para>
/// </remarks>
[Table("OrgMembers")]
public class OrgMember : ITenantOwned
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>The org the person belongs to (the same as <c>User.OrgId</c>).</summary>
    public Guid OrgId { get; set; }

    /// <summary>The person (<c>User.Id</c>). Unique: one org per user.</summary>
    [Required, MaxLength(255)]
    public string UserId { get; set; } = string.Empty;

    public OrgRole Role { get; set; }

    public OrgMemberStatus Status { get; set; } = OrgMemberStatus.Active;

    /// <summary>Every property of the org until the per-property scope exists (AM-03).</summary>
    public PropertyScope PropertyScope { get; set; } = PropertyScope.All;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Who added the person (<c>User.Id</c>); <c>null</c> for the owner (onboarding, backfill).</summary>
    [MaxLength(255)]
    public string? CreatedByUserId { get; set; }

    /// <summary>When the member was last deactivated; <c>null</c> while active (cleared by the reactivation).</summary>
    public DateTime? DeactivatedAt { get; set; }

    public User User { get; set; } = null!;
}
