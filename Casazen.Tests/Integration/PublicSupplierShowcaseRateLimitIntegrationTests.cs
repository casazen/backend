using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// SP-09: the slots and the estimate of the public showcase have a rate limit of their own (<c>PublicSupplierSlots</c>,
/// <c>PublicSupplierQuote</c>), per client IP, answered with the usual 429 <c>rate_limited</c> and <c>Retry-After</c>; the plain
/// reads (the page, the services) keep the <c>PublicRead</c> quota and are not eaten by the other two. Here both new limits are
/// two requests a minute.
/// </summary>
[Collection(SupplierCatalogHostsCollection.Name)]
public class PublicSupplierShowcaseRateLimitIntegrationTests(PublicShowcaseThrottledFactory factory) : IClassFixture<PublicShowcaseThrottledFactory>
{
    [Fact]
    public async Task Slots_AThirdRequestInAMinuteFromTheSameIp_Is429_AnotherIpIsNot()
    {
        var supplier = await PublicShowcaseTestData.SeedSupplierAsync(factory);
        var slug = await PublicShowcaseTestData.SeedServiceAsync(factory, supplier.OrgId);
        using var client = factory.CreateClient();
        var url = $"/api/public/suppliers/{supplier.Slug}/slots?service={slug}";

        Assert.Equal(HttpStatusCode.OK, (await GetAsync(client, url, "203.0.113.11")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(client, url, "203.0.113.11")).StatusCode);
        var limited = await GetAsync(client, url, "203.0.113.11");
        var other = await GetAsync(client, url, "203.0.113.12");

        await ClientIpRateLimitingIntegrationTests.AssertRateLimitedAsync(limited);
        Assert.Equal(HttpStatusCode.OK, other.StatusCode);
    }

    [Fact]
    public async Task Quote_AThirdRequestInAMinuteFromTheSameIp_Is429_WhateverTheServiceOrTheSupplierAsked()
    {
        var a = await PublicShowcaseTestData.SeedSupplierAsync(factory);
        var b = await PublicShowcaseTestData.SeedSupplierAsync(factory);
        var slugA = await PublicShowcaseTestData.SeedServiceAsync(factory, a.OrgId);
        var slugB = await PublicShowcaseTestData.SeedServiceAsync(factory, b.OrgId);
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await PostAsync(client, a.Slug, slugA, "203.0.113.21")).StatusCode);
        // Another supplier and another service do not open a new quota: it is the visitor's, not the target's.
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(client, b.Slug, slugB, "203.0.113.21")).StatusCode);
        var limited = await PostAsync(client, a.Slug, "non-esiste", "203.0.113.21");

        await ClientIpRateLimitingIntegrationTests.AssertRateLimitedAsync(limited);
    }

    [Fact]
    public async Task TheTwoLimits_AreSeparate_AndThePlainReadsAreNotTouchedByThem()
    {
        var supplier = await PublicShowcaseTestData.SeedSupplierAsync(factory);
        var slug = await PublicShowcaseTestData.SeedServiceAsync(factory, supplier.OrgId);
        using var client = factory.CreateClient();
        const string ip = "203.0.113.31";
        var slots = $"/api/public/suppliers/{supplier.Slug}/slots?service={slug}";
        for (var i = 0; i < 3; i++)
            await GetAsync(client, slots, ip); // the slots quota is spent

        Assert.Equal(HttpStatusCode.TooManyRequests, (await GetAsync(client, slots, ip)).StatusCode);
        // The estimate has its own quota, and the page and the services are PublicRead: all still answer.
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(client, supplier.Slug, slug, ip)).StatusCode);
        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await GetAsync(client, $"/api/public/suppliers/{supplier.Slug}", ip)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await GetAsync(client, $"/api/public/suppliers/{supplier.Slug}/services", ip)).StatusCode);
        }
    }

    [Fact]
    public async Task TheLimitedAnswer_IsTheStandardProblem_WithRetryAfter_AndTranslated()
    {
        var supplier = await PublicShowcaseTestData.SeedSupplierAsync(factory);
        var slug = await PublicShowcaseTestData.SeedServiceAsync(factory, supplier.OrgId);
        using var client = factory.CreateClient();
        var url = $"/api/public/suppliers/{supplier.Slug}/slots?service={slug}";
        await GetAsync(client, url, "203.0.113.41");
        await GetAsync(client, url, "203.0.113.41");

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add(TestPeerIpStartupFilter.HeaderName, "203.0.113.41");
        request.Headers.AcceptLanguage.ParseAdd("en");
        var response = await client.SendAsync(request);

        var problem = await ClientIpRateLimitingIntegrationTests.AssertRateLimitedAsync(response);
        Assert.Equal("Too many requests in a short time. Try again in 60 seconds.", problem.GetProperty("detail").GetString());
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    private static Task<HttpResponseMessage> GetAsync(HttpClient client, string url, string ip)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add(TestPeerIpStartupFilter.HeaderName, ip);
        return client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, string supplierSlug, string serviceSlug, string ip)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/public/suppliers/{supplierSlug}/quote")
        {
            Content = JsonContent.Create(new { service = serviceSlug }),
        };
        request.Headers.Add(TestPeerIpStartupFilter.HeaderName, ip);
        return client.SendAsync(request);
    }
}
