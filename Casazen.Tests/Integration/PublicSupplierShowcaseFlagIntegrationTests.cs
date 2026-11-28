using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Casazen.Tests.Integration;

[Collection(SupplierCatalogHostsCollection.Name)]
public class PublicSupplierShowcaseFlagOffIntegrationTests(CasazenWebApplicationFactory factory) : IClassFixture<CasazenWebApplicationFactory>
{
    [Fact]
    public async Task TheFlag_IsOffByDefault_AndTheFrontendIsToldSo()
    {
        using var client = factory.CreateClient();

        var features = await client.GetFromJsonAsync<JsonElement>("/api/public/features");

        Assert.False(features.GetProperty("supplierShowcaseBooking").GetBoolean());
    }

    [Fact]
    public async Task TheNewEndpoints_AnswerTheSame404AsARouteThatDoesNotExist_EvenForARealSupplierWithRealServices()
    {
        var supplier = await PublicShowcaseTestData.SeedSupplierAsync(factory);
        var slug = await PublicShowcaseTestData.SeedServiceAsync(factory, supplier.OrgId);
        using var client = factory.CreateClient();

        var missing = await client.GetAsync("/api/public/suppliers/non-esiste/non-esiste/non-esiste/non-esiste");
        var responses = new[]
        {
            await client.GetAsync($"/api/public/suppliers/{supplier.Slug}/services"),
            await client.GetAsync($"/api/public/suppliers/{supplier.Slug}/services/{slug}"),
            await client.GetAsync($"/api/public/suppliers/{supplier.Slug}/slots?service={slug}"),
            await client.PostAsJsonAsync($"/api/public/suppliers/{supplier.Slug}/quote", new { service = slug }),
            await client.GetAsync($"/api/public/suppliers/{supplier.Slug}/slots"),
            await client.PostAsync($"/api/public/suppliers/{supplier.Slug}/quote", new StringContent("{ not json", Encoding.UTF8, "application/json")),
        };

        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        var missingDetail = (await missing.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString();
        foreach (var response in responses)
        {
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("not_found", body.GetProperty("code").GetString());
            Assert.Equal(missingDetail, body.GetProperty("detail").GetString());
        }
    }

    [Fact]
    public async Task ThePage_WithTheFlagOff_IsExactlyThePageOfBeforeSp09_EvenForASupplierWithServicesAndAnsweredRequests()
    {
        var supplier = await PublicShowcaseTestData.SeedSupplierAsync(factory);
        await PublicShowcaseTestData.SeedServiceAsync(factory, supplier.OrgId);
        foreach (var minutes in new[] { 5, 10, 15, 20, 25, 30 })
            await PublicShowcaseTestData.SeedAnsweredRequestAsync(factory, supplier.OrgId, DateTimeOffset.UtcNow, daysAgo: 2, minutes);
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/api/public/suppliers/{supplier.Slug}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("noindex", response.Headers.GetValues("X-Robots-Tag").Single());
        var raw = await response.Content.ReadAsStringAsync();
        var members = JsonDocument.Parse(raw).RootElement.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "availability", "bio", "categories", "comuni", "legalName", "photoUrls", "slug" }, members);
        // The privacy the page always had: never the phone, the e-mail, the VAT number or the status.
        foreach (var secret in supplier.Secrets)
            Assert.DoesNotContain(secret, raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("phone", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("email", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("status", raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ThePage_OfAPendingOrSuspendedSupplier_IsStillThe404OfBefore()
    {
        var pending = await PublicShowcaseTestData.SeedSupplierAsync(factory, SupplierStatus.Pending);
        var suspended = await PublicShowcaseTestData.SeedSupplierAsync(factory, SupplierStatus.Suspended);
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/public/suppliers/{pending.Slug}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/public/suppliers/{suspended.Slug}")).StatusCode);
    }
}

public sealed class PublicShowcaseWithHoldsFactory : PublicShowcaseFactory
{
    public Dictionary<Guid, List<SupplierOccupancy>> Holds { get; } = [];

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            RemoveService<ISupplierAgendaService>(services);
            services.AddScoped<ISupplierAgendaService>(provider =>
                new HoldInjectingAgenda(ActivatorUtilities.CreateInstance<SupplierAgendaService>(provider), Holds));
        });
    }

    private sealed class HoldInjectingAgenda(ISupplierAgendaService inner, Dictionary<Guid, List<SupplierOccupancy>> holds) : ISupplierAgendaService
    {
        public async Task<IReadOnlyList<SupplierDayPlan>> PlanAsync(
            Guid supplierOrgId, DateOnly from, DateOnly to, SupplierSlotQuery query, CancellationToken cancellationToken = default)
        {
            var input = await inner.BuildPlanningInputAsync(supplierOrgId, from, to, cancellationToken);
            var extra = holds.TryGetValue(supplierOrgId, out var mine) ? mine : [];
            return SupplierSlotPlanner.PlanRange(from, to, input with { Occupancies = [.. input.Occupancies, .. extra] }, query);
        }

        public Task<IReadOnlyList<SupplierDayPlan>> PlanAsync(
            Guid supplierOrgId, DateOnly from, DateOnly to, SupplierSlotQuery query, Guid? exceptRequestId, CancellationToken cancellationToken = default) =>
            inner.PlanAsync(supplierOrgId, from, to, query, exceptRequestId, cancellationToken);

        public Task<SupplierPlanningRules> GetRulesAsync(Guid supplierOrgId, CancellationToken cancellationToken = default) =>
            inner.GetRulesAsync(supplierOrgId, cancellationToken);

        public Task<SupplierBookingSettings> GetBookingSettingsAsync(Guid supplierOrgId, CancellationToken cancellationToken = default) =>
            inner.GetBookingSettingsAsync(supplierOrgId, cancellationToken);

        public Task<SupplierSettingsView> GetSettingsAsync(Guid supplierOrgId, CancellationToken cancellationToken = default) =>
            inner.GetSettingsAsync(supplierOrgId, cancellationToken);

        public Task<SupplierSettingsView> ReplaceSettingsAsync(Guid supplierOrgId, SupplierSettingsInput input, CancellationToken cancellationToken = default) =>
            inner.ReplaceSettingsAsync(supplierOrgId, input, cancellationToken);

        public Task SetOnlineBookingEnabledAsync(Guid supplierOrgId, bool enabled, CancellationToken cancellationToken = default) =>
            inner.SetOnlineBookingEnabledAsync(supplierOrgId, enabled, cancellationToken);

        public Task<SupplierUrgentSettings> GetUrgentAsync(Guid supplierOrgId, CancellationToken cancellationToken = default) =>
            inner.GetUrgentAsync(supplierOrgId, cancellationToken);

        public Task<SupplierUrgentSettings> ReplaceUrgentAsync(Guid supplierOrgId, SupplierUrgentInput input, CancellationToken cancellationToken = default) =>
            inner.ReplaceUrgentAsync(supplierOrgId, input, cancellationToken);

        public Task<SupplierPlanningInput> BuildPlanningInputAsync(Guid supplierOrgId, DateOnly from, DateOnly to, CancellationToken cancellationToken = default) =>
            inner.BuildPlanningInputAsync(supplierOrgId, from, to, cancellationToken);

        public Task<SupplierPlanningInput> BuildPlanningInputAsync(
            Guid supplierOrgId, DateOnly from, DateOnly to, Guid? exceptRequestId, CancellationToken cancellationToken = default) =>
            inner.BuildPlanningInputAsync(supplierOrgId, from, to, exceptRequestId, cancellationToken);

        public Task<SupplierHours> GetHoursAsync(Guid supplierOrgId, CancellationToken cancellationToken = default) =>
            inner.GetHoursAsync(supplierOrgId, cancellationToken);

        public Task<SupplierHours> ReplaceHoursAsync(Guid supplierOrgId, SupplierHoursInput input, CancellationToken cancellationToken = default) =>
            inner.ReplaceHoursAsync(supplierOrgId, input, cancellationToken);

        public Task<IReadOnlyList<SupplierTimeOff>> ListTimeOffAsync(Guid supplierOrgId, CancellationToken cancellationToken = default) =>
            inner.ListTimeOffAsync(supplierOrgId, cancellationToken);

        public Task<SupplierTimeOff> AddTimeOffAsync(Guid supplierOrgId, SupplierTimeOffInput input, CancellationToken cancellationToken = default) =>
            inner.AddTimeOffAsync(supplierOrgId, input, cancellationToken);

        public Task DeleteTimeOffAsync(Guid supplierOrgId, Guid id, CancellationToken cancellationToken = default) =>
            inner.DeleteTimeOffAsync(supplierOrgId, id, cancellationToken);

        public Task<IReadOnlyList<SupplierBusyWindow>> ListBlocksAsync(Guid supplierOrgId, CancellationToken cancellationToken = default) =>
            inner.ListBlocksAsync(supplierOrgId, cancellationToken);

        public Task<SupplierBusyWindow> AddBlockAsync(Guid supplierOrgId, SupplierBlockInput input, CancellationToken cancellationToken = default) =>
            inner.AddBlockAsync(supplierOrgId, input, cancellationToken);

        public Task DeleteBlockAsync(Guid supplierOrgId, Guid id, CancellationToken cancellationToken = default) =>
            inner.DeleteBlockAsync(supplierOrgId, id, cancellationToken);

        public Task<SupplierPlanningRules> ReplaceRulesAsync(Guid supplierOrgId, SupplierRulesInput input, CancellationToken cancellationToken = default) =>
            inner.ReplaceRulesAsync(supplierOrgId, input, cancellationToken);

        public Task<SupplierCalendar> GetCalendarAsync(Guid supplierOrgId, DateOnly from, DateOnly to, CancellationToken cancellationToken = default) =>
            inner.GetCalendarAsync(supplierOrgId, from, to, cancellationToken);
    }
}

[Collection(SupplierCatalogHostsCollection.Name)]
public class PublicSupplierShowcaseHoldsIntegrationTests(PublicShowcaseWithHoldsFactory factory) : IClassFixture<PublicShowcaseWithHoldsFactory>
{
    [Fact]
    public async Task Slots_AHoldOfAnotherCustomer_TakesItsSlots_UntilItExpires()
    {
        var supplier = await PublicShowcaseTestData.SeedSupplierAsync(factory);
        var slug = await PublicShowcaseTestData.SeedServiceAsync(factory, supplier.OrgId);
        var start = new DateTime(2026, 10, 14, 8, 0, 0, DateTimeKind.Utc);
        factory.Holds[supplier.OrgId] = [SupplierOccupancy.Hold(start, start.AddHours(2), PublicShowcaseFactory.Start.UtcDateTime.AddMinutes(30))];
        using var client = factory.CreateClient();

        var body = await client.GetFromJsonAsync<JsonElement>($"/api/public/suppliers/{supplier.Slug}/slots?service={slug}&from=2026-10-14&days=1");

        var wednesday = body.GetProperty("days")[0].GetProperty("slots").EnumerateArray().Select(s => s.GetProperty("startUtc").GetString()).ToArray();
        Assert.Equal(new[] { "2026-10-14T12:00:00Z", "2026-10-14T13:00:00Z", "2026-10-14T14:00:00Z" }, wednesday);
    }

    [Fact]
    public async Task Slots_AnExpiredHold_TakesNothing()
    {
        var supplier = await PublicShowcaseTestData.SeedSupplierAsync(factory);
        var slug = await PublicShowcaseTestData.SeedServiceAsync(factory, supplier.OrgId);
        var start = new DateTime(2026, 10, 14, 8, 0, 0, DateTimeKind.Utc);
        factory.Holds[supplier.OrgId] = [SupplierOccupancy.Hold(start, start.AddHours(2), PublicShowcaseFactory.Start.UtcDateTime.AddMinutes(-1))];
        using var client = factory.CreateClient();

        var body = await client.GetFromJsonAsync<JsonElement>($"/api/public/suppliers/{supplier.Slug}/slots?service={slug}&from=2026-10-14&days=1");

        Assert.Equal(6, body.GetProperty("days")[0].GetProperty("slots").GetArrayLength());
    }

    [Fact]
    public async Task ASupplierThatStopsBeingActive_IsGoneAtOnce_EvenWhileItsSlotsAreCached()
    {
        var supplier = await PublicShowcaseTestData.SeedSupplierAsync(factory);
        var slug = await PublicShowcaseTestData.SeedServiceAsync(factory, supplier.OrgId);
        using var client = factory.CreateClient();
        var url = $"/api/public/suppliers/{supplier.Slug}/slots?service={slug}";
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(url)).StatusCode);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var profile = await db.SupplierProfiles.SingleAsync(p => p.OrgId == supplier.OrgId);
            profile.Status = SupplierStatus.Suspended;
            await db.SaveChangesAsync();
        }

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/public/suppliers/{supplier.Slug}/services")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/public/suppliers/{supplier.Slug}")).StatusCode);
    }

    [Fact]
    public async Task AServiceThatIsPaused_IsGoneAtOnce_EvenWhileItsSlotsAreCached()
    {
        var supplier = await PublicShowcaseTestData.SeedSupplierAsync(factory);
        var slug = await PublicShowcaseTestData.SeedServiceAsync(factory, supplier.OrgId);
        using var client = factory.CreateClient();
        var url = $"/api/public/suppliers/{supplier.Slug}/slots?service={slug}";
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(url)).StatusCode);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var listing = await db.SupplierServiceListings.SingleAsync(l => l.OrgId == supplier.OrgId && l.Slug == slug);
            listing.Status = SupplierServiceListingStatus.Paused;
            await db.SaveChangesAsync();
        }

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(url)).StatusCode);
    }
}
