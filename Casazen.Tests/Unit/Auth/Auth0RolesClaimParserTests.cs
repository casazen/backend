using Casazen.Web.Infrastructure;
using Xunit;

namespace Casazen.Tests.Unit.Auth;

public class Auth0RolesClaimParserTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ParseValue_WhenEmpty_ReturnsEmpty(string? value)
    {
        Assert.Empty(Auth0RolesClaimParser.ParseValue(value));
    }

    [Fact]
    public void ParseValue_WhenPlainRoleName_ReturnsSingleRole()
    {
        var roles = Auth0RolesClaimParser.ParseValue("LongTermLandlord");

        Assert.Single(roles);
        Assert.Equal("LongTermLandlord", roles[0]);
    }

    [Fact]
    public void ParseValue_WhenJsonArray_ReturnsAllRoles()
    {
        var roles = Auth0RolesClaimParser.ParseValue("[\"LongTermLandlord\",\"PropertyOwner\"]");

        Assert.Equal(2, roles.Count);
        Assert.Contains("LongTermLandlord", roles);
        Assert.Contains("PropertyOwner", roles);
    }

    [Fact]
    public void ParseValue_WhenJsonString_ReturnsSingleRole()
    {
        var roles = Auth0RolesClaimParser.ParseValue("\"LongTermLandlord\"");

        Assert.Single(roles);
        Assert.Equal("LongTermLandlord", roles[0]);
    }

    [Fact]
    public void Parse_WhenMultipleClaimValues_ReturnsDistinctRoles()
    {
        var roles = Auth0RolesClaimParser.Parse(["LongTermLandlord", "[\"PropertyOwner\"]"]);

        Assert.Equal(2, roles.Count);
        Assert.Contains("LongTermLandlord", roles);
        Assert.Contains("PropertyOwner", roles);
    }
}
