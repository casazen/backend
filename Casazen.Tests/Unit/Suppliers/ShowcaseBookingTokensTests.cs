using Casazen.Core.Suppliers;
using Xunit;

namespace Casazen.Tests.Unit.Suppliers;

/// <summary>
/// SP-10: the token of the link that checks the customer's e-mail (256 random bits, only its SHA-256 stored, compared in constant
/// time) and the comparison of the customer's address after it was decrypted.
/// </summary>
public class ShowcaseBookingTokensTests
{
    [Fact]
    public void New_IsRandomUrlSafeAnd256Bits()
    {
        var tokens = Enumerable.Range(0, 50).Select(_ => ShowcaseBookingTokens.New()).ToList();

        Assert.Equal(50, tokens.Distinct().Count());
        Assert.All(tokens, token =>
        {
            Assert.Equal(43, token.Length);
            Assert.Matches("^[A-Za-z0-9_-]+$", token);
        });
    }

    [Fact]
    public void Hash_IsTheLowercaseHexOfSha256_NeverTheToken()
    {
        var token = ShowcaseBookingTokens.New();

        var hash = ShowcaseBookingTokens.Hash(token);

        Assert.Equal(64, hash.Length);
        Assert.Matches("^[0-9a-f]{64}$", hash);
        Assert.DoesNotContain(token, hash, StringComparison.Ordinal);
        Assert.Equal(hash, ShowcaseBookingTokens.Hash(token));
        Assert.Equal("2c26b46b68ffc68ff99b453c1d30413413422d706483bfa0f98a5e886266e7ae", ShowcaseBookingTokens.Hash("foo"));
    }

    [Fact]
    public void Matches_TheRightToken_AndOnlyIt()
    {
        var token = ShowcaseBookingTokens.New();
        var hash = ShowcaseBookingTokens.Hash(token);

        Assert.True(ShowcaseBookingTokens.Matches(hash, token));
        Assert.True(ShowcaseBookingTokens.Matches(hash, $"  {token} "));
        Assert.False(ShowcaseBookingTokens.Matches(hash, ShowcaseBookingTokens.New()));
        Assert.False(ShowcaseBookingTokens.Matches(hash, token[..^1] + (token[^1] == 'A' ? 'B' : 'A')));
        Assert.False(ShowcaseBookingTokens.Matches(hash, token.ToUpperInvariant()));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Matches_ANullOrEmptyToken_NeverMatches(string? token) =>
        Assert.False(ShowcaseBookingTokens.Matches(ShowcaseBookingTokens.Hash("x"), token));

    [Fact]
    public void Matches_AMissingHash_OrAnOverLongToken_NeverMatches()
    {
        var token = ShowcaseBookingTokens.New();

        Assert.False(ShowcaseBookingTokens.Matches(null, token));
        Assert.False(ShowcaseBookingTokens.Matches(string.Empty, token));
        var tooLong = new string('a', ShowcaseBookingLimits.TokenMaxLength + 1);
        Assert.False(ShowcaseBookingTokens.Matches(ShowcaseBookingTokens.Hash(tooLong), tooLong));
    }

    [Theory]
    [InlineData("mario@example.com", "mario@example.com", true)]
    [InlineData("Mario@Example.COM", "mario@example.com", true)]
    [InlineData(" mario@example.com ", "mario@example.com", true)]
    [InlineData("mario@example.com", "maria@example.com", false)]
    [InlineData("mario@example.com", "mario@example.com.", false)]
    [InlineData("mario@example.com", null, false)]
    [InlineData(null, "mario@example.com", false)]
    [InlineData("", "", false)]
    public void SameAddress_IsComparedAfterTheDecryption_InConstantTime(string? stored, string? provided, bool expected) =>
        Assert.Equal(expected, ServiceCustomerEmails.SameAddress(stored, provided));
}
