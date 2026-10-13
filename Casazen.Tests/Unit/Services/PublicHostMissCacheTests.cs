using Casazen.Infrastructure.Services;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>The memory of the hosts that resolve to no org is bounded (BK-16): a flood of made-up hosts cannot grow it.</summary>
public class PublicHostMissCacheTests
{
    [Fact]
    public void Add_Host_IsRememberedUntilRemoved()
    {
        using var cache = new PublicHostMissCache();

        cache.Add("unknown.example.test", TimeSpan.FromMinutes(1));
        Assert.True(cache.Contains("unknown.example.test"));
        Assert.False(cache.Contains("other.example.test"));

        cache.Remove("unknown.example.test");
        Assert.False(cache.Contains("unknown.example.test"));
    }

    [Fact]
    public async Task Add_Host_IsForgottenAfterItsTtl()
    {
        using var cache = new PublicHostMissCache();

        cache.Add("unknown.example.test", TimeSpan.FromMilliseconds(30));
        await Task.Delay(150);

        Assert.False(cache.Contains("unknown.example.test"));
    }

    [Fact]
    public void Add_MoreHostsThanTheCapacity_NeverRemembersMoreThanTheCapacity()
    {
        using var cache = new PublicHostMissCache();
        var hosts = Enumerable.Range(0, PublicHostMissCache.Capacity * 2).Select(i => $"made-up-{i}.example.test").ToList();

        foreach (var host in hosts)
            cache.Add(host, TimeSpan.FromMinutes(1));

        Assert.True(hosts.Count(cache.Contains) <= PublicHostMissCache.Capacity);
    }
}
