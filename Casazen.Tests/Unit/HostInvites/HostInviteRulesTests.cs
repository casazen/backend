using Casazen.Core.Entities;
using Casazen.Core.HostInvites;
using Casazen.Core.Options;
using Casazen.Core.Suppliers;
using Xunit;

namespace Casazen.Tests.Unit.HostInvites;

public class HostInviteRulesTests
{
    private static readonly DateTime Now = new(2026, 10, 13, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Generate_Twice_ReturnsDifferent64CharLowercaseHexTokens()
    {
        var first = SupplierInviteTokens.Generate();
        var second = SupplierInviteTokens.Generate();

        Assert.NotEqual(first, second);
        Assert.Equal(SupplierInviteTokens.Length, first.Length);
        Assert.True(SupplierInviteTokens.TryNormalize(first, out var normalized));
        Assert.Equal(first, normalized);
    }

    [Fact]
    public void Hash_Token_IsDeterministicSha256HexAndNotTheToken()
    {
        var token = SupplierInviteTokens.Generate();

        var hash = SupplierInviteTokens.Hash(token);

        Assert.Equal(hash, SupplierInviteTokens.Hash(token));
        Assert.Equal(64, hash.Length);
        Assert.NotEqual(token, hash);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]
    [InlineData("3fa85f64-5717-4562-b3fc-2c963f66afa6")]
    public void TryNormalize_MalformedToken_ReturnsFalse(string? value)
    {
        Assert.False(SupplierInviteTokens.TryNormalize(value, out _));
    }

    [Fact]
    public void TryNormalize_UppercaseAndPaddedToken_IsTrimmedAndLowercased()
    {
        var token = SupplierInviteTokens.Generate();

        Assert.True(SupplierInviteTokens.TryNormalize($"  {token.ToUpperInvariant()} ", out var normalized));
        Assert.Equal(token, normalized);
    }

    [Fact]
    public void Validity_IsSevenDays()
    {
        Assert.Equal(TimeSpan.FromDays(7), HostInviteRules.Validity);
    }

    [Theory]
    [InlineData("  Mario.Rossi@Example.COM ", "mario.rossi@example.com")]
    [InlineData("a@b.it", "a@b.it")]
    [InlineData(null, "")]
    public void NormalizeEmail_Value_IsTrimmedAndLowercase(string? value, string expected)
    {
        Assert.Equal(expected, HostInviteRules.NormalizeEmail(value));
    }

    [Theory]
    [InlineData("en", "en")]
    [InlineData("EN", "en")]
    [InlineData("fr", "it")]
    [InlineData(null, "it")]
    public void NormalizeLocale_Value_IsSupportedLanguageOrItalian(string? value, string expected)
    {
        Assert.Equal(expected, HostInviteRules.NormalizeLocale(value));
    }

    [Fact]
    public void StateOf_NotUsedNotRevokedNotExpired_IsPending()
    {
        Assert.Equal(HostInviteState.Pending, HostInviteRules.StateOf(Invite(), Now));
    }

    [Fact]
    public void StateOf_ExpiresExactlyNow_IsExpired()
    {
        Assert.Equal(HostInviteState.Expired, HostInviteRules.StateOf(Invite(i => i.ExpiresAt = Now), Now));
    }

    [Fact]
    public void StateOf_UsedAndRevokedAndExpired_UsedWinsThenRevoked()
    {
        var used = Invite(i => { i.IsUsed = true; i.RevokedAt = Now; i.ExpiresAt = Now.AddDays(-1); });
        var revoked = Invite(i => { i.RevokedAt = Now; i.ExpiresAt = Now.AddDays(-1); });

        Assert.Equal(HostInviteState.Used, HostInviteRules.StateOf(used, Now));
        Assert.Equal(HostInviteState.Revoked, HostInviteRules.StateOf(revoked, Now));
    }

    [Theory]
    [InlineData(HostInviteState.Pending)]
    [InlineData(HostInviteState.Used)]
    [InlineData(HostInviteState.Expired)]
    [InlineData(HostInviteState.Revoked)]
    public void StatePredicate_EveryInvite_AgreesWithStateOf(HostInviteState state)
    {
        var invites = new[]
        {
            Invite(),
            Invite(i => i.IsUsed = true),
            Invite(i => i.RevokedAt = Now.AddHours(-1)),
            Invite(i => i.ExpiresAt = Now.AddMinutes(-1)),
            Invite(i => { i.IsUsed = true; i.RevokedAt = Now; i.ExpiresAt = Now.AddDays(-3); }),
        };

        var viaPredicate = invites.AsQueryable().Where(HostInviteRules.StatePredicate(state, Now)).ToList();
        var viaStateOf = invites.Where(i => HostInviteRules.StateOf(i, Now) == state).ToList();

        Assert.Equal(viaStateOf, viaPredicate);
    }

    [Fact]
    public void ErasedEmail_Invite_IsUniqueAndNeverDeliverable()
    {
        var a = HostInviteRules.ErasedEmail(Guid.NewGuid());
        var b = HostInviteRules.ErasedEmail(Guid.NewGuid());

        Assert.NotEqual(a, b);
        Assert.EndsWith("@invalid", a, StringComparison.Ordinal);
    }

    [Fact]
    public void Options_Defaults_AreTenPerDayAndFiftyPending()
    {
        var options = new HostInviteOptions();

        Assert.Equal(10, options.DailyQuotaPerHost);
        Assert.Equal(50, options.MaxPendingPerHost);
        Assert.Equal(30, options.RetentionDays);
        Assert.Empty(options.Validate());
    }

    [Theory]
    [InlineData(0, 50, 30)]
    [InlineData(10, 0, 30)]
    [InlineData(10, 50, 0)]
    [InlineData(-1, 50, 30)]
    public void Options_ValueOutOfRange_IsRefusedAtStartup(int quota, int pending, int retention)
    {
        var options = new HostInviteOptions { DailyQuotaPerHost = quota, MaxPendingPerHost = pending, RetentionDays = retention };

        var result = new HostInviteOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
    }

    private static HostInviteRecord Invite(Action<HostInviteRecord>? configure = null)
    {
        var invite = new HostInviteRecord
        {
            Email = "invitee@example.com",
            TokenHash = SupplierInviteTokens.Hash(SupplierInviteTokens.Generate()),
            InvitedByUserId = "auth0|host",
            CreatedAt = Now.AddDays(-1),
            ExpiresAt = Now.AddDays(6),
        };
        configure?.Invoke(invite);
        return invite;
    }
}
