using Casazen.Web.Infrastructure;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Casazen.Tests.Unit.Infrastructure;

/// <summary>FD-21 (A8-01): the endpoints that call an AI provider are limited per user and per organization.</summary>
public class AiRequestRateLimiterTests
{
    [Fact]
    public void TryAcquire_UserOverLimit_RejectedWhileOtherUserAllowed()
    {
        using var limiter = Create(userLimit: 2, orgLimit: 100);
        var org = Guid.NewGuid();

        Assert.True(limiter.TryAcquire("user:a", org).IsAllowed);
        Assert.True(limiter.TryAcquire("user:a", org).IsAllowed);
        var rejected = limiter.TryAcquire("user:a", org);

        Assert.False(rejected.IsAllowed);
        Assert.Equal("AiPerUser", rejected.Policy);
        Assert.True(rejected.RetryAfter > TimeSpan.Zero);
        Assert.True(limiter.TryAcquire("user:b", org).IsAllowed);
    }

    [Fact]
    public void TryAcquire_OrgOverLimit_RejectsEveryUserOfTheOrgOnly()
    {
        using var limiter = Create(userLimit: 100, orgLimit: 3);
        var org = Guid.NewGuid();

        Assert.True(limiter.TryAcquire("user:a", org).IsAllowed);
        Assert.True(limiter.TryAcquire("user:b", org).IsAllowed);
        Assert.True(limiter.TryAcquire("user:c", org).IsAllowed);
        var rejected = limiter.TryAcquire("user:d", org);

        Assert.False(rejected.IsAllowed);
        Assert.Equal("AiPerOrg", rejected.Policy);
        Assert.True(limiter.TryAcquire("user:d", Guid.NewGuid()).IsAllowed);
    }

    [Fact]
    public void TryAcquire_DefaultConfiguration_AllowsTwentyPerUserPerHour()
    {
        using var limiter = new AiRequestRateLimiter(new ConfigurationBuilder().Build());

        for (var i = 0; i < 20; i++)
            Assert.True(limiter.TryAcquire("user:a", null).IsAllowed);

        Assert.False(limiter.TryAcquire("user:a", null).IsAllowed);
    }

    private static AiRequestRateLimiter Create(int userLimit, int orgLimit) =>
        new(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RateLimiting:AiPerUser:PermitLimit"] = userLimit.ToString(),
                ["RateLimiting:AiPerOrg:PermitLimit"] = orgLimit.ToString(),
            })
            .Build());
}
