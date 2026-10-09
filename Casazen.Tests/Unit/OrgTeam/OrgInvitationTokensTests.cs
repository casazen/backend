using Casazen.Core.OrgTeam;
using Xunit;

namespace Casazen.Tests.Unit.OrgTeam;

/// <summary>
/// AM-02: the secret of an org invitation link. 256 random bits as hex, only its SHA-256 stored, a malformed value is
/// refused as "invalid" rather than breaking anything.
/// </summary>
public class OrgInvitationTokensTests
{
    [Fact]
    public void Generate_ReturnsA256BitTokenAsLowercaseHex()
    {
        var token = OrgInvitationTokens.Generate();

        Assert.Equal(OrgInvitationTokens.Length, token.Length);
        Assert.Matches("^[0-9a-f]{64}$", token);
    }

    [Fact]
    public void Generate_NeverRepeats()
    {
        var tokens = Enumerable.Range(0, 500).Select(_ => OrgInvitationTokens.Generate()).ToHashSet();

        Assert.Equal(500, tokens.Count);
    }

    [Fact]
    public void Hash_IsTheSha256OfTheTokenAndNeverTheTokenItself()
    {
        var token = new string('a', 64);

        var hash = OrgInvitationTokens.Hash(token);

        Assert.Equal(64, hash.Length);
        Assert.Matches("^[0-9a-f]{64}$", hash);
        Assert.NotEqual(token, hash);
        Assert.Equal(hash, OrgInvitationTokens.Hash(token));
        // SHA-256("aaaa…a" x64), computed independently.
        Assert.Equal(
            Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token))),
            hash);
    }

    [Fact]
    public void Hash_TwoTokens_GiveTwoHashes()
    {
        Assert.NotEqual(
            OrgInvitationTokens.Hash(OrgInvitationTokens.Generate()),
            OrgInvitationTokens.Hash(OrgInvitationTokens.Generate()));
    }

    [Fact]
    public void TryNormalize_WellFormedToken_IsTrimmedAndLowercased()
    {
        var token = OrgInvitationTokens.Generate();

        Assert.True(OrgInvitationTokens.TryNormalize($"  {token.ToUpperInvariant()}\n", out var normalized));
        Assert.Equal(token, normalized);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcde")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef0")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdeg")]
    [InlineData("3f9c1d52-9f0e-4b8a-8d65-2c4e1f0b9a11")]
    public void TryNormalize_NotAToken_IsRefused(string? value)
    {
        Assert.False(OrgInvitationTokens.TryNormalize(value, out _));
    }

    [Fact]
    public void Matches_TheTokenOfTheStoredHash_IsTrue()
    {
        var token = OrgInvitationTokens.Generate();

        Assert.True(OrgInvitationTokens.Matches(OrgInvitationTokens.Hash(token), token));
    }

    [Fact]
    public void Matches_AnotherTokenOrNoHash_IsFalse()
    {
        var token = OrgInvitationTokens.Generate();
        var hash = OrgInvitationTokens.Hash(token);

        Assert.False(OrgInvitationTokens.Matches(hash, OrgInvitationTokens.Generate()));
        Assert.False(OrgInvitationTokens.Matches(token, token));
        Assert.False(OrgInvitationTokens.Matches(null, token));
        Assert.False(OrgInvitationTokens.Matches(string.Empty, token));
        Assert.False(OrgInvitationTokens.Matches(hash[..63], token));
    }
}
