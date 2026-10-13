using Casazen.Core.Options;
using Casazen.Infrastructure.External;
using Microsoft.Extensions.Options;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>A8-25 (SE-05): the cache of AI answers is bounded in size, expires, and is keyed by org.</summary>
public class AiResponseCacheTests
{
    private static readonly Guid OrgA = Guid.NewGuid();
    private static readonly Guid OrgB = Guid.NewGuid();

    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 9, 24, 10, 0, 0, TimeSpan.Zero));

    private AiResponseCache Create(int maxEntries = 500, int maxPerOrg = 50, int ttlHours = 24) =>
        new(Options.Create(new AiCacheOptions { MaxEntries = maxEntries, MaxEntriesPerOrg = maxPerOrg, TtlHours = ttlHours }), _clock);

    private static string Value(string text) => text;

    [Fact]
    public void TryGet_AfterSet_ReturnsTheValue()
    {
        var cache = Create();

        cache.Set(null, "k", Value("one"));

        Assert.True(cache.TryGet<string>(null, "k", out var value));
        Assert.Equal("one", value);
    }

    [Fact]
    public void TryGet_UnknownKeyOrOtherType_ReturnsFalse()
    {
        var cache = Create();
        cache.Set(null, "k", Value("one"));

        Assert.False(cache.TryGet<string>(null, "other", out _));
        Assert.False(cache.TryGet<List<int>>(null, "k", out _));
    }

    [Fact]
    public void TryGet_SameKeyOfAnotherOrg_IsNeverServed()
    {
        var cache = Create();
        cache.Set(OrgA, "supplier-match:cleaning:Normal:0", Value("answer of A"));

        Assert.False(cache.TryGet<string>(OrgB, "supplier-match:cleaning:Normal:0", out _));
        Assert.False(cache.TryGet<string>(null, "supplier-match:cleaning:Normal:0", out _));
        Assert.True(cache.TryGet<string>(OrgA, "supplier-match:cleaning:Normal:0", out var value));
        Assert.Equal("answer of A", value);
    }

    [Fact]
    public void TryGet_AfterTheTimeToLive_ReturnsFalseAndFreesTheEntry()
    {
        var cache = Create(ttlHours: 24);
        cache.Set(OrgA, "k", Value("one"));

        _clock.Advance(TimeSpan.FromHours(23));
        Assert.True(cache.TryGet<string>(OrgA, "k", out _));

        _clock.Advance(TimeSpan.FromHours(1));
        Assert.False(cache.TryGet<string>(OrgA, "k", out _));
        Assert.Equal(0, cache.Count);
        Assert.Equal(0, cache.CountForOrg(OrgA));
    }

    [Fact]
    public void Set_SameKeyAgain_ReplacesTheValueAndRestartsTheTimeToLive()
    {
        var cache = Create(ttlHours: 24);
        cache.Set(null, "k", Value("old"));
        _clock.Advance(TimeSpan.FromHours(20));

        cache.Set(null, "k", Value("new"));
        _clock.Advance(TimeSpan.FromHours(20));

        Assert.True(cache.TryGet<string>(null, "k", out var value));
        Assert.Equal("new", value);
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public void Set_PastTheTotalLimit_EvictsTheOldestEntries()
    {
        var cache = Create(maxEntries: 3);

        for (var i = 0; i < 10; i++)
        {
            cache.Set(null, $"k{i}", Value($"v{i}"));
            _clock.Advance(TimeSpan.FromMinutes(1));
        }

        Assert.Equal(3, cache.Count);
        Assert.False(cache.TryGet<string>(null, "k6", out _));
        Assert.True(cache.TryGet<string>(null, "k7", out _));
        Assert.True(cache.TryGet<string>(null, "k9", out _));
    }

    [Fact]
    public void Set_PastTheOrgLimit_ReplacesTheOldestEntryOfThatOrgAndKeepsTheOthers()
    {
        var cache = Create(maxEntries: 100, maxPerOrg: 2);
        cache.Set(OrgB, "b1", Value("b1"));
        cache.Set(OrgA, "a1", Value("a1"));
        cache.Set(OrgA, "a2", Value("a2"));

        cache.Set(OrgA, "a3", Value("a3"));

        Assert.Equal(2, cache.CountForOrg(OrgA));
        Assert.False(cache.TryGet<string>(OrgA, "a1", out _));
        Assert.True(cache.TryGet<string>(OrgA, "a2", out _));
        Assert.True(cache.TryGet<string>(OrgA, "a3", out _));
        // One org filling its quota never pushes out the entry of another org.
        Assert.True(cache.TryGet<string>(OrgB, "b1", out _));
        Assert.Equal(3, cache.Count);
    }

    [Fact]
    public void Set_ThousandsOfDistinctKeys_StaysWithinTheLimitsAndExpiresEverything()
    {
        var cache = Create(maxEntries: 50, maxPerOrg: 10, ttlHours: 1);

        for (var i = 0; i < 5000; i++)
            cache.Set(i % 2 == 0 ? OrgA : null, $"key-{i}", Value($"v{i}"));

        Assert.True(cache.Count <= 50);
        Assert.True(cache.CountForOrg(OrgA) <= 10);

        _clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(0, cache.Count);
        Assert.Equal(0, cache.CountForOrg(OrgA));
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(-5, -1, -1)]
    public void Normalized_NonPositiveLimits_FallBackToTheDefaults(int maxEntries, int maxPerOrg, int ttlHours)
    {
        var normalized = new AiCacheOptions { MaxEntries = maxEntries, MaxEntriesPerOrg = maxPerOrg, TtlHours = ttlHours }.Normalized();

        Assert.Equal(AiCacheOptions.DefaultMaxEntries, normalized.MaxEntries);
        Assert.Equal(AiCacheOptions.DefaultMaxEntriesPerOrg, normalized.MaxEntriesPerOrg);
        Assert.Equal(AiCacheOptions.DefaultTtlHours, normalized.TtlHours);
    }
}
