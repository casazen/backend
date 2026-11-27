using Casazen.Core.SiteConnections;
using Xunit;

namespace Casazen.Tests.Unit.SiteConnections;

/// <summary>
/// S022 (PLG-BE-CONNECT, site integration plan §10.3-§10.4, AC 2): what a site address, a return address, a state, a PKCE challenge and a verifier must be,
/// the address WordPress is sent back to (its query kept, the code and the state added by the URL builder) and the shape of the secrets.
/// </summary>
public class SiteConnectionRulesTests
{
    private const string Site = "https://www.quokka360.com";
    private const string ReturnUrl = "https://www.quokka360.com/wp-admin/options-general.php?page=casazen";

    [Theory]
    [InlineData("https://www.quokka360.com", "https://www.quokka360.com")]
    [InlineData("https://www.quokka360.com/", "https://www.quokka360.com")]
    [InlineData("HTTPS://WWW.Quokka360.COM", "https://www.quokka360.com")]
    [InlineData("https://www.quokka360.com:443", "https://www.quokka360.com")]
    [InlineData("https://www.quokka360.com:8443", "https://www.quokka360.com:8443")]
    [InlineData("http://localhost:8080", "http://localhost:8080")]
    [InlineData("http://127.0.0.1:8881", "http://127.0.0.1:8881")]
    [InlineData("http://wp.localhost", "http://wp.localhost")]
    [InlineData("http://quokka.test", "http://quokka.test")]
    public void TryNormalizeSiteUrl_AnAllowedOrigin_IsNormalized(string value, string expected)
    {
        Assert.True(SiteConnectionRules.TryNormalizeSiteUrl(value, out var origin));
        Assert.Equal(expected, origin);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("www.quokka360.com")]
    [InlineData("http://www.quokka360.com")]
    [InlineData("http://localhost.evil.example")]
    [InlineData("ftp://www.quokka360.com")]
    [InlineData("javascript:alert(1)")]
    [InlineData("https://user:secret@www.quokka360.com")]
    [InlineData("https://www.quokka360.com/wp")]
    [InlineData("https://www.quokka360.com//")]
    [InlineData("https://www.quokka360.com?page=casazen")]
    [InlineData("https://www.quokka360.com/?page=casazen")]
    [InlineData("https://www.quokka360.com#top")]
    [InlineData(" https://www.quokka360.com")]
    [InlineData("https://www.quokka360.com\\")]
    [InlineData("https://[::1]")]
    public void TryNormalizeSiteUrl_NotAnAllowedOrigin_IsRefused(string? value)
    {
        Assert.False(SiteConnectionRules.TryNormalizeSiteUrl(value, out var origin));
        Assert.Null(origin);
    }

    [Fact]
    public void TryNormalizeSiteUrl_LongerThanTheColumn_IsRefused()
    {
        var host = new string('a', SiteConnectionRules.SiteUrlMaxLength) + ".example";

        Assert.False(SiteConnectionRules.TryNormalizeSiteUrl("https://" + host, out _));
    }

    [Fact]
    public void RedirectUrl_TheReturnAddressKeepsItsQuery_AndGetsTheCodeAndTheStateFromTheBuilder()
    {
        Assert.True(SiteConnectionRules.TryParseReturnUrl(ReturnUrl, Site, out var returnUri));

        var redirect = SiteConnectionRules.RedirectUrl(returnUri, "Code_of-43-characters", "state-1");

        Assert.Equal(ReturnUrl + "&casazen_code=Code_of-43-characters&state=state-1", redirect);
        Assert.True(SiteConnectionRules.TryParseReturnUrl("https://www.quokka360.com/wp-admin/", Site, out var bareUri));
        Assert.Equal("https://www.quokka360.com/wp-admin/?casazen_code=c&state=s", SiteConnectionRules.RedirectUrl(bareUri, "c", "s"));
    }

    [Theory]
    [InlineData("https://evil.example/wp-admin/options-general.php?page=casazen")]
    [InlineData("https://www.quokka360.com.evil.example/wp-admin/")]
    [InlineData("http://www.quokka360.com/wp-admin/")]
    [InlineData("https://www.quokka360.com:8443/wp-admin/")]
    [InlineData("javascript:alert(document.cookie)")]
    [InlineData("ftp://www.quokka360.com/wp-admin/")]
    [InlineData("https://admin:secret@www.quokka360.com/wp-admin/")]
    [InlineData("https://www.quokka360.com/wp-admin/#casazen")]
    [InlineData("https://www.quokka360.com/wp-admin/options-general.php?page=casazen&state=other")]
    [InlineData("https://www.quokka360.com/wp-admin/options-general.php?casazen_code=planted")]
    [InlineData("/wp-admin/options-general.php?page=casazen")]
    [InlineData("https://www.quokka360.com/wp admin/")]
    public void TryParseReturnUrl_NotOnTheSiteOrNotClean_IsRefused(string value)
    {
        Assert.False(SiteConnectionRules.TryParseReturnUrl(value, Site, out var returnUri));
        Assert.Null(returnUri);
    }

    [Fact]
    public void TryParseReturnUrl_UpTo2048Characters()
    {
        const string prefix = "https://www.quokka360.com/wp-admin/options-general.php?page=casazen&p=";
        var longest = prefix + new string('a', SiteConnectionRules.ReturnUrlMaxLength - prefix.Length);

        Assert.True(SiteConnectionRules.TryParseReturnUrl(longest, Site, out _));
        Assert.False(SiteConnectionRules.TryParseReturnUrl(longest + "a", Site, out _));
    }

    [Fact]
    public void StateChallengeMethodAndVerifier_HaveTheShapesOfTheContract()
    {
        Assert.True(SiteConnectionRules.IsValidState("a"));
        Assert.True(SiteConnectionRules.IsValidState(new string('f', 128)));
        Assert.True(SiteConnectionRules.IsValidState("AZaz09_-"));
        Assert.False(SiteConnectionRules.IsValidState(new string('f', 129)));
        Assert.False(SiteConnectionRules.IsValidState(string.Empty));
        Assert.False(SiteConnectionRules.IsValidState(null));
        Assert.False(SiteConnectionRules.IsValidState("with space"));
        Assert.False(SiteConnectionRules.IsValidState("a.b"));
        Assert.False(SiteConnectionRules.IsValidState("abc\n"));

        Assert.True(SiteConnectionRules.IsValidCodeChallenge(new string('A', 43)));
        Assert.True(SiteConnectionRules.IsValidCodeChallenge("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM"));
        Assert.False(SiteConnectionRules.IsValidCodeChallenge(new string('A', 42)));
        Assert.False(SiteConnectionRules.IsValidCodeChallenge(new string('A', 44)));
        Assert.False(SiteConnectionRules.IsValidCodeChallenge(new string('A', 42) + "="));
        Assert.False(SiteConnectionRules.IsValidCodeChallenge(new string('A', 42) + "+"));
        Assert.False(SiteConnectionRules.IsValidCodeChallenge(null));

        Assert.True(SiteConnectionRules.IsS256("S256"));
        Assert.False(SiteConnectionRules.IsS256("s256"));
        Assert.False(SiteConnectionRules.IsS256("plain"));
        Assert.False(SiteConnectionRules.IsS256(null));

        Assert.True(SiteConnectionRules.IsValidCodeVerifier(new string('a', 43)));
        Assert.True(SiteConnectionRules.IsValidCodeVerifier(new string('~', 64) + "-._"));
        Assert.True(SiteConnectionRules.IsValidCodeVerifier(new string('a', 128)));
        Assert.False(SiteConnectionRules.IsValidCodeVerifier(new string('a', 42)));
        Assert.False(SiteConnectionRules.IsValidCodeVerifier(new string('a', 129)));
        Assert.False(SiteConnectionRules.IsValidCodeVerifier(new string('a', 42) + "+"));
        Assert.False(SiteConnectionRules.IsValidCodeVerifier(null));
    }

    [Fact]
    public void Tokens_AreUnguessableUrlSafeSecrets_StoredAsTheirLowercaseSha256()
    {
        var code = SiteConnectionTokens.NewCode();
        var credential = SiteConnectionTokens.NewCredential();

        Assert.Equal(SiteConnectionTokens.SecretLength, code.Length);
        Assert.True(SiteConnectionTokens.IsCode(code));
        Assert.NotEqual(code, SiteConnectionTokens.NewCode());
        Assert.StartsWith(SiteConnectionTokens.CredentialPrefix, credential, StringComparison.Ordinal);
        Assert.Equal(SiteConnectionTokens.CredentialPrefix.Length + SiteConnectionTokens.SecretLength, credential.Length);
        Assert.True(SiteConnectionTokens.IsCredential(credential));
        Assert.False(SiteConnectionTokens.IsCredential(code));
        Assert.False(SiteConnectionTokens.IsCredential("czs_short"));
        Assert.False(SiteConnectionTokens.IsCredential(credential + "\n"));

        // SHA-256 of "abc" (FIPS 180-2), lowercase hex.
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", SiteConnectionTokens.Hash("abc"));
        Assert.True(SiteConnectionTokens.HashMatches(SiteConnectionTokens.Hash(credential), credential));
        Assert.False(SiteConnectionTokens.HashMatches(SiteConnectionTokens.Hash(credential), SiteConnectionTokens.NewCredential()));
        Assert.False(SiteConnectionTokens.HashMatches(null, credential));
    }

    [Fact]
    public void S256_IsTheExampleOfRfc7636_ComparedWithTheChallenge()
    {
        // RFC 7636, appendix B.
        const string verifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk";
        const string challenge = "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM";

        Assert.Equal(challenge, SiteConnectionTokens.S256(verifier));
        Assert.True(SiteConnectionTokens.ProofMatches(challenge, SiteConnectionTokens.S256(verifier)));
        Assert.False(SiteConnectionTokens.ProofMatches(challenge, SiteConnectionTokens.S256(verifier + "x")));
        Assert.False(SiteConnectionTokens.ProofMatches(null, SiteConnectionTokens.S256(verifier)));
    }
}
