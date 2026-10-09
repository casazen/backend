using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Infrastructure.Services;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// SP-09: the short cache of the public slots (<see cref="PublicSupplierSlotCache"/>): a plan lives 30 seconds, the cache is
/// bounded, and a full cache refuses a new plan instead of growing or pushing a fresh one out.
/// </summary>
public class PublicSupplierSlotCacheTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 8, 10, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _clock = new(Start);

    [Fact]
    public void Defaults_AreThirtySecondsAndTwoThousandPlans()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), PublicShowcaseLimits.SlotsCacheTtl);
        Assert.Equal(2000, PublicShowcaseLimits.SlotsCacheCapacity);
    }

    [Fact]
    public void TryGet_NothingStored_IsAMiss()
    {
        Assert.False(Cache().TryGet("a", out _));
    }

    [Fact]
    public void TryGet_AFreshPlan_IsTheSameOne()
    {
        var cache = Cache();
        var plan = Plan();

        cache.Set("a", plan);

        Assert.True(cache.TryGet("a", out var found));
        Assert.Same(plan, found);
    }

    [Fact]
    public void TryGet_ThePlan_LivesThirtySeconds_Exactly()
    {
        var cache = Cache();
        cache.Set("a", Plan());

        _clock.Advance(TimeSpan.FromMilliseconds(29_999));
        Assert.True(cache.TryGet("a", out _));

        _clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.False(cache.TryGet("a", out _));
        Assert.Equal(0, cache.Count); // the expired entry is dropped when it is met
    }

    [Fact]
    public void Set_ANewPlanForTheSameKey_ReplacesTheOldOneAndStartsItsTimeAgain()
    {
        var cache = Cache();
        cache.Set("a", Plan());
        _clock.Advance(TimeSpan.FromSeconds(20));
        var newer = Plan();

        cache.Set("a", newer);
        _clock.Advance(TimeSpan.FromSeconds(20)); // 40 s after the first, 20 after the second

        Assert.True(cache.TryGet("a", out var found));
        Assert.Same(newer, found);
    }

    [Fact]
    public void TryGet_TwoKeys_AreTwoPlans_ASupplierNeverReadsAnothers()
    {
        var cache = Cache();
        var first = Plan();
        var second = Plan();
        cache.Set("supplier-a|pulizia", first);
        cache.Set("supplier-b|pulizia", second);

        Assert.True(cache.TryGet("supplier-a|pulizia", out var a));
        Assert.True(cache.TryGet("supplier-b|pulizia", out var b));
        Assert.Same(first, a);
        Assert.Same(second, b);
        Assert.False(cache.TryGet("supplier-c|pulizia", out _));
    }

    [Fact]
    public void Set_WhenFull_ANewKeyIsNotRemembered_AndNothingFreshIsPushedOut()
    {
        var cache = Cache(capacity: 2);
        cache.Set("a", Plan());
        cache.Set("b", Plan());

        cache.Set("c", Plan());

        Assert.Equal(2, cache.Count);
        Assert.False(cache.TryGet("c", out _));
        Assert.True(cache.TryGet("a", out _));
        Assert.True(cache.TryGet("b", out _));
    }

    [Fact]
    public void Set_WhenFull_AKeyThatIsAlreadyThere_CanStillBeReplaced()
    {
        var cache = Cache(capacity: 2);
        cache.Set("a", Plan());
        cache.Set("b", Plan());
        var newer = Plan();

        cache.Set("a", newer);

        Assert.True(cache.TryGet("a", out var found));
        Assert.Same(newer, found);
        Assert.Equal(2, cache.Count);
    }

    [Fact]
    public void Set_WhenFull_TheExpiredPlansLeaveFirst_SoANewKeyFindsRoom()
    {
        var cache = Cache(capacity: 2);
        cache.Set("a", Plan());
        _clock.Advance(TimeSpan.FromSeconds(10));
        cache.Set("b", Plan());
        _clock.Advance(TimeSpan.FromSeconds(21)); // a is 31 s old, b 21 s

        cache.Set("c", Plan());

        Assert.False(cache.TryGet("a", out _));
        Assert.True(cache.TryGet("b", out _));
        Assert.True(cache.TryGet("c", out _));
    }

    [Fact]
    public void Set_WhenFullOfFreshPlans_NothingGrows_EvenWithManyKeys()
    {
        var cache = Cache(capacity: 10);

        for (var i = 0; i < 1_000; i++)
            cache.Set($"key-{i}", Plan());

        Assert.Equal(10, cache.Count);
    }

    private PublicSupplierSlotCache Cache(int? capacity = null) => new(_clock, capacity: capacity);

    private static PublicSlotPlan Plan() => new(new DateOnly(2026, 11, 12), []);
}
