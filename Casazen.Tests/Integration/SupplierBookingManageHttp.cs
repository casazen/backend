using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email.Templates;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// A booking made through the API of a supplier's showcase and checked through the link of its e-mail (SP-10), with what the customer
/// types to find it again (SP-11): the id of the request (for the supplier's side of a test), the code as people read it
/// (<c>XXXXX-XXXXX</c>), the address and the slug.
/// </summary>
internal sealed record BookedForManagement(Guid RequestId, string Code, string Email, string Slug)
{
    /// <summary>The body of a call of the customer's area: slug, code and address, and the <paramref name="more"/> of the call.</summary>
    public object Access(object? more = null) =>
        more is null
            ? new { slug = Slug, code = Code, email = Email }
            : SupplierBookingManageHttp.Merge(new { slug = Slug, code = Code, email = Email }, more);
}

/// <summary>What the HTTP tests of the customer's own area of a booking (SP-11) share.</summary>
internal static class SupplierBookingManageHttp
{
    /// <summary>A booking of a new customer (a new address), made and checked through the API.</summary>
    public static async Task<BookedForManagement> BookAsync(
        PublicBookingFactory factory,
        HttpClient client,
        BookableSupplier supplier,
        DateTime start,
        string? peerIp = null,
        string? email = null)
    {
        email ??= PublicBookingTestData.NewEmail();
        var create = new HttpRequestMessage(HttpMethod.Post, $"/api/public/suppliers/{supplier.Slug}/bookings")
        {
            Content = JsonContent.Create(PublicBookingTestData.Body(supplier.Service, start, email)),
        };
        if (peerIp is not null)
            create.Headers.Add(TestPeerIpStartupFilter.HeaderName, peerIp);
        var created = await client.SendAsync(create);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var holdId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        // The last verification e-mail to this address: the same address may have booked another supplier before.
        var (_, token) = PublicBookingTestData.LinkOf(
            factory.EmailsTo(email).Last(e => e.Template == EmailTemplates.Names.SupplierBookingVerification));

        var confirm = new HttpRequestMessage(HttpMethod.Post, $"/api/public/suppliers/{supplier.Slug}/bookings/{holdId}/confirm-email")
        {
            Content = JsonContent.Create(new { token }),
        };
        if (peerIp is not null)
            confirm.Headers.Add(TestPeerIpStartupFilter.HeaderName, peerIp);
        var confirmed = await client.SendAsync(confirm);
        Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
        var code = (await confirmed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("publicCode").GetString()!;

        return new BookedForManagement(await RequestIdOfAsync(factory, supplier, code), code, email, supplier.Slug);
    }

    /// <summary>The id of the supplier's request with the code <paramref name="code"/> (<c>XXXXX-XXXXX</c>).</summary>
    public static async Task<Guid> RequestIdOfAsync(PublicBookingFactory factory, BookableSupplier supplier, string code)
    {
        var stored = code.Replace("-", string.Empty, StringComparison.Ordinal);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.ServiceRequests
            .IgnoreQueryFilters()
            .Where(r => r.SupplierOrgId == supplier.OrgId && r.PublicCode == stored)
            .Select(r => r.Id)
            .SingleAsync();
    }

    /// <summary>The supplier proposes another time for a booking, through its console API.</summary>
    public static async Task ProposeAsync(
        PublicBookingFactory factory,
        BookableSupplier supplier,
        BookedForManagement booked,
        DateTime start,
        string? message)
    {
        using var asSupplier = supplier.Client(factory);
        var response = await asSupplier.PostAsJsonAsync($"/api/service-requests/{booked.RequestId}/propose-time", new { startUtc = start, message });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    public static Task<HttpResponseMessage> PostAsync(HttpClient client, string path, object body, string? peerIp = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/public/supplier-bookings/{path}") { Content = JsonContent.Create(body) };
        if (peerIp is not null)
            request.Headers.Add(TestPeerIpStartupFilter.HeaderName, peerIp);
        return client.SendAsync(request);
    }

    /// <summary>The properties of <paramref name="left"/> and <paramref name="right"/> in one object (the right one wins).</summary>
    public static object Merge(object left, object right)
    {
        var merged = new Dictionary<string, JsonElement>();
        foreach (var source in new[] { left, right })
        {
            foreach (var property in JsonSerializer.SerializeToElement(source).EnumerateObject())
                merged[property.Name] = property.Value;
        }

        return merged;
    }

    public static bool IsNull(JsonElement element, string name) =>
        !element.TryGetProperty(name, out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined;

    /// <summary>The body of a problem without the id of the trace, which is different on every call; the rest has to be the same.</summary>
    public static string SameBody(JsonElement problem) =>
        JsonSerializer.Serialize(problem.EnumerateObject()
            .Where(p => p.Name != "traceId")
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .ToDictionary(p => p.Name, p => p.Value));

    public static void AssertPrivateAnswer(HttpResponseMessage response)
    {
        Assert.Equal("noindex", Assert.Single(response.Headers.GetValues("X-Robots-Tag")));
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString());
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    public static async Task<JsonElement> AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(code, problem.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("detail").GetString()));
        Assert.DoesNotContain("SupplierBooking", problem.GetProperty("detail").GetString()!);
        return problem;
    }

    public static TimeSpan Offset(DateTime utc) =>
        TimeZoneInfo.FindSystemTimeZoneById("Europe/Rome").GetUtcOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc));
}
