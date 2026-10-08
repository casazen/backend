using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Casazen.Core.Entities.Enums;
using Casazen.Infrastructure.Data;
using Casazen.Tests.Integration.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// SP-09 on PostgreSQL, where it counts: the public reads are anonymous and the tables they read are keyed by the supplier org
/// and not tenant-filtered, so isolation between two suppliers, the join on the active profile, real timestamps against the
/// planner, the order and limit of the response-time sample, the <c>jsonb</c> supplements and parallel reads are proved on the
/// real database. They are skipped locally when no PostgreSQL is available and always run on CI
/// (<see cref="PostgresFactAttribute"/>); the same HTTP behavior without these guarantees is in
/// <see cref="PublicSupplierShowcaseIntegrationTests"/>.
/// </summary>
[Collection(SupplierCatalogHostsCollection.Name)]
public class PublicSupplierShowcasePostgresTests(PublicShowcaseFactory factory) : IClassFixture<PublicShowcaseFactory>
{
    [PostgresFact]
    public async Task TwoSuppliers_WithTheSameServiceSlug_NeverSeeEachOthersServicesOrSlots()
    {
        var a = await PublicShowcaseTestData.SeedSupplierAsync(factory);
        var b = await PublicShowcaseTestData.SeedSupplierAsync(factory);
        await PublicShowcaseTestData.SeedServiceAsync(factory, a.OrgId, "Pulizia", slug: "pulizia", priceFromCents: 6000, durationMinutes: 120);
        await PublicShowcaseTestData.SeedServiceAsync(factory, b.OrgId, "Pulizia", slug: "pulizia", priceFromCents: 9900, durationMinutes: 60);
        await PublicShowcaseTestData.SeedServiceAsync(factory, b.OrgId, "Solo di B", slug: "solo-di-b");
        // B is blocked all of Tuesday 13 October: A's Tuesday is untouched.
        await PublicShowcaseTestData.SeedBlockAsync(
            factory, b.OrgId, new DateTime(2026, 10, 13, 6, 0, 0, DateTimeKind.Utc), new DateTime(2026, 10, 13, 17, 0, 0, DateTimeKind.Utc));
        using var client = factory.CreateClient();

        var servicesA = await client.GetFromJsonAsync<JsonElement>($"/api/public/suppliers/{a.Slug}/services");
        var servicesB = await client.GetFromJsonAsync<JsonElement>($"/api/public/suppliers/{b.Slug}/services");
        var slotsA = await client.GetFromJsonAsync<JsonElement>($"/api/public/suppliers/{a.Slug}/slots?service=pulizia&from=2026-10-13&days=1");
        var slotsB = await client.GetFromJsonAsync<JsonElement>($"/api/public/suppliers/{b.Slug}/slots?service=pulizia&from=2026-10-13&days=1");

        Assert.Equal(1, servicesA.GetProperty("total").GetInt32());
        Assert.Equal(6000, servicesA.GetProperty("items")[0].GetProperty("priceFromCents").GetInt32());
        Assert.Equal(2, servicesB.GetProperty("total").GetInt32());
        Assert.Equal(6, slotsA.GetProperty("days")[0].GetProperty("slots").GetArrayLength());
        Assert.False(slotsB.GetProperty("days")[0].GetProperty("available").GetBoolean());
        Assert.Equal(60, slotsB.GetProperty("durationMinutes").GetInt32());
        // A cannot reach B's service by its slug, and the other way round.
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/public/suppliers/{a.Slug}/services/solo-di-b")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/public/suppliers/{b.Slug}/services/solo-di-b")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/public/suppliers/{a.Slug}/slots?service=solo-di-b")).StatusCode);
    }

    [PostgresFact]
    public async Task ARequestWithHoursOfAHost_TakesItsSlots_OnRealTimestamps_AndShowsNothingOfTheHost()
    {
        var supplier = await PublicShowcaseTestData.SeedSupplierAsync(factory);
        var slug = await PublicShowcaseTestData.SeedServiceAsync(factory, supplier.OrgId);
        var hostTexts = await PublicShowcaseTestData.SeedTimedRequestAsync(
            factory, supplier.OrgId, new DateTime(2026, 10, 13, 8, 0, 0, DateTimeKind.Utc), new DateTime(2026, 10, 13, 10, 0, 0, DateTimeKind.Utc));
        // A request already cancelled does not hold its slot any more.
        await PublicShowcaseTestData.SeedTimedRequestAsync(
            factory, supplier.OrgId, new DateTime(2026, 10, 14, 8, 0, 0, DateTimeKind.Utc), new DateTime(2026, 10, 14, 10, 0, 0, DateTimeKind.Utc), ServiceRequestStatus.Annullato);
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/api/public/suppliers/{supplier.Slug}/slots?service={slug}&from=2026-10-13&days=2");

        var raw = await response.Content.ReadAsStringAsync();
        var days = JsonDocument.Parse(raw).RootElement.GetProperty("days").EnumerateArray().ToList();
        Assert.Equal(3, days[0].GetProperty("slots").GetArrayLength()); // Tuesday: the afternoon only
        Assert.Equal(6, days[1].GetProperty("slots").GetArrayLength()); // Wednesday: the cancelled request holds nothing
        foreach (var text in hostTexts)
            Assert.DoesNotContain(text, raw, StringComparison.OrdinalIgnoreCase);
    }

    [PostgresFact]
    public async Task ASupplierThatIsSuspendedAfterwards_AndAServiceThatIsPaused_AreGoneFromTheJoinedQueries()
    {
        var supplier = await PublicShowcaseTestData.SeedSupplierAsync(factory);
        var kept = await PublicShowcaseTestData.SeedServiceAsync(factory, supplier.OrgId, "Resta");
        var paused = await PublicShowcaseTestData.SeedServiceAsync(factory, supplier.OrgId, "Messo in pausa", SupplierServiceListingStatus.Paused);
        using var client = factory.CreateClient();

        var before = await client.GetFromJsonAsync<JsonElement>($"/api/public/suppliers/{supplier.Slug}/services");
        await SetSupplierStatusAsync(supplier.OrgId, SupplierStatus.Suspended);
        var after = await client.GetAsync($"/api/public/suppliers/{supplier.Slug}/services");
        await SetSupplierStatusAsync(supplier.OrgId, SupplierStatus.Active);
        var back = await client.GetFromJsonAsync<JsonElement>($"/api/public/suppliers/{supplier.Slug}/services");

        Assert.Equal(new[] { kept }, before.GetProperty("items").EnumerateArray().Select(s => s.GetProperty("slug").GetString()));
        Assert.Equal(HttpStatusCode.NotFound, after.StatusCode);
        Assert.Equal(new[] { kept }, back.GetProperty("items").EnumerateArray().Select(s => s.GetProperty("slug").GetString()));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/public/suppliers/{supplier.Slug}/services/{paused}")).StatusCode);
    }

    [PostgresFact]
    public async Task TheResponseTime_IsTheMedianOfTheLatestAnswers_OnTheRealOrderAndLimit()
    {
        var supplier = await PublicShowcaseTestData.SeedSupplierAsync(factory);
        var now = factory.Clock.GetUtcNow();
        foreach (var minutes in new[] { 10, 20, 30, 40, 50, 60, 70 })
            await PublicShowcaseTestData.SeedAnsweredRequestAsync(factory, supplier.OrgId, now, daysAgo: 4, minutes);
        using var client = factory.CreateClient();

        var body = await client.GetFromJsonAsync<JsonElement>($"/api/public/suppliers/{supplier.Slug}");

        Assert.Equal(40, body.GetProperty("medianResponseMinutes").GetInt32());
    }

    [PostgresFact]
    public async Task TheSupplementsOfAJsonbColumn_ArePricedAsTheSupplierWroteThem()
    {
        var supplier = await PublicShowcaseTestData.SeedSupplierAsync(factory);
        // jsonb keeps no key order and no whitespace: the supplements must read back the same.
        var slug = await PublicShowcaseTestData.SeedServiceAsync(
            factory,
            supplier.OrgId,
            supplementsJson: """[{ "per": "bathroom", "max": 2, "amountCents": 1500, "label": "Bagno in più", "code": "bagno" },{ "code": "mq", "per": "sqm30", "amountCents": 700, "label": "Oltre 60 m²" }]""");
        using var client = factory.CreateClient();

        var detail = await client.GetFromJsonAsync<JsonElement>($"/api/public/suppliers/{supplier.Slug}/services/{slug}");
        var quote = await (await client.PostAsJsonAsync($"/api/public/suppliers/{supplier.Slug}/quote", new
        {
            service = slug,
            surfaceSqm = 91,
            options = new[] { new { code = "bagno", quantity = 2 } },
        })).Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(new[] { "bagno", "mq" }, detail.GetProperty("supplements").EnumerateArray().Select(s => s.GetProperty("code").GetString()));
        Assert.Equal(6000 + 3000 + 1400, quote.GetProperty("totalCents").GetInt32()); // 2 bathrooms and 2 blocks of 30 m² above 60
    }

    [PostgresFact]
    public async Task ParallelReads_OfTheSamePlan_AllAnswerTheSame()
    {
        var supplier = await PublicShowcaseTestData.SeedSupplierAsync(factory);
        var slug = await PublicShowcaseTestData.SeedServiceAsync(factory, supplier.OrgId);
        await PublicShowcaseTestData.SeedBlockAsync(
            factory, supplier.OrgId, new DateTime(2026, 10, 15, 7, 0, 0, DateTimeKind.Utc), new DateTime(2026, 10, 15, 9, 0, 0, DateTimeKind.Utc));
        using var client = factory.CreateClient();
        var url = $"/api/public/suppliers/{supplier.Slug}/slots?service={slug}&days=14";

        var answers = await Task.WhenAll(Enumerable.Range(0, 20).Select(async _ =>
        {
            var response = await client.GetAsync(url);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return await response.Content.ReadAsStringAsync();
        }));

        Assert.Single(answers.Distinct());
    }

    private async Task SetSupplierStatusAsync(Guid orgId, SupplierStatus status)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var profile = await db.SupplierProfiles.SingleAsync(p => p.OrgId == orgId);
        profile.Status = status;
        await db.SaveChangesAsync();
    }
}
