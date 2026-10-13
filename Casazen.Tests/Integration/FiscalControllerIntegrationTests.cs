using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Infrastructure.Data;
using Casazen.Tests.Unit.Documents;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Casazen.Tests.Integration;

public class FiscalControllerIntegrationTests : IClassFixture<CasazenWebApplicationFactory>
{
    private readonly CasazenWebApplicationFactory _factory;

    public FiscalControllerIntegrationTests(CasazenWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task GetRegime_OneProperty_RecommendsCedolare21()
    {
        var owner = $"auth0|fiscal-{Guid.NewGuid():N}";
        var property = await _factory.SeedPropertyAsync(owner);
        // Only apartments with short-term stays in the tax year count toward the threshold (CO-18, A5-22).
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Bookings.Add(new Booking
            {
                PropertyId = property.Id,
                OrgId = property.OrgId,
                Guest = new Guest
                {
                    OrgId = property.OrgId,
                    FirstName = "Regime",
                    LastName = "Guest",
                    Email = $"regime-{Guid.NewGuid():N}@example.com",
                    Country = "IT",
                },
                CheckInDate = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
                CheckOutDate = new DateTime(2026, 5, 4, 0, 0, 0, DateTimeKind.Utc),
                NumberOfGuests = 1,
                NumberOfAdults = 1,
                Status = BookingStatus.Confirmed,
                Source = BookingSource.Direct,
                BasePrice = 300m,
                TotalPrice = 300m,
            });
            await db.SaveChangesAsync();
        }

        using var client = _factory.CreateAuthenticatedClient(owner, "PropertyOwner");
        var response = await client.GetAsync("/api/fiscal/regime?taxYear=2026");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, body.GetProperty("strPropertyCount").GetInt32());
        Assert.False(body.GetProperty("requiresPartitaIva").GetBoolean());
        Assert.Equal("CedolareSecca21", body.GetProperty("properties")[0].GetProperty("recommendedRegime").GetString());
        Assert.Contains("informativa", body.GetProperty("disclaimer").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AssignRegime_ForeignProperty_Returns404()
    {
        var ownerA = $"auth0|fiscal-a-{Guid.NewGuid():N}";
        var ownerB = $"auth0|fiscal-b-{Guid.NewGuid():N}";
        var property = await _factory.SeedPropertyAsync(ownerA);
        await _factory.SeedOrgForOwnerAsync(ownerB);

        using var client = _factory.CreateAuthenticatedClient(ownerB, "PropertyOwner");
        var response = await client.PutAsJsonAsync($"/api/fiscal/properties/{property.Id}/regime", new
        {
            taxYear = 2026,
            regime = "CedolareSecca21",
            isPrimaryForCedolare = true,
        });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task AnnualReport_ContainsPackLabelNotTaxDue()
    {
        var owner = $"auth0|fiscal-r-{Guid.NewGuid():N}";
        await _factory.SeedPropertyAsync(owner);
        using var client = _factory.CreateAuthenticatedClient(owner, "PropertyOwner");
        var response = await client.GetAsync("/api/fiscal/reports/annual/2026");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        Assert.Contains("commercialista", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("irpef", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("taxDue", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AnnualReport_IncludesSettledPaymentsForInactiveProperties()
    {
        var owner = $"auth0|fiscal-inactive-{Guid.NewGuid():N}";
        var property = await _factory.SeedPropertyAsync(owner);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var trackedProperty = await db.Properties.SingleAsync(p => p.Id == property.Id);
            trackedProperty.IsActive = false;

            var guest = new Guest
            {
                OrgId = property.OrgId,
                FirstName = "Inactive",
                LastName = "Guest",
                Email = $"inactive-{Guid.NewGuid():N}@example.com",
                Country = "IT",
            };
            var booking = new Booking
            {
                PropertyId = property.Id,
                OrgId = property.OrgId,
                Guest = guest,
                CheckInDate = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
                CheckOutDate = new DateTime(2026, 6, 3, 0, 0, 0, DateTimeKind.Utc),
                NumberOfGuests = 1,
                NumberOfAdults = 1,
                Status = BookingStatus.Confirmed,
                Source = BookingSource.Direct,
                BasePrice = 240m,
                TotalPrice = 250m,
            };
            db.Bookings.Add(booking);
            db.Payments.Add(new Payment
            {
                Booking = booking,
                OrgId = property.OrgId,
                Amount = 250m,
                Status = PaymentStatus.Completed,
                Method = PaymentMethod.CreditCard,
                ProcessedAt = new DateTime(2026, 6, 2, 0, 0, 0, DateTimeKind.Utc),
                NetAmountAfterWithholding = 250m,
            });

            await db.SaveChangesAsync();
        }

        using var client = _factory.CreateAuthenticatedClient(owner, "PropertyOwner");
        var response = await client.GetAsync("/api/fiscal/reports/annual/2026");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var reportProperty = body.GetProperty("properties").EnumerateArray()
            .Single(p => p.GetProperty("propertyId").GetGuid() == property.Id);
        Assert.Equal(250m, reportProperty.GetProperty("grossIncome").GetDecimal());
        Assert.Equal(250m, body.GetProperty("totals").GetProperty("grossIncome").GetDecimal());
    }

    [Fact]
    public async Task AnnualReportCsv_HasCsvContentType()
    {
        var owner = $"auth0|fiscal-csv-{Guid.NewGuid():N}";
        await _factory.SeedPropertyAsync(owner);
        using var client = _factory.CreateAuthenticatedClient(owner, "PropertyOwner");
        var response = await client.GetAsync("/api/fiscal/reports/annual/2026?format=csv");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/csv", response.Content.Headers.ContentType?.MediaType);
        Assert.DoesNotContain("FiscalCode", response.Content.Headers.ContentDisposition?.FileName ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TouristTaxReport_Pdf_ReturnsItalianTablesPerComuneAndMonth()
    {
        var owner = $"auth0|fiscal-tt-{Guid.NewGuid():N}";
        var property = await _factory.SeedPropertyAsync(owner);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Bookings.Add(new Booking
            {
                PropertyId = property.Id,
                OrgId = property.OrgId,
                Guest = new Guest
                {
                    OrgId = property.OrgId,
                    FirstName = "Tassa",
                    LastName = "Soggiorno",
                    Email = $"tt-{Guid.NewGuid():N}@example.com",
                    Country = "IT",
                },
                CheckInDate = new DateTime(2026, 3, 2, 0, 0, 0, DateTimeKind.Utc),
                CheckOutDate = new DateTime(2026, 3, 5, 0, 0, 0, DateTimeKind.Utc),
                NumberOfGuests = 2,
                NumberOfAdults = 2,
                Status = BookingStatus.Confirmed,
                Source = BookingSource.Direct,
                BasePrice = 300m,
                TouristTax = 57m,
                TouristTaxAmount = 57m,
                TotalPrice = 357m,
            });
            await db.SaveChangesAsync();
        }

        using var client = _factory.CreateAuthenticatedClient(owner, "PropertyOwner");
        var json = await client.GetFromJsonAsync<JsonElement>("/api/fiscal/reports/tourist-tax?from=2026-01-01&to=2026-03-31");
        Assert.Equal(57m, json.GetProperty("totals").GetProperty("amount").GetDecimal());
        Assert.Equal(3, json.GetProperty("rows")[0].GetProperty("month").GetInt32());

        var response = await client.GetAsync("/api/fiscal/reports/tourist-tax?from=2026-01-01&to=2026-03-31&format=pdf");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/pdf", response.Content.Headers.ContentType?.MediaType);
        var text = PdfTestReader.Text(await response.Content.ReadAsByteArrayAsync());
        Assert.Contains("Tassa di soggiorno per comune e periodo", text, StringComparison.Ordinal);
        Assert.Contains("marzo 2026", text, StringComparison.Ordinal);
        Assert.Contains("57,00", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/api/fiscal/reports/tourist-tax?from=2026-01-01")]
    [InlineData("/api/fiscal/reports/tourist-tax?from=2026-03-01&to=2026-01-01")]
    [InlineData("/api/fiscal/reports/annual/2026?from=2025-12-01&to=2026-01-31")]
    [InlineData("/api/fiscal/reports/withholding/2026?from=2026-06-01&to=2026-05-01&format=pdf")]
    public async Task Reports_InvalidPeriod_Return400WithStableCode(string url)
    {
        var owner = $"auth0|fiscal-period-{Guid.NewGuid():N}";
        await _factory.SeedPropertyAsync(owner);
        using var client = _factory.CreateAuthenticatedClient(owner, "PropertyOwner");

        var response = await client.GetAsync(url);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("fiscal_report_period_invalid", body.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("detail").GetString()));
    }

    [Fact]
    public async Task AnnualReportPdf_ItalianSummaryWithoutEnglishLine()
    {
        var owner = $"auth0|fiscal-pdf-{Guid.NewGuid():N}";
        await _factory.SeedPropertyAsync(owner);
        using var client = _factory.CreateAuthenticatedClient(owner, "PropertyOwner");

        var response = await client.GetAsync("/api/fiscal/reports/annual/2026?format=pdf");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/pdf", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("casazen-redditi-20260101-20261231.pdf", response.Content.Headers.ContentDisposition?.ToString() ?? "", StringComparison.Ordinal);
        var text = PdfTestReader.Text(await response.Content.ReadAsByteArrayAsync());
        Assert.Contains("Incassi per immobile", text, StringComparison.Ordinal);
        Assert.Contains("Note e fonti", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Gross", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PutTaxProfile_OnlyFiscalCode_KeepsTheSavedPartitaIva()
    {
        var owner = $"auth0|fiscal-profile-{Guid.NewGuid():N}";
        await _factory.SeedPropertyAsync(owner);
        using var client = _factory.CreateAuthenticatedClient(owner, "PropertyOwner");
        var first = await client.PutAsJsonAsync("/api/fiscal/tax-profile", new { hasPartitaIva = true, partitaIvaNumber = "12345678901" });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var second = await client.PutAsJsonAsync("/api/fiscal/tax-profile", new { fiscalCode = "RSSMRA80A01H501U" });

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var profile = await client.GetFromJsonAsync<JsonElement>("/api/fiscal/tax-profile");
        Assert.True(profile.GetProperty("hasPartitaIva").GetBoolean());
        Assert.Equal("12345678901", profile.GetProperty("partitaIvaNumber").GetString());
        Assert.Equal("RSSMRA80A01H501U", profile.GetProperty("fiscalCode").GetString());
    }

    [Fact]
    public async Task PutTaxProfile_InvalidPartitaIva_Returns400WithLocalizedProblem()
    {
        var owner = $"auth0|fiscal-profile-bad-{Guid.NewGuid():N}";
        await _factory.SeedPropertyAsync(owner);
        using var client = _factory.CreateAuthenticatedClient(owner, "PropertyOwner");

        var response = await client.PutAsJsonAsync("/api/fiscal/tax-profile", new { hasPartitaIva = true, partitaIvaNumber = "123" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("fiscal_tax_identifier_invalid", body.GetProperty("code").GetString());
    }
}
