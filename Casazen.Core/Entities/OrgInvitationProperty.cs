using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

/// <summary>
/// One property an invitation carries (AM-02c, AM-03-FU1): the properties a collaborator «Solo alcuni» will reach once it accepts.
/// They are kept with the invitation and become <see cref="PropertyMemberAccess"/> rows in the very transaction of the
/// acceptance (<c>IOrgPropertyAccessService.GrantFromInvitationAsync</c>); a property deleted in the meantime is skipped without an
/// error. Only ids: the property is not named anywhere an anonymous reader can see (the public lookup of the invitation shows no
/// property), and nothing here is personal data.
/// </summary>
/// <remarks>
/// Written together with the invitation by <c>IOrgInvitationService</c>, never alone. The rows go with the invitation (cascade),
/// so the deletion of a closed invitation 30 days after its end (<c>OrgTeam:InvitationRetentionDays</c>) takes them away.
/// <c>(InvitationId, PropertyId)</c> is unique.
/// </remarks>
[Table("OrgInvitationProperties")]
public class OrgInvitationProperty : ITenantOwned
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Tenant key: the org of the invitation and of the property. Server-set, never client-supplied.</summary>
    public Guid OrgId { get; set; }

    public Guid InvitationId { get; set; }

    public Guid PropertyId { get; set; }
}
