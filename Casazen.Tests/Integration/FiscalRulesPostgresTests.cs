using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Infrastructure.Data;
using Casazen.Tests.Integration.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// CO-18 (A5-22) on real PostgreSQL, through the API: short-rental threshold per taxpayer (not per org) on the apartments
/// with short-term stays in the tax year, one 21% cedolare unit per taxpayer (several per org: the old unique index per org
/// is gone), IRPEF ordinaria selectable, and OTA withholding only outside the business (impresa) regime.
/// </summary>
public class FiscalRulesPostgresTests : IClassFixture<CasazenWebApplicationFactory>
{
    private const int TaxYear = 2026;
    private static readonly string[] OwnerCodes = ["RSSMRA80A01H501U", "VRDLGU75B12F205X", "BNCGNN90C41L219K"];

    private readonly CasazenWebApplicationFactory _factory;

    public FiscalRulesPostgresTests(CasazenWebApplicationFactory factory) => _factory = factory;

    [PostgresFact]
    public async Task AssignRegime_PropertyManagerWithThreeOwnersOneApartmentEach_NoPartitaIvaAndCedolare21ForEach()
    {
        // One org (a property manager's) with the apartments of three different owners: the caller is the org's host.
        var manager = $"auth0|co18-pm-{Guid.NewGuid():N}";
        using var client = _factory.CreateAuthenticatedClient(manager, "PropertyOwner");
        var properties = new List<Property>();
        for (var i = 0; i < OwnerCodes.Length; i++)
        {
            var property = await _factory.SeedPropertyAsync(manager);
            await SeedStayAsync(property, new DateTime(TaxYear, 6, i + 1, 0, 0, 0, DateTimeKind.Utc), nights: 3);
            var taxpayer = await client.PutAsJsonAsync(
                $"/api/fiscal/properties/{property.Id}/taxpayer", new { fiscalCode = OwnerCodes[i].ToLowerInvariant() });
            Assert.Equal(HttpStatusCode.OK, taxpayer.StatusCode);
            var masked = (await taxpayer.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("fiscalCodeMasked").GetString();
            Assert.Equal("************" + OwnerCodes[i][^4..], masked);
            properties.Add(property);
        }

        var snapshot = await GetRegimeAsync(client);

        Assert.Equal(3, snapshot.GetProperty("strPropertyCount").GetInt32());
        Assert.False(snapshot.GetProperty("requiresPartitaIva").GetBoolean());
        var taxpayers = snapshot.GetProperty("taxpayers").EnumerateArray().ToList();
        Assert.Equal(3, taxpayers.Count);
        Assert.All(taxpayers, t =>
        {
            Assert.Equal(1, t.GetProperty("shortStayApartmentCount").GetInt32());
            Assert.False(t.GetProperty("thresholdExceeded").GetBoolean());
        });
        Assert.DoesNotContain(OwnerCodes[0], snapshot.GetRawText(), StringComparison.Ordinal);

        foreach (var property in properties)
        {
            var assigned = await AssignAsync(client, property.Id, "CedolareSecca21");
            Assert.Equal(HttpStatusCode.OK, assigned.StatusCode);
            var row = await assigned.Content.ReadFromJsonAsync<JsonElement>();
            Assert.True(row.GetProperty("isPrimaryForCedolare").GetBoolean());
            Assert.Equal(0.21m, row.GetProperty("cedolareRate").GetDecimal());
        }

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var ids = properties.Select(p => p.Id).ToList();
        Assert.Equal(3, await db.PropertyFiscalYears.CountAsync(y => ids.Contains(y.PropertyId) && y.IsPrimaryForCedolare));
    }

    [PostgresFact]
    public async Task AssignRegime_SameTaxpayerThreeApartments_Returns409AndOnlyImpresaIsAllowed()
    {
        var host = $"auth0|co18-host-{Guid.NewGuid():N}";
        using var client = _factory.CreateAuthenticatedClient(host, "PropertyOwner");
        var properties = new List<Property>();
        for (var i = 0; i < 3; i++)
        {
            var property = await _factory.SeedPropertyAsync(host);
            await SeedStayAsync(property, new DateTime(TaxYear, 7, i + 1, 0, 0, 0, DateTimeKind.Utc), nights: 2);
            properties.Add(property);
        }

        var snapshot = await GetRegimeAsync(client);
        Assert.True(snapshot.GetProperty("requiresPartitaIva").GetBoolean());
        var taxpayer = Assert.Single(snapshot.GetProperty("taxpayers").EnumerateArray().ToList());
        Assert.True(taxpayer.GetProperty("isOrgTaxProfile").GetBoolean());
        Assert.True(taxpayer.GetProperty("thresholdExceeded").GetBoolean());
        Assert.Equal(2, snapshot.GetProperty("maxShortStayApartmentsPerTaxpayer").GetInt32());

        foreach (var regime in new[] { "CedolareSecca21", "CedolareSecca26", "IrpefOrdinaria" })
        {
            var refused = await AssignAsync(client, properties[0].Id, regime);
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            var problem = await refused.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("fiscal_short_stay_threshold_exceeded", problem.GetProperty("code").GetString());
        }

        var profile = await client.PutAsJsonAsync("/api/fiscal/tax-profile", new { hasPartitaIva = true, partitaIvaNumber = "12345678901" });
        Assert.Equal(HttpStatusCode.OK, profile.StatusCode);
        var impresa = await AssignAsync(client, properties[0].Id, "RegimeForfettario");
        Assert.Equal(HttpStatusCode.OK, impresa.StatusCode);
    }

    [PostgresFact]
    public async Task AssignRegime_IrpefOrdinariaWithoutPartitaIva_IsSelectableAndFlaggedAsNotComputed()
    {
        var host = $"auth0|co18-irpef-{Guid.NewGuid():N}";
        using var client = _factory.CreateAuthenticatedClient(host, "PropertyOwner");
        var property = await _factory.SeedPropertyAsync(host);
        await SeedStayAsync(property, new DateTime(TaxYear, 9, 1, 0, 0, 0, DateTimeKind.Utc), nights: 5);

        var response = await AssignAsync(client, property.Id, "IrpefOrdinaria");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var row = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("IrpefOrdinaria", row.GetProperty("assignedRegime").GetString());
        Assert.Equal("irpef_ordinaria_not_computed", row.GetProperty("taxNote").GetString());
        Assert.Equal(JsonValueKind.Null, row.GetProperty("cedolareRate").ValueKind);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var stored = await db.PropertyFiscalYears.AsNoTracking().SingleAsync(y => y.PropertyId == property.Id);
        Assert.Equal(4, (int)stored.Regime);
    }

    [PostgresFact]
    public async Task CreatePayment_OtaStay_WithholdsOnlyOutsideImpresaRegime()
    {
        var host = $"auth0|co18-pay-{Guid.NewGuid():N}";
        using var client = _factory.CreateAuthenticatedClient(host, "PropertyOwner");
        var property = await _factory.SeedPropertyAsync(host);
        var profile = await client.PutAsJsonAsync("/api/fiscal/tax-profile", new { hasPartitaIva = true, partitaIvaNumber = "12345678901" });
        Assert.Equal(HttpStatusCode.OK, profile.StatusCode);
        var booking = await SeedStayAsync(property, new DateTime(TaxYear, 5, 10, 0, 0, 0, DateTimeKind.Utc), nights: 3, BookingSource.Airbnb);

        // Partita IVA but cedolare: still a short-term rental, the OTA withholds (fiscale.md C10).
        Assert.Equal(HttpStatusCode.OK, (await AssignAsync(client, property.Id, "CedolareSecca21")).StatusCode);
        var withheld = await CreatePaymentAsync(client, booking.Id, 300m);
        Assert.Equal(63m, withheld.GetProperty("otaWithholdingTax").GetDecimal());
        Assert.Equal("AutoOta", withheld.GetProperty("withholdingSource").GetString());

        // Impresa regime: not a short-term rental, no withholding.
        Assert.Equal(HttpStatusCode.OK, (await AssignAsync(client, property.Id, "RegimeOrdinario")).StatusCode);
        var notWithheld = await CreatePaymentAsync(client, booking.Id, 300m);
        Assert.Equal(0m, notWithheld.GetProperty("otaWithholdingTax").GetDecimal());
        Assert.False(notWithheld.GetProperty("withholdingTaxApplied").GetBoolean());
        Assert.Equal(300m, notWithheld.GetProperty("netAmountAfterWithholding").GetDecimal());
    }

    [PostgresFact]
    public async Task SetTaxpayer_InvalidCode_Returns422WithCode()
    {
        var host = $"auth0|co18-cf-{Guid.NewGuid():N}";
        using var client = _factory.CreateAuthenticatedClient(host, "PropertyOwner");
        var property = await _factory.SeedPropertyAsync(host);

        var response = await client.PutAsJsonAsync($"/api/fiscal/properties/{property.Id}/taxpayer", new { fiscalCode = "12345678901" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalid_taxpayer_fiscal_code", problem.GetProperty("code").GetString());
    }

    [PostgresFact]
    public async Task SetTaxpayer_PropertyOfAnotherOrg_Returns404()
    {
        var ownerA = $"auth0|co18-a-{Guid.NewGuid():N}";
        var ownerB = $"auth0|co18-b-{Guid.NewGuid():N}";
        var property = await _factory.SeedPropertyAsync(ownerA);
        await _factory.SeedOrgForOwnerAsync(ownerB);
        using var client = _factory.CreateAuthenticatedClient(ownerB, "PropertyOwner");

        var response = await client.PutAsJsonAsync($"/api/fiscal/properties/{property.Id}/taxpayer", new { fiscalCode = OwnerCodes[0] });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Null((await db.Properties.AsNoTracking().SingleAsync(p => p.Id == property.Id)).TaxpayerFiscalCode);
    }

    private static async Task<JsonElement> GetRegimeAsync(HttpClient client)
    {
        var response = await client.GetAsync($"/api/fiscal/regime?taxYear={TaxYear}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static Task<HttpResponseMessage> AssignAsync(HttpClient client, Guid propertyId, string regime) =>
        client.PutAsJsonAsync($"/api/fiscal/properties/{propertyId}/regime", new { taxYear = TaxYear, regime });

    private static async Task<JsonElement> CreatePaymentAsync(HttpClient client, Guid bookingId, decimal amount)
    {
        var response = await client.PostAsJsonAsync("/api/payments", new { bookingId, amount, method = "BankTransfer" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<Booking> SeedStayAsync(Property property, DateTime checkIn, int nights, BookingSource source = BookingSource.Direct)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var booking = new Booking
        {
            PropertyId = property.Id,
            OrgId = property.OrgId,
            Guest = new Guest
            {
                OrgId = property.OrgId,
                FirstName = "Ospite",
                LastName = "Fiscale",
                Email = $"co18-{Guid.NewGuid():N}@example.com",
                Country = "IT",
            },
            CheckInDate = checkIn,
            CheckOutDate = checkIn.AddDays(nights),
            NumberOfGuests = 1,
            NumberOfAdults = 1,
            Status = BookingStatus.Confirmed,
            Source = source,
            BasePrice = 100m * nights,
            TotalPrice = 100m * nights,
        };
        db.Bookings.Add(booking);
        await db.SaveChangesAsync();
        return booking;
    }
}
