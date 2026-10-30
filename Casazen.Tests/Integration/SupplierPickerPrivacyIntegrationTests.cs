using System.Net;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Suppliers;
using Casazen.Infrastructure.Data;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
// OrgEntity is a global alias defined in Casazen.Tests.csproj: OrgEntity = global::Casazen.Core.Entities.Org

namespace Casazen.Tests.Integration;

/// <summary>
/// SR-09a: the supplier picker of the marketplace (<c>GET /api/suppliers</c>) lists only <c>Active</c> suppliers and sends a host
/// nothing private about them. The answer is checked against a whitelist of fields (so a field added later by mistake fails here),
/// and neither the VAT number nor the Stripe account of a supplier appears in it: only the <c>verified</c> boolean does.
/// </summary>
public class SupplierPickerPrivacyIntegrationTests(CasazenWebApplicationFactory factory) : IClassFixture<CasazenWebApplicationFactory>
{
    private const string VatNumber = "IT01234567890";
    private const string StripeAccount = "acct_1PrivateConnectAccount";

    private static readonly string[] PickerFields =
    [
        "orgId", "legalName", "phone", "email", "categories", "comuni", "bio", "photoUrls",
        "services", "priceFromCents", "priceFromUnit", "medianResponseMinutes", "verified", "showcaseSlug",
    ];

    private static readonly string[] ServiceFields =
    [
        "id", "name", "category", "durationMinutes", "priceFromCents", "priceUnit", "requiresQuote",
    ];

    [Fact]
    public async Task GetSuppliers_ListsOnlyActiveSuppliers_AndSendsNoFiscalOrPrivateData()
    {
        var comune = $"Pick{Guid.NewGuid():N}"[..12];
        var verified = await SeedSupplierAsync(comune, SupplierStatus.Active, VatNumber);
        var withoutVat = await SeedSupplierAsync(comune, SupplierStatus.Active, vatNumber: null);
        var pending = await SeedSupplierAsync(comune, SupplierStatus.Pending, VatNumber);
        var suspended = await SeedSupplierAsync(comune, SupplierStatus.Suspended, VatNumber);

        await SeedServiceAsync(verified, "pulizia-a-ore", "Pulizia a ore", SupplierServiceListingStatus.Active, 2500, SupplierServicePriceUnit.PerHour);
        await SeedServiceAsync(verified, "pulizia-profonda", "Pulizia profonda", SupplierServiceListingStatus.Active, 9000, SupplierServicePriceUnit.PerJob);
        await SeedServiceAsync(verified, "sanificazione", "Sanificazione", SupplierServiceListingStatus.Active, 8000, SupplierServicePriceUnit.PerJob);
        await SeedServiceAsync(verified, "lavaggio-vetri", "Lavaggio vetri", SupplierServiceListingStatus.Active, 3000, SupplierServicePriceUnit.PerJob);
        await SeedServiceAsync(verified, "bozza", "Bozza", SupplierServiceListingStatus.Draft, 100, SupplierServicePriceUnit.PerJob);
        await SeedServiceAsync(verified, "eliminata", "Eliminata", SupplierServiceListingStatus.Active, 100, SupplierServicePriceUnit.PerJob, deleted: true);
        await SeedServiceAsync(pending, "del-pending", "Del pending", SupplierServiceListingStatus.Active, 100, SupplierServicePriceUnit.PerJob);

        await factory.SeedOrgForOwnerAsync();
        using var client = factory.CreateAuthenticatedClient(roles: "PropertyOwner");

        var response = await client.GetAsync($"/api/suppliers?comune={comune}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var raw = await response.Content.ReadAsStringAsync();
        var items = JsonDocument.Parse(raw).RootElement.GetProperty("items").EnumerateArray().ToList();

        // Only the Active suppliers of the comune.
        var listed = items.Select(item => item.GetProperty("orgId").GetGuid()).Order().ToList();
        Assert.Equal(new[] { verified.OrgId, withoutVat.OrgId }.Order().ToList(), listed);
        Assert.DoesNotContain(pending.OrgId, listed);
        Assert.DoesNotContain(suspended.OrgId, listed);

        // Nothing fiscal or private: not the VAT number, not the Stripe account, and no field outside the whitelist.
        Assert.DoesNotContain(VatNumber, raw);
        Assert.DoesNotContain(StripeAccount, raw);
        foreach (var item in items)
        {
            Assert.Equal(PickerFields.Order(), item.EnumerateObject().Select(p => p.Name).Order());
            foreach (var service in item.GetProperty("services").EnumerateArray())
                Assert.Equal(ServiceFields.Order(), service.EnumerateObject().Select(p => p.Name).Order());
        }

        // What the host does get: the badge as a boolean, the published services only (the first three: it has four), the lowest
        // "from" price of all of them.
        var withVatItem = items.Single(item => item.GetProperty("orgId").GetGuid() == verified.OrgId);
        Assert.True(withVatItem.GetProperty("verified").GetBoolean());
        Assert.Equal(verified.Slug, withVatItem.GetProperty("showcaseSlug").GetString());
        Assert.Equal(
            new[] { "Pulizia a ore", "Pulizia profonda", "Sanificazione" },
            withVatItem.GetProperty("services").EnumerateArray().Select(s => s.GetProperty("name").GetString()).ToArray());
        Assert.Equal(2500, withVatItem.GetProperty("priceFromCents").GetInt32());
        Assert.Equal("PerHour", withVatItem.GetProperty("priceFromUnit").GetString());

        var withoutVatItem = items.Single(item => item.GetProperty("orgId").GetGuid() == withoutVat.OrgId);
        Assert.False(withoutVatItem.GetProperty("verified").GetBoolean());
        Assert.Equal(JsonValueKind.Null, withoutVatItem.GetProperty("medianResponseMinutes").ValueKind);

        // The search (MKT) looks at the name of every published service, even the ones beyond the three listed, and at the name of
        // the category in Italian and in English, whatever the case and the accents; a draft or a deleted service is not searchable.
        Assert.Equal(new[] { verified.OrgId }, await SearchAsync(client, comune, "vetri"));
        Assert.Equal(new[] { verified.OrgId }, await SearchAsync(client, comune, "SANIFICAZIONE"));
        Assert.Equal(new[] { verified.OrgId, withoutVat.OrgId }.Order(), await SearchAsync(client, comune, "pulizìe"));
        Assert.Equal(new[] { verified.OrgId, withoutVat.OrgId }.Order(), await SearchAsync(client, comune, "Cleaning"));
        Assert.Empty(await SearchAsync(client, comune, "bozza"));
        Assert.Empty(await SearchAsync(client, comune, "eliminata"));
        Assert.Empty(await SearchAsync(client, comune, "del pending"));
    }

    [Fact]
    public async Task GetSupplier_ReturnsTheSameFieldsWithEveryService_AndOnlyForAnActiveSupplierOfTheComune()
    {
        var comune = $"Pick{Guid.NewGuid():N}"[..12];
        var active = await SeedSupplierAsync(comune, SupplierStatus.Active, VatNumber);
        var pending = await SeedSupplierAsync(comune, SupplierStatus.Pending, VatNumber);
        var elsewhere = await SeedSupplierAsync($"Elsewhere{Guid.NewGuid():N}"[..14], SupplierStatus.Active, VatNumber);
        foreach (var (slug, name) in new[] { ("uno", "Uno"), ("due", "Due"), ("tre", "Tre"), ("quattro", "Quattro") })
            await SeedServiceAsync(active, slug, name, SupplierServiceListingStatus.Active, 1000, SupplierServicePriceUnit.PerJob);
        await SeedServiceAsync(active, "bozza", "Bozza", SupplierServiceListingStatus.Draft, 100, SupplierServicePriceUnit.PerJob);

        await factory.SeedOrgForOwnerAsync();
        using var client = factory.CreateAuthenticatedClient(roles: "PropertyOwner");

        // The page of one supplier lists every published service (the list of the marketplace carries three), under the same fields.
        var response = await client.GetAsync($"/api/suppliers/{active.OrgId}?comune={comune}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var raw = await response.Content.ReadAsStringAsync();
        var item = JsonDocument.Parse(raw).RootElement;
        Assert.Equal(PickerFields.Order(), item.EnumerateObject().Select(p => p.Name).Order());
        Assert.DoesNotContain(VatNumber, raw);
        Assert.DoesNotContain(StripeAccount, raw);
        var services = item.GetProperty("services").EnumerateArray().ToList();
        Assert.Equal(4, services.Count);
        Assert.All(services, service => Assert.Equal(ServiceFields.Order(), service.EnumerateObject().Select(p => p.Name).Order()));
        Assert.True(item.GetProperty("verified").GetBoolean());

        // Never reachable by id alone: not active, not working in that comune, no target at all, a property that is not the host's.
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/suppliers/{pending.OrgId}?comune={comune}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/suppliers/{elsewhere.OrgId}?comune={comune}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/suppliers/{Guid.NewGuid()}?comune={comune}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"/api/suppliers/{active.OrgId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/suppliers/{active.OrgId}?propertyId={Guid.NewGuid()}")).StatusCode);
    }

    private static async Task<Guid[]> SearchAsync(HttpClient client, string comune, string q)
    {
        var response = await client.GetAsync($"/api/suppliers?comune={comune}&q={Uri.EscapeDataString(q)}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var items = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("items");
        return items.EnumerateArray().Select(item => item.GetProperty("orgId").GetGuid()).Order().ToArray();
    }

    private async Task<(Guid OrgId, string Slug)> SeedSupplierAsync(string comune, SupplierStatus status, string? vatNumber)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var key = Guid.NewGuid().ToString("N");

        var org = new OrgEntity
        {
            Name = $"Fornitore {key[..8]} Srl",
            Slug = $"picker-{key}"[..30],
            DisplayName = $"Fornitore {key[..8]} Srl",
            ContactEmail = $"{key}@test.com",
            OrgType = OrgType.Supplier,
            PlanTier = PlanTier.Starter,
            StripeConnectedAccountId = StripeAccount,
            ConnectChargesEnabled = true,
            ConnectPayoutsEnabled = true,
        };
        db.Orgs.Add(org);

        var slug = $"fornitore-{key[..8]}";
        db.SupplierProfiles.Add(new SupplierProfile
        {
            OrgId = org.Id,
            Status = status,
            Email = org.ContactEmail,
            LegalName = $"Fornitore {key[..8]} Srl",
            VatNumber = vatNumber,
            Phone = "+39 06 999999",
            CategoriesJson = "[\"cleaning\"]",
            ComuniJson = $"[\"{comune}\"]",
            Bio = "Azienda di pulizie professionale.",
            ShowcaseSlug = vatNumber is null ? null : slug,
        });

        await db.SaveChangesAsync();
        return (org.Id, slug);
    }

    private async Task SeedServiceAsync(
        (Guid OrgId, string Slug) supplier,
        string slug,
        string name,
        SupplierServiceListingStatus status,
        int priceFromCents,
        SupplierServicePriceUnit unit,
        bool deleted = false)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        db.SupplierServiceListings.Add(new SupplierServiceListing
        {
            OrgId = supplier.OrgId,
            Slug = slug,
            Name = name,
            Category = ServiceCategories.Cleaning,
            PriceFromCents = priceFromCents,
            PriceUnit = unit,
            DurationMinutes = 120,
            Status = status,
            DeletedAt = deleted ? DateTime.UtcNow : null,
        });

        await db.SaveChangesAsync();
    }
}
