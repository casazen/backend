using Casazen.Core.Entities;
using Casazen.Infrastructure.External;
using Xunit;

namespace Casazen.Tests.Unit.External;

public class SupplierInviteEmailBuilderTests
{
    [Fact]
    public void BuildLoginUrl_ReturnsFrontendLoginUrl()
    {
        var url = SupplierInviteEmailBuilder.BuildLoginUrl("https://casazen-app.vercel.app/");

        Assert.Equal("https://casazen-app.vercel.app/login", url);
    }

    [Fact]
    public void Build_IncludesCustomMessageAndInviteEmail()
    {
        var invite = new SupplierInviteRecord
        {
            Email = "fornitore@example.com",
            ComuneCode = "H501",
            Message = "Benvenuto nel pilota Roma",
        };

        var signupUrl = "https://casazen-app.vercel.app/login";
        var (subject, html) = SupplierInviteEmailBuilder.Build(
            invite,
            signupUrl,
            new DateTime(2026, 6, 27, 12, 0, 0, DateTimeKind.Utc));

        Assert.Equal("Invito CasaZen — Console fornitore", subject);
        Assert.Contains(signupUrl, html);
        Assert.Contains("fornitore@example.com", html);
        Assert.Contains("Benvenuto nel pilota Roma", html);
        Assert.Contains("H501", html);
    }
}
