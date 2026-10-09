using System.Net;
using Casazen.Web.Extensions;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Casazen.Tests.Unit.Infrastructure;

/// <summary>
/// Client IP resolution, forwarded headers configuration and rate limiting partitions/limits (FD-10, #273).
/// </summary>
public class ClientIpAndRateLimitConfigurationTests
{
    [Fact]
    public void GetAddress_Ipv4MappedToIpv6_ReturnsPlainIpv4()
    {
        var context = ContextWithPeer("::ffff:203.0.113.7");

        Assert.Equal("203.0.113.7", ClientIp.GetString(context));
        Assert.Equal("203.0.113.7", ClientIp.GetRateLimitKey(context));
    }

    [Fact]
    public void GetRateLimitKey_Ipv6Client_UsesSlash64Prefix()
    {
        var first = ContextWithPeer("2001:db8:1:2:aaaa:bbbb:cccc:dddd");
        var sameSubscriber = ContextWithPeer("2001:db8:1:2::1");
        var otherSubscriber = ContextWithPeer("2001:db8:1:3::1");

        Assert.Equal("2001:db8:1:2::/64", ClientIp.GetRateLimitKey(first));
        Assert.Equal(ClientIp.GetRateLimitKey(first), ClientIp.GetRateLimitKey(sameSubscriber));
        Assert.NotEqual(ClientIp.GetRateLimitKey(first), ClientIp.GetRateLimitKey(otherSubscriber));
        Assert.Equal("2001:db8:1:2:aaaa:bbbb:cccc:dddd", ClientIp.GetString(first));
    }

    [Fact]
    public void GetRateLimitKey_UnknownPeer_ReturnsUnknownKey()
    {
        var context = new DefaultHttpContext();

        Assert.Null(ClientIp.GetString(context));
        Assert.Equal(ClientIp.UnknownKey, ClientIp.GetRateLimitKey(context));
    }

    [Fact]
    public void GetPartitionKey_TokenPolicy_CombinesIpAndTokenHashWithoutRawToken()
    {
        var tokenA = ContextWithPeer("203.0.113.7", token: "secret-token-a");
        var tokenB = ContextWithPeer("203.0.113.7", token: "secret-token-b");

        var keyA = RateLimitingServiceCollectionExtensions.GetPartitionKey(tokenA, partitionByToken: true);
        var keyB = RateLimitingServiceCollectionExtensions.GetPartitionKey(tokenB, partitionByToken: true);

        Assert.StartsWith("203.0.113.7|", keyA);
        Assert.NotEqual(keyA, keyB);
        Assert.DoesNotContain("secret-token-a", keyA);
        Assert.Equal("203.0.113.7", RateLimitingServiceCollectionExtensions.GetPartitionKey(tokenA, partitionByToken: false));
    }

    [Fact]
    public void ResolveOptions_NothingConfigured_UsesPreviousGlobalLimitPerIp()
    {
        var submit = Policy(RateLimitPolicies.GuestCheckInSubmit);

        var options = RateLimitingServiceCollectionExtensions.ResolveOptions(submit, Configuration());

        Assert.Equal(3, options.PermitLimit);
        Assert.Equal(TimeSpan.FromMinutes(1), options.Window);
        Assert.Equal(0, options.QueueLimit);
    }

    [Fact]
    public void ResolveOptions_LegacyAndNewKeys_NewKeyWins()
    {
        var create = Policy(RateLimitPolicies.PublicBookingCreate);

        var legacyOnly = RateLimitingServiceCollectionExtensions.ResolveOptions(
            create, Configuration(("DirectBooking:RateLimitPermitLimit", "25")));
        var both = RateLimitingServiceCollectionExtensions.ResolveOptions(
            create,
            Configuration(
                ("DirectBooking:RateLimitPermitLimit", "25"),
                ("RateLimiting:PublicBookingCreate:PermitLimit", "40"),
                ("RateLimiting:PublicBookingCreate:WindowSeconds", "30")));

        Assert.Equal(25, legacyOnly.PermitLimit);
        Assert.Equal(40, both.PermitLimit);
        Assert.Equal(TimeSpan.FromSeconds(30), both.Window);
    }

    [Fact]
    public void ResolveOptions_NonPositiveLimit_Throws()
    {
        var read = Policy(RateLimitPolicies.PublicRead);

        Assert.Throws<InvalidOperationException>(() => RateLimitingServiceCollectionExtensions.ResolveOptions(
            read, Configuration(("RateLimiting:PublicRead:PermitLimit", "0"))));
    }

    [Fact]
    public void Policies_EveryPolicyName_IsDefinedOnce()
    {
        var names = typeof(RateLimitPolicies).GetFields()
            .Where(field => field.IsLiteral)
            .Select(field => (string)field.GetRawConstantValue()!)
            .OrderBy(name => name)
            .ToList();

        Assert.Equal(names, RateLimitingServiceCollectionExtensions.Policies.Select(p => p.Name).OrderBy(name => name));
    }

    [Fact]
    public void Policies_TheSlotsAndTheEstimateOfTheSupplierShowcase_HaveTheirOwnTighterLimits()
    {
        // SP-09: each slots read runs the planner (cached 30 s) and each estimate may look up a comune, so neither shares the
        // 120 per minute of the plain reads.
        var slots = RateLimitingServiceCollectionExtensions.ResolveOptions(Policy(RateLimitPolicies.PublicSupplierSlots), Configuration());
        var quote = RateLimitingServiceCollectionExtensions.ResolveOptions(Policy(RateLimitPolicies.PublicSupplierQuote), Configuration());
        var read = RateLimitingServiceCollectionExtensions.ResolveOptions(Policy(RateLimitPolicies.PublicRead), Configuration());

        Assert.Equal(("PublicSupplierSlots", 60, TimeSpan.FromMinutes(1)), (RateLimitPolicies.PublicSupplierSlots, slots.PermitLimit, slots.Window));
        Assert.Equal(("PublicSupplierQuote", 30, TimeSpan.FromMinutes(1)), (RateLimitPolicies.PublicSupplierQuote, quote.PermitLimit, quote.Window));
        Assert.True(slots.PermitLimit < read.PermitLimit);
        Assert.True(quote.PermitLimit < slots.PermitLimit);
        Assert.Equal(0, slots.QueueLimit);
        Assert.Equal(0, quote.QueueLimit);
    }

    [Fact]
    public void Policies_TheSupplierShowcasePolicies_AreSetByTheirOwnConfigurationKeys()
    {
        var configuration = Configuration(
            ("RateLimiting:PublicSupplierSlots:PermitLimit", "7"),
            ("RateLimiting:PublicSupplierSlots:WindowSeconds", "20"),
            ("RateLimiting:PublicSupplierQuote:PermitLimit", "3"));

        var slots = RateLimitingServiceCollectionExtensions.ResolveOptions(Policy(RateLimitPolicies.PublicSupplierSlots), configuration);
        var quote = RateLimitingServiceCollectionExtensions.ResolveOptions(Policy(RateLimitPolicies.PublicSupplierQuote), configuration);
        var read = RateLimitingServiceCollectionExtensions.ResolveOptions(Policy(RateLimitPolicies.PublicRead), configuration);

        Assert.Equal((7, TimeSpan.FromSeconds(20)), (slots.PermitLimit, slots.Window));
        Assert.Equal(3, quote.PermitLimit);
        Assert.Equal(120, read.PermitLimit); // the plain reads are not touched
    }

    [Fact]
    public void ConfigureForwardedHeaders_NothingConfigured_TrustsOneHopFromAnyPeer()
    {
        var options = new ForwardedHeadersOptions();

        ForwardedHeadersServiceCollectionExtensions.Configure(options, Configuration());

        Assert.Equal(ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto, options.ForwardedHeaders);
        Assert.Equal(1, options.ForwardLimit);
        Assert.Empty(options.KnownIPNetworks);
        Assert.Empty(options.KnownProxies);
    }

    [Fact]
    public void ConfigureForwardedHeaders_CommaSeparatedAndArrayValues_AreParsed()
    {
        var options = new ForwardedHeadersOptions();

        ForwardedHeadersServiceCollectionExtensions.Configure(options, Configuration(
            ("KnownNetworks", "100.64.0.0/10, 10.0.0.0/8"),
            ("KnownProxies:0", "192.0.2.10"),
            ("KnownProxies:1", "2001:db8::10"),
            ("ForwardLimit", "2")));

        Assert.Equal(["100.64.0.0/10", "10.0.0.0/8"], options.KnownIPNetworks.Select(n => n.ToString()));
        Assert.Equal([IPAddress.Parse("192.0.2.10"), IPAddress.Parse("2001:db8::10")], options.KnownProxies);
        Assert.Equal(2, options.ForwardLimit);
    }

    [Theory]
    [InlineData("KnownNetworks", "100.64.0.0/99")]
    [InlineData("KnownProxies", "not-an-ip")]
    [InlineData("ForwardLimit", "0")]
    public void ConfigureForwardedHeaders_InvalidValue_Throws(string key, string value)
    {
        Assert.Throws<InvalidOperationException>(() =>
            ForwardedHeadersServiceCollectionExtensions.Configure(new ForwardedHeadersOptions(), Configuration((key, value))));
    }

    private static RateLimitPolicyDefinition Policy(string name) =>
        RateLimitingServiceCollectionExtensions.Policies.Single(policy => policy.Name == name);

    private static IConfiguration Configuration(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build();

    private static DefaultHttpContext ContextWithPeer(string address, string? token = null)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(address);
        if (token is not null)
            context.Request.RouteValues["token"] = token;
        return context;
    }
}
