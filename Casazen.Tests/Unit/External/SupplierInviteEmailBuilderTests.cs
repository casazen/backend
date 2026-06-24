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
            Id = Guid.Parse("a1b2c3d4-e5f6-7890-abcd-ef1234567890"),
            Email = "fornitore@example.com",
            ComuneCode = "H501",
        };

        var url = SupplierInviteEmailBuilder.BuildSignupUrl("https://casazen-api.up.railway.app", invite);

        Assert.StartsWith("https://casazen-api.up.railway.app/register?", url);
        Assert.Contains("inviteToken=a1b2c3d4-e5f6-7890-abcd-ef1234567890", url);
        Assert.Contains("email=fornitore%40example.com", url);
        Assert.Contains("comune=H501", url);
    }

    [Fact]
    public void BuildSignupUrl_TrimsTrailingSlash()
    {
        var invite = new SupplierInviteRecord
        {
            Email = "test@test.com",
            ComuneCode = "X999",
        };

        var url = SupplierInviteEmailBuilder.BuildSignupUrl("https://casazen-api.up.railway.app/", invite);

        Assert.StartsWith("https://casazen-api.up.railway.app/register?", url);
    }

    [Fact]
    public void Build_IncludesCustomMessageAndInviteEmail()
    {
        var invite = new SupplierInviteRecord
        {
            Id = Guid.NewGuid(),
            Email = "fornitore@example.com",
            ComuneCode = "H501",
            Message = "Benvenuto nel pilota Roma",
        };

        var signupUrl = SupplierInviteEmailBuilder.BuildSignupUrl("https://casazen-api.up.railway.app", invite);
        var (subject, html) = SupplierInviteEmailBuilder.Build(
            invite,
            signupUrl,
            new DateTime(2026, 6, 27, 12, 0, 0, DateTimeKind.Utc));

        Assert.Equal("Invito CasaZen — Console fornitore", subject);
        Assert.Contains("/register?inviteToken=", html);
        Assert.Contains("fornitore%40example.com", html);
        Assert.Contains("Benvenuto nel pilota Roma", html);
        Assert.Contains("H501", html);
    }
}
