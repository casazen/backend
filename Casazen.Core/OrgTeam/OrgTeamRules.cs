using Casazen.Core.Entities.Enums;

namespace Casazen.Core.OrgTeam;

/// <summary>
/// Who may do what with the people of an org (AM-02, decisions D12 and D15), as pure rules the services and the tests
/// share. The policy <c>RequireContext:account:org.members.manage</c> says who may call the endpoints at all (the owner
/// and the administrators hold the permission); these rules say what each of them may do to whom, so that nobody can
/// give more than they have.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Only the <see cref="OrgRole.Owner"/> and the <see cref="OrgRole.Admin"/>s manage people.</item>
/// <item>Nobody assigns <see cref="OrgRole.Owner"/>: the ownership is not transferable in this version.</item>
/// <item>Only the owner creates an administrator (invitation or change of role) and only the owner changes, deactivates,
/// reactivates or removes one.</item>
/// <item>The owner itself is never changed, deactivated or removed (<c>org_last_owner</c>, enforced by
/// <c>IOrgMembershipService</c>).</item>
/// </list>
/// </remarks>
public static class OrgTeamRules
{
    public static bool CanManageTeam(OrgRole role) => role is OrgRole.Owner or OrgRole.Admin;

    public static bool CanAssign(OrgRole actor, OrgRole role) => role switch
    {
        OrgRole.Owner => false,
        OrgRole.Admin => actor == OrgRole.Owner,
        _ => CanManageTeam(actor),
    };

    public static bool CanActOn(OrgRole actor, OrgRole target) => target switch
    {
        OrgRole.Owner => false,
        OrgRole.Admin => actor == OrgRole.Owner,
        _ => CanManageTeam(actor),
    };
}
