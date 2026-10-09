using Casazen.Core.Authorization;
using Xunit;

namespace Casazen.Tests.Unit.Authorization;

/// <summary>
/// AM-00: the single answer to "is this DB membership the owner's?", used by the org billing policy and by the
/// onboarding guard. A role key counts only in its own context.
/// </summary>
public class OrgOwnerRolesTests
{
    [Theory]
    [InlineData("short-rent", "property_owner")]
    [InlineData("long-rent", "long_term_landlord")]
    [InlineData("admin", "platform_admin")]
    [InlineData("SHORT-RENT", "Property_Owner")]
    public void IsOwnerRole_OwnerRoleOfItsContext_IsTrue(string contextKey, string roleKey) =>
        Assert.True(OrgOwnerRoles.IsOwnerRole(contextKey, roleKey));

    [Theory]
    [InlineData("short-rent", "property_manager")]
    [InlineData("short-rent", "staff")]
    [InlineData("short-rent", "accountant")]
    [InlineData("short-rent", "bk09_collaborator")]
    [InlineData("long-rent", "staff")]
    [InlineData("long-rent", "property_manager")]
    [InlineData("admin", "support")]
    public void IsOwnerRole_AnyOtherRoleOfTheContext_IsFalse(string contextKey, string roleKey) =>
        Assert.False(OrgOwnerRoles.IsOwnerRole(contextKey, roleKey));

    [Theory]
    [InlineData("long-rent", "property_owner")]
    [InlineData("short-rent", "long_term_landlord")]
    [InlineData("short-rent", "platform_admin")]
    [InlineData("admin", "property_owner")]
    [InlineData("supplier", "supplier")]
    [InlineData("unknown-context", "property_owner")]
    [InlineData("", "")]
    public void IsOwnerRole_OwnerRoleOfAnotherContextOrUnknownContext_IsFalse(string contextKey, string roleKey) =>
        Assert.False(OrgOwnerRoles.IsOwnerRole(contextKey, roleKey));

    [Theory]
    [InlineData("short-rent", "staff")]
    [InlineData("short-rent", "property_manager")]
    [InlineData("long-rent", "staff")]
    [InlineData("long-rent", "accountant")]
    [InlineData("short-rent", "bk09_collaborator")]
    public void IsHostMemberRole_NonOwnerRoleOfAHostContext_IsTrue(string contextKey, string roleKey) =>
        Assert.True(OrgOwnerRoles.IsHostMemberRole(contextKey, roleKey));

    [Theory]
    [InlineData("short-rent", "property_owner")]
    [InlineData("long-rent", "long_term_landlord")]
    public void IsHostMemberRole_OwnerOfAHostContext_IsFalse(string contextKey, string roleKey) =>
        Assert.False(OrgOwnerRoles.IsHostMemberRole(contextKey, roleKey));

    [Theory]
    [InlineData("admin", "platform_admin")]
    [InlineData("admin", "support")]
    [InlineData("supplier", "supplier")]
    public void IsHostMemberRole_ContextThatIsNotAHostContext_IsFalse(string contextKey, string roleKey) =>
        Assert.False(OrgOwnerRoles.IsHostMemberRole(contextKey, roleKey));
}
