using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Tests.Integration.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// SR-08a (part 2) over the real pipeline on PostgreSQL: what the host reads of a supplier to ask it for an intervention. The services
/// are the published ones of a supplier that is active and covers the comune of the property, with nothing of their public content; the
/// slots are the ones the creation of a request accepts (the same planner, the same query), so the host is never offered a time the
/// <c>POST</c> would refuse. The host is told what the creation tells it, and the other org's property is not found. The clock is the
/// host's, Monday 12 October 2026, 08:45 in Rome.
/// </summary>
public class HostSupplierCatalogPostgresTests : IClassFixture<PublicShowcaseFactory>
{
    private const string Host = "PropertyOwner";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly PublicShowcaseFactory _factory;

    public HostSupplierCatalogPostgresTests(PublicShowcaseFactory factory) => _factory = factory;

    [PostgresFact]
    public async Task Services_TheHostReadsThePublishedOnesOfASupplierThatCoversItsProperty_AndNothingOfTheirPublicContent()
    {
        var host = await SeedHostAsync();
        var supplier = await PublicShowcaseTestData.SeedSupplierAsync(_factory);
        var deep = await SeedServiceAsync(supplier.OrgId, "Pulizia profonda", sortOrder: 0);
        var onQuote = await SeedServiceAsync(supplier.OrgId, "Pulizia su preventivo", sortOrder: 1, priceFromCents: null, requiresQuote: true);
        await SeedServiceAsync(supplier.OrgId, "Pulizia in bozza", sortOrder: 2, status: SupplierServiceListingStatus.Draft);
        var hourly = await SeedServiceAsync(supplier.OrgId, "Stiro a ore", sortOrder: 3, priceFromCents: 2500, priceUnit: SupplierServicePriceUnit.PerHour);
        using var client = _factory.CreateAuthenticatedClient(host.OwnerId, Host);

        var response = await client.GetAsync($"/api/suppliers/{supplier.OrgId}/services?propertyId={host.PropertyId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        var items = JsonSerializer.Deserialize<JsonElement>(text, JsonOptions).GetProperty("items").EnumerateArray().ToList();
        // The published ones, in the order of the catalog: no draft.
        Assert.Equal([deep.Id, onQuote.Id, hourly.Id], items.Select(i => i.GetProperty("id").GetGuid()).ToList());

        // What the host needs to choose, and the price a request starts with: only a per-job service with a price has one.
        Assert.Equal(6000, items[0].GetProperty("priceFromCents").GetInt32());
        Assert.Equal(6000, items[0].GetProperty("estimatedAmountCents").GetInt32());
        Assert.Equal(120, items[0].GetProperty("durationMinutes").GetInt32());
        Assert.Equal(JsonValueKind.Null, items[1].GetProperty("estimatedAmountCents").ValueKind);
        Assert.True(items[1].GetProperty("requiresQuote").GetBoolean());
        Assert.Equal("PerHour", items[2].GetProperty("priceUnit").GetString());
        Assert.Equal(JsonValueKind.Null, items[2].GetProperty("estimatedAmountCents").ValueKind);

        // Nothing of the public page of the service, and nothing private of the supplier.
        Assert.DoesNotContain("Bagni e cucina", text, StringComparison.Ordinal);
        Assert.DoesNotContain("descrizione lunga", text, StringComparison.Ordinal);
        Assert.DoesNotContain(deep.Slug, text, StringComparison.Ordinal);
        Assert.All(supplier.Secrets, secret => Assert.DoesNotContain(secret, text, StringComparison.Ordinal));

        // A category narrows the list; what is not a code is refused as in the supplier picker.
        Assert.Equal(3, (await ItemsAsync(client, $"/api/suppliers/{supplier.OrgId}/services?propertyId={host.PropertyId}&category=cleaning")).Count);
        Assert.Empty(await ItemsAsync(client, $"/api/suppliers/{supplier.OrgId}/services?propertyId={host.PropertyId}&category=plumbing"));
        await AssertProblemAsync(
            await client.GetAsync($"/api/suppliers/{supplier.OrgId}/services?propertyId={host.PropertyId}&category=Pulizie"),
            HttpStatusCode.UnprocessableEntity,
            "invalid_service_category");
    }

    [PostgresFact]
    public async Task ServicesAndSlots_AreTold_WhatTheCreationOfARequestTellsTheHost_AndTheOtherOrgsPropertyIsNotFound()
    {
        var host = await SeedHostAsync();
        var active = await PublicShowcaseTestData.SeedSupplierAsync(_factory);
        var service = await SeedServiceAsync(active.OrgId, "Pulizia profonda");
        var draft = await SeedServiceAsync(active.OrgId, "Pulizia in bozza", sortOrder: 1, status: SupplierServiceListingStatus.Draft);
        var pending = await PublicShowcaseTestData.SeedSupplierAsync(_factory, status: SupplierStatus.Pending);
        var elsewhere = await PublicShowcaseTestData.SeedSupplierAsync(_factory, comuni: """["F205"]""");
        using var client = _factory.CreateAuthenticatedClient(host.OwnerId, Host);
        var query = $"propertyId={host.PropertyId}";

        // The supplier: unknown, not active, not covering the comune of the property.
        await AssertProblemAsync(await client.GetAsync($"/api/suppliers/{Guid.NewGuid()}/services?{query}"), HttpStatusCode.NotFound, "supplier_not_found");
        await AssertProblemAsync(await client.GetAsync($"/api/suppliers/{pending.OrgId}/services?{query}"), HttpStatusCode.UnprocessableEntity, "service_request_supplier_inactive");
        await AssertProblemAsync(await client.GetAsync($"/api/suppliers/{elsewhere.OrgId}/services?{query}"), HttpStatusCode.UnprocessableEntity, "service_request_supplier_outside_comune");
        await AssertProblemAsync(
            await client.GetAsync($"/api/suppliers/{elsewhere.OrgId}/services/{service.Id}/slots?{query}"),
            HttpStatusCode.UnprocessableEntity,
            "service_request_supplier_outside_comune");

        // The service of the slots: not the supplier's, and not published.
        await AssertProblemAsync(await client.GetAsync($"/api/suppliers/{active.OrgId}/services/{Guid.NewGuid()}/slots?{query}"), HttpStatusCode.NotFound, "supplier_service_not_found");
        await AssertProblemAsync(await client.GetAsync($"/api/suppliers/{active.OrgId}/services/{draft.Id}/slots?{query}"), HttpStatusCode.UnprocessableEntity, "service_request_service_unavailable");

        // The property: required, and the other org's is not found (as for every read of the host).
        await AssertProblemAsync(await client.GetAsync($"/api/suppliers/{active.OrgId}/services"), HttpStatusCode.BadRequest, "validation_error");
        var outsider = $"auth0|sr08a-outsider-{Guid.NewGuid():N}";
        await _factory.SeedOrgForOwnerAsync(outsider);
        using var other = _factory.CreateAuthenticatedClient(outsider, Host);
        await AssertProblemAsync(await other.GetAsync($"/api/suppliers/{active.OrgId}/services?{query}"), HttpStatusCode.NotFound, "property_not_found");
        await AssertProblemAsync(await other.GetAsync($"/api/suppliers/{active.OrgId}/services/{service.Id}/slots?{query}"), HttpStatusCode.NotFound, "property_not_found");
    }

    [PostgresFact]
    public async Task Slots_AreTheOnesTheCreationOfARequestAccepts_AndTheOneTakenIsNotOfferedAgain()
    {
        var host = await SeedHostAsync();
        var supplier = await PublicShowcaseTestData.SeedSupplierAsync(_factory);
        var service = await SeedServiceAsync(supplier.OrgId, "Pulizia profonda");
        using var client = _factory.CreateAuthenticatedClient(host.OwnerId, Host);
        var url = $"/api/suppliers/{supplier.OrgId}/services/{service.Id}/slots?propertyId={host.PropertyId}&date=2026-10-13";

        var slots = await SlotsAsync(client, url);

        Assert.Equal(120, slots.DurationMinutes);
        Assert.Equal("2026-10-13", slots.From);
        Assert.Equal("2026-10-26", slots.To);
        Assert.NotEmpty(slots.Starts);
        // Tuesday 13 October 09:00 in Rome (summer time): the first hour of a working day after the 24 hours of notice.
        Assert.Equal(new DateTime(2026, 10, 13, 7, 0, 0, DateTimeKind.Utc), slots.Starts[0]);
        // The window of 14 days, from Tuesday: working days only (Monday to Friday), the length of the service.
        Assert.All(slots.Starts, start =>
        {
            Assert.InRange(RomeCalendar.DateInRome(start), new DateOnly(2026, 10, 13), new DateOnly(2026, 10, 26));
            Assert.DoesNotContain(RomeCalendar.DateInRome(start).DayOfWeek, new[] { DayOfWeek.Saturday, DayOfWeek.Sunday });
        });
        Assert.All(slots.Lengths, length => Assert.Equal(TimeSpan.FromMinutes(120), length));

        // The first and the last slot offered are accepted by the creation; the first is then taken, and the same time is a 409.
        var first = slots.Starts[0];
        var last = slots.Starts[^1];
        Assert.Equal(HttpStatusCode.Created, (await CreateAsync(client, host, supplier.OrgId, service.Id, first)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await CreateAsync(client, host, supplier.OrgId, service.Id, last)).StatusCode);
        await AssertProblemAsync(
            await CreateAsync(client, host, supplier.OrgId, service.Id, first),
            HttpStatusCode.Conflict,
            "supplier_slot_unavailable");

        var after = await SlotsAsync(client, url);
        Assert.DoesNotContain(first, after.Starts);
        Assert.DoesNotContain(last, after.Starts);
        Assert.True(after.Starts.Count < slots.Starts.Count);

        // A time the supplier does not offer (half past nine) is refused by the creation as well.
        var notOffered = new DateTime(2026, 10, 14, 7, 30, 0, DateTimeKind.Utc);
        Assert.DoesNotContain(notOffered, slots.Starts);
        await AssertProblemAsync(
            await CreateAsync(client, host, supplier.OrgId, service.Id, notOffered),
            HttpStatusCode.Conflict,
            "supplier_slot_unavailable");

        // A first day past the horizon of the supplier is an empty window, not an error.
        var far = await ReadJsonAsync(await client.GetAsync(
            $"/api/suppliers/{supplier.OrgId}/services/{service.Id}/slots?propertyId={host.PropertyId}&date=2027-12-01"));
        Assert.Equal(JsonValueKind.Null, far.GetProperty("from").ValueKind);
        Assert.Equal(0, far.GetProperty("slots").GetArrayLength());
    }

    // ─── helpers ───

    private sealed record HostWorld(string OwnerId, Guid PropertyId);

    private sealed record ServiceRow(Guid Id, string Slug);

    private sealed record Slots(int? DurationMinutes, string? From, string? To, IReadOnlyList<DateTime> Starts, IReadOnlyList<TimeSpan> Lengths);

    private static Task<HttpResponseMessage> CreateAsync(HttpClient client, HostWorld host, Guid supplierOrgId, Guid serviceId, DateTime startUtc) =>
        client.PostAsJsonAsync("/api/service-requests", new
        {
            propertyId = host.PropertyId,
            supplierOrgId,
            category = "cleaning",
            serviceListingId = serviceId,
            scheduledStartUtc = startUtc,
        });

    private static async Task<Slots> SlotsAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"GET {url}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        var body = await ReadJsonAsync(response);
        var slots = body.GetProperty("slots").EnumerateArray().ToList();
        var starts = slots.Select(s => s.GetProperty("startUtc").GetDateTime().ToUniversalTime()).ToList();
        var lengths = slots.Select(s => s.GetProperty("endUtc").GetDateTime() - s.GetProperty("startUtc").GetDateTime()).ToList();
        return new Slots(
            body.GetProperty("durationMinutes").ValueKind == JsonValueKind.Null ? null : body.GetProperty("durationMinutes").GetInt32(),
            body.GetProperty("from").GetString(),
            body.GetProperty("to").GetString(),
            starts,
            lengths);
    }

    private static async Task<List<JsonElement>> ItemsAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"GET {url}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return (await ReadJsonAsync(response)).GetProperty("items").EnumerateArray().ToList();
    }

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == status, $"Expected {(int)status}, got {(int)response.StatusCode}: {text}");
        Assert.Equal(code, JsonSerializer.Deserialize<JsonElement>(text, JsonOptions).GetProperty("code").GetString());
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response) =>
        JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync(), JsonOptions);

    private async Task<ServiceRow> SeedServiceAsync(
        Guid supplierOrgId,
        string name,
        int sortOrder = 0,
        SupplierServiceListingStatus status = SupplierServiceListingStatus.Active,
        int? priceFromCents = 6000,
        SupplierServicePriceUnit priceUnit = SupplierServicePriceUnit.PerJob,
        bool requiresQuote = false)
    {
        var slug = await PublicShowcaseTestData.SeedServiceAsync(
            _factory,
            supplierOrgId,
            name,
            status,
            priceFromCents,
            priceUnit,
            requiresQuote,
            sortOrder: sortOrder);
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var id = await db.SupplierServiceListings.IgnoreQueryFilters().Where(l => l.OrgId == supplierOrgId && l.Slug == slug).Select(l => l.Id).SingleAsync();
        return new ServiceRow(id, slug);
    }

    /// <summary>A host org with one property in comune H501 (where the supplier of the showcase tests operates), owned by one user.</summary>
    private async Task<HostWorld> SeedHostAsync()
    {
        var ownerId = $"auth0|sr08a-catalog-{Guid.NewGuid():N}";
        var org = await _factory.SeedOrgForOwnerAsync(ownerId);

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var property = new Property
        {
            OwnerId = ownerId,
            OrgId = org.Id,
            Name = "Casa Catalogo",
            Address = $"Via SR08a {Guid.NewGuid():N}",
            City = "H501",
            PostalCode = "00100",
            Bedrooms = 2,
            Bathrooms = 1,
            MaxGuests = 4,
            NightlyRate = 100m,
            CinCode = "IT058091C27G5FFZDZ",
            IsActive = true,
        };
        db.Properties.Add(property);
        await db.SaveChangesAsync();
        return new HostWorld(ownerId, property.Id);
    }
}
