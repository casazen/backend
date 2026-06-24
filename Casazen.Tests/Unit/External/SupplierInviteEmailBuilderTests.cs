using Casazen.Core.Entities;
using Casazen.Infrastructure.External;
using Xunit;

namespace Casazen.Tests.Unit.External;

public class SupplierInviteEmailBuilderTests
{
    [Fact]
    public void BuildAuth0SignupUrl_GeneratesAuth0AuthorizeUrl()
    {
        var invite = new SupplierInviteRecord
        {
            Id = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
            Email = "fornitore@example.com",
            ComuneCode = "H501",
        };

        var url = SupplierInviteEmailBuilder.BuildAuth0SignupUrl(
            "dev-xxx.us.auth0.com",
            "abc123",
            "https://casazen-app.vercel.app",
            invite);

        Assert.Contains("https://dev-xxx.us.auth0.com/authorize?", url);
        Assert.Contains("client_id=abc123", url);
        Assert.Contains("redirect_uri=https%3A%2F%2Fcasazen-app.vercel.app%2Fcallback", url);
        Assert.Contains("screen_hint=signup", url);
        Assert.Contains("login_hint=fornitore%40example.com", url);
    }

    [Fact]
    public void Build_IncludesCustomMessageAndSignupLink()
    {
        var invite = new SupplierInviteRecord
        {
            Email = "fornitore@example.com",
            ComuneCode = "H501",
            Message = "Benvenuto nel pilota Roma",
        };

        var signupUrl = "https://dev-xxx.us.auth0.com/authorize?client_id=abc&screen_hint=signup";
        var (subject, html) = SupplierInviteEmailBuilder.Build(
            invite,
            signupUrl,
            new DateTime(2026, 6, 27, 12, 0, 0, DateTimeKind.Utc));

        Assert.Equal("Invito CasaZen — Console fornitore", subject);
        Assert.Contains("client_id=abc", html);
        Assert.Contains("screen_hint=signup", html);
        Assert.Contains("Benvenuto nel pilota Roma", html);
        Assert.Contains("H501", html);
    }
}
