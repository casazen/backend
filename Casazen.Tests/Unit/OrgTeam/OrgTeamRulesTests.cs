using Casazen.Core.Entities.Enums;
using Casazen.Core.OrgTeam;
using Xunit;

namespace Casazen.Tests.Unit.OrgTeam;

/// <summary>
/// AM-02: who may do what with the people of an org, as pure rules. No escalation: nobody gives more than they have, only
/// the owner creates and manages administrators, nobody becomes owner, the owner is out of everyone's reach.
/// </summary>
public class OrgTeamRulesTests
{
    public static TheoryData<OrgRole> AllRoles => new(Enum.GetValues<OrgRole>());

    [Theory]
    [InlineData(OrgRole.Owner, true)]
    [InlineData(OrgRole.Admin, true)]
    [InlineData(OrgRole.PropertyManager, false)]
    [InlineData(OrgRole.Collaborator, false)]
    [InlineData(OrgRole.Accountant, false)]
    public void CanManageTeam_OnlyTheOwnerAndTheAdministrators(OrgRole role, bool expected)
    {
        Assert.Equal(expected, OrgTeamRules.CanManageTeam(role));
    }

    [Theory]
    [MemberData(nameof(AllRoles))]
    public void CanAssign_NobodyGivesTheOwnerRole(OrgRole actor)
    {
        Assert.False(OrgTeamRules.CanAssign(actor, OrgRole.Owner));
    }

    [Theory]
    [InlineData(OrgRole.Owner, true)]
    [InlineData(OrgRole.Admin, false)]
    [InlineData(OrgRole.PropertyManager, false)]
    [InlineData(OrgRole.Collaborator, false)]
    [InlineData(OrgRole.Accountant, false)]
    public void CanAssign_OnlyTheOwnerCreatesAnAdministrator(OrgRole actor, bool expected)
    {
        Assert.Equal(expected, OrgTeamRules.CanAssign(actor, OrgRole.Admin));
    }

    [Theory]
    [InlineData(OrgRole.PropertyManager)]
    [InlineData(OrgRole.Collaborator)]
    [InlineData(OrgRole.Accountant)]
    public void CanAssign_TheOwnerAndAnAdministratorGiveTheOtherRoles_NobodyElseDoes(OrgRole role)
    {
        Assert.True(OrgTeamRules.CanAssign(OrgRole.Owner, role));
        Assert.True(OrgTeamRules.CanAssign(OrgRole.Admin, role));
        Assert.False(OrgTeamRules.CanAssign(OrgRole.PropertyManager, role));
        Assert.False(OrgTeamRules.CanAssign(OrgRole.Collaborator, role));
        Assert.False(OrgTeamRules.CanAssign(OrgRole.Accountant, role));
    }

    [Theory]
    [MemberData(nameof(AllRoles))]
    public void CanActOn_TheOwnerIsOutOfReachForEverybody(OrgRole actor)
    {
        Assert.False(OrgTeamRules.CanActOn(actor, OrgRole.Owner));
    }

    [Theory]
    [InlineData(OrgRole.Owner, true)]
    [InlineData(OrgRole.Admin, false)]
    [InlineData(OrgRole.PropertyManager, false)]
    [InlineData(OrgRole.Collaborator, false)]
    [InlineData(OrgRole.Accountant, false)]
    public void CanActOn_AnAdministratorIsInTheHandsOfTheOwnerOnly(OrgRole actor, bool expected)
    {
        Assert.Equal(expected, OrgTeamRules.CanActOn(actor, OrgRole.Admin));
    }

    [Theory]
    [InlineData(OrgRole.PropertyManager)]
    [InlineData(OrgRole.Collaborator)]
    [InlineData(OrgRole.Accountant)]
    public void CanActOn_TheOwnerAndAnAdministratorManageTheOtherRoles_NobodyElseDoes(OrgRole target)
    {
        Assert.True(OrgTeamRules.CanActOn(OrgRole.Owner, target));
        Assert.True(OrgTeamRules.CanActOn(OrgRole.Admin, target));
        Assert.False(OrgTeamRules.CanActOn(OrgRole.PropertyManager, target));
        Assert.False(OrgTeamRules.CanActOn(OrgRole.Collaborator, target));
        Assert.False(OrgTeamRules.CanActOn(OrgRole.Accountant, target));
    }

    [Fact]
    public void NoRoleCanGiveWhatItCannotTouch()
    {
        // Nobody can assign a role that they could not act on afterwards: assigning is never wider than acting.
        foreach (var actor in Enum.GetValues<OrgRole>())
            foreach (var role in Enum.GetValues<OrgRole>())
                Assert.False(OrgTeamRules.CanAssign(actor, role) && !OrgTeamRules.CanActOn(actor, role), $"{actor} could give {role} and not touch it");
    }
}
