using System.Net;
using System.Security.Claims;
using Casazen.Web.Extensions;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Casazen.Tests.Unit.Infrastructure;

/// <summary>
/// UI-13a: the rate limit of the global search. The palette asks at every pause in typing, so the limit is per user (the people
/// of one office share an address), 60 a minute, set by <c>RateLimiting:GlobalSearch:*</c> like the other policies.
/// </summary>
public class GlobalSearchRateLimitTests
{
    private static RateLimitPolicyDefinition Policy() =>
        RateLimitingServiceCollectionExtensions.Policies.Single(policy => policy.Name == RateLimitPolicies.GlobalSearch);

    private static IConfiguration Configuration(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build();

    private static DefaultHttpContext Context(string? subject, string address)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(address);
        if (subject is not null)
            context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", subject)], "Test"));
        return context;
    }

    [Fact]
    public void Policy_IsRegisteredOnce_SixtyAMinute_PartitionedByUser()
    {
        var policy = Policy();

        Assert.Equal("GlobalSearch", RateLimitPolicies.GlobalSearch);
        Assert.Equal((60, TimeSpan.FromMinutes(1)), (policy.DefaultPermitLimit, policy.DefaultWindow));
        Assert.True(policy.PartitionByUser);
        Assert.False(policy.PartitionByToken);
        Assert.Null(policy.LegacyPermitLimitKey);
        Assert.Single(RateLimitingServiceCollectionExtensions.Policies, p => p.Name == RateLimitPolicies.GlobalSearch);
    }

    [Fact]
    public void Policy_NothingConfigured_UsesTheDefaults_NoQueue()
    {
        var options = RateLimitingServiceCollectionExtensions.ResolveOptions(Policy(), Configuration());

        Assert.Equal(60, options.PermitLimit);
        Assert.Equal(TimeSpan.FromMinutes(1), options.Window);
        Assert.Equal(0, options.QueueLimit);
    }

    [Fact]
    public void Policy_IsSetByItsOwnConfigurationKeys()
    {
        var options = RateLimitingServiceCollectionExtensions.ResolveOptions(
            Policy(),
            Configuration(("RateLimiting:GlobalSearch:PermitLimit", "30"), ("RateLimiting:GlobalSearch:WindowSeconds", "20")));

        Assert.Equal((30, TimeSpan.FromSeconds(20)), (options.PermitLimit, options.Window));
    }

    [Fact]
    public void Policy_ANonPositiveLimit_FailsTheStartup()
    {
        Assert.Throws<InvalidOperationException>(() => RateLimitingServiceCollectionExtensions.ResolveOptions(
            Policy(), Configuration(("RateLimiting:GlobalSearch:PermitLimit", "0"))));
    }

    [Fact]
    public void Partition_TwoUsersBehindTheSameAddress_DoNotShareTheLimit()
    {
        var anna = RateLimitingServiceCollectionExtensions.GetPartitionKey(Context("auth0|anna", "203.0.113.7"), false, partitionByUser: true);
        var bruno = RateLimitingServiceCollectionExtensions.GetPartitionKey(Context("auth0|bruno", "203.0.113.7"), false, partitionByUser: true);

        Assert.NotEqual(anna, bruno);
    }

    [Fact]
    public void Partition_TheSameUserFromTwoAddresses_SharesTheLimit()
    {
        var home = RateLimitingServiceCollectionExtensions.GetPartitionKey(Context("auth0|anna", "203.0.113.7"), false, partitionByUser: true);
        var phone = RateLimitingServiceCollectionExtensions.GetPartitionKey(Context("auth0|anna", "198.51.100.9"), false, partitionByUser: true);

        Assert.Equal(home, phone);
    }

    [Fact]
    public void Partition_TheKeyHoldsAHashAndNeverTheSubjectNorTheAddress()
    {
        var key = RateLimitingServiceCollectionExtensions.GetPartitionKey(Context("auth0|anna", "203.0.113.7"), false, partitionByUser: true);

        Assert.StartsWith("user:", key, StringComparison.Ordinal);
        Assert.DoesNotContain("anna", key, StringComparison.Ordinal);
        Assert.DoesNotContain("203.0.113.7", key, StringComparison.Ordinal);
        Assert.Matches("^user:[0-9A-F]{32}$", key);
    }

    [Fact]
    public void Partition_WithoutAUser_FallsBackToTheAddress()
    {
        var key = RateLimitingServiceCollectionExtensions.GetPartitionKey(Context(null, "203.0.113.7"), false, partitionByUser: true);

        Assert.Equal("203.0.113.7", key);
    }

    [Fact]
    public void Partition_ThePoliciesOfTheAnonymousEndpoints_StayPerAddress_EvenForASignedInCaller()
    {
        // The other policies do not look at the user: a signed-in caller of a public endpoint is limited as anybody behind its address.
        var key = RateLimitingServiceCollectionExtensions.GetPartitionKey(Context("auth0|anna", "203.0.113.7"), partitionByToken: false);

        Assert.Equal("203.0.113.7", key);
    }
}
