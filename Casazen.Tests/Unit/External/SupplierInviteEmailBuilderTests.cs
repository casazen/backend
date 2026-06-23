using Casazen.Core.Entities;
using Casazen.Infrastructure.External;
using Xunit;

namespace Casazen.Tests.Unit.External;

public class SupplierInviteEmailBuilderTests
{
    [Fact]
    public void BuildSignupUrl_IncludesInviteTokenEmailAndComune()
    {
        var invite = new SupplierInviteRecord
        {
            Id = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
            Email = "fornitore@example.com",
            ComuneCode = "H501",
        };

        var url = SupplierInviteEmailBuilder.BuildSignupUrl("https://casazen-app.vercel.app/", invite);

        Assert.Contains("inviteToken=aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", url);
        Assert.Contains("email=fornitore%40example.com", url);
        Assert.Contains("comune=H501", url);
        Assert.StartsWith("https://casazen-app.vercel.app/register?", url);
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

        var signupUrl = "https://casazen-app.vercel.app/register?inviteToken=abc";
        var (subject, html) = SupplierInviteEmailBuilder.Build(
            invite,
            signupUrl,
            new DateTime(2026, 6, 27, 12, 0, 0, DateTimeKind.Utc));

        Assert.Equal("Invito CasaZen — Console fornitore", subject);
        Assert.Contains(signupUrl, html);
        Assert.Contains("Benvenuto nel pilota Roma", html);
        Assert.Contains("H501", html);
    }
}
