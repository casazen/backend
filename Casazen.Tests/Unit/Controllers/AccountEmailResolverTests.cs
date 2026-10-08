using System.Security.Claims;
using Casazen.Core.Services;
using Casazen.Web.Infrastructure;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Controllers;

/// <summary>
/// AM-02: the email of the signed-in account and whether it is verified, the two facts an invitation is bound to. The
/// token first, Auth0 for what the token lacks, and a verified flag from Auth0 only for the very same email.
/// </summary>
public class AccountEmailResolverTests
{
    private const string Sub = "auth0|anna";

    private readonly Mock<IAuth0ManagementService> _auth0 = new();

    private AccountEmailResolver Resolver() => new(_auth0.Object);

    private static ClaimsPrincipal Principal(params (string Type, string Value)[] claims) =>
        new(new ClaimsIdentity(claims.Select(c => new Claim(c.Type, c.Value)), "TestAuth"));

    private void Auth0Profile(string email, bool? verified) =>
        _auth0.Setup(a => a.GetUserProfileAsync(Sub)).ReturnsAsync(new Auth0UserProfile(email, "Anna", "Leone", verified));

    [Theory]
    [InlineData("https://casazen.app/email", "https://casazen.app/email_verified")]
    [InlineData("email", "email_verified")]
    public async Task ResolveAsync_TheTokenSaysBoth_Auth0IsNotAsked(string emailClaim, string verifiedClaim)
    {
        var (email, verified) = await Resolver().ResolveAsync(
            Principal((emailClaim, " Anna.Leone@Example.com "), (verifiedClaim, "true")), Sub);

        Assert.Equal(("Anna.Leone@Example.com", true), (email, verified));
        _auth0.Verify(a => a.GetUserProfileAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ResolveAsync_TheTokenSaysNotVerified_ThatIsTheAnswer()
    {
        var (email, verified) = await Resolver().ResolveAsync(
            Principal(("email", "anna@example.com"), ("email_verified", "false")), Sub);

        Assert.Equal(("anna@example.com", false), (email, verified));
        _auth0.Verify(a => a.GetUserProfileAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ResolveAsync_TheActionClaimWinsOverTheStandardOne()
    {
        var (email, verified) = await Resolver().ResolveAsync(
            Principal(("email", "other@example.com"), ("https://casazen.app/email", "anna@example.com"),
                ("email_verified", "false"), ("https://casazen.app/email_verified", "true")),
            Sub);

        Assert.Equal(("anna@example.com", true), (email, verified));
    }

    [Fact]
    public async Task ResolveAsync_TheTokenHasTheEmailButNotTheFlag_Auth0DecidesForTheSameEmail()
    {
        Auth0Profile("ANNA@example.com", verified: true);

        var (email, verified) = await Resolver().ResolveAsync(Principal(("email", "anna@example.com")), Sub);

        Assert.Equal(("anna@example.com", true), (email, verified));
    }

    [Fact]
    public async Task ResolveAsync_Auth0KnowsAnotherEmailThanTheToken_TheFlagIsNotBorrowed()
    {
        Auth0Profile("someone.else@example.com", verified: true);

        var (email, verified) = await Resolver().ResolveAsync(Principal(("email", "anna@example.com")), Sub);

        Assert.Equal(("anna@example.com", false), (email, verified));
    }

    [Fact]
    public async Task ResolveAsync_TheTokenHasNothing_BothComeFromAuth0()
    {
        Auth0Profile("anna@example.com", verified: true);

        var (email, verified) = await Resolver().ResolveAsync(Principal(), Sub);

        Assert.Equal(("anna@example.com", true), (email, verified));
    }

    [Fact]
    public async Task ResolveAsync_Auth0SaysNotVerifiedOrNothing_NotVerified()
    {
        Auth0Profile("anna@example.com", verified: null);

        var (email, verified) = await Resolver().ResolveAsync(Principal(), Sub);

        Assert.Equal(("anna@example.com", false), (email, verified));
    }

    [Fact]
    public async Task ResolveAsync_Auth0IsNotReachable_TheEmailOfTheTokenIsNotVerified_AndWithoutAnyThereIsNone()
    {
        _auth0.Setup(a => a.GetUserProfileAsync(Sub)).ReturnsAsync((Auth0UserProfile?)null);

        var fromToken = await Resolver().ResolveAsync(Principal(("email", "anna@example.com")), Sub);
        var nothing = await Resolver().ResolveAsync(Principal(), Sub);

        Assert.Equal(("anna@example.com", false), fromToken);
        Assert.Equal((null, false), nothing);
    }

    [Theory]
    [InlineData("True", true)]
    [InlineData("TRUE", true)]
    [InlineData(" true ", true)]
    [InlineData("false", false)]
    public void GetEmailVerified_ParsesTheBooleanClaim(string value, bool expected)
    {
        Assert.Equal(expected, AccountEmailResolver.GetEmailVerified(Principal(("email_verified", value))));
    }

    [Theory]
    [InlineData("yes")]
    [InlineData("1")]
    [InlineData("")]
    public void GetEmailVerified_AnUnreadableClaim_IsUnknown(string value)
    {
        Assert.Null(AccountEmailResolver.GetEmailVerified(Principal(("email_verified", value))));
        Assert.Null(AccountEmailResolver.GetEmailVerified(Principal()));
    }
}
