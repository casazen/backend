using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Repositories;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Data.Seeds;
using Casazen.Infrastructure.Services.LeaseContracts;
using Casazen.Tests.Integration.Postgres;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// LT-10 on PostgreSQL: the canone concordato range is computed and checked by the server at lease creation from the
/// characteristics and the lease dates (A7-12); verified agreement data refuse a rent outside it (422), Partial data only
/// warn (A7-23); the term is checked per contract type (A7-13); cadastral data, APE identification and deposit are
/// stored and fill the contract template (LT-03 data).
/// </summary>
public class LeaseConcordatoAndContractDataIntegrationTests : IClassFixture<LeaseContractDataWebApplicationFactory>
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private readonly LeaseContractDataWebApplicationFactory _factory;

    public LeaseConcordatoAndContractDataIntegrationTests(LeaseContractDataWebApplicationFactory factory) => _factory = factory;

    [PostgresFact]
    public async Task CreateLease_ConcordatoRentOutsideRangeWithVerifiedData_Returns422AndSavesNothing()
    {
        var owner = UniqueOwner("verified");
        var comune = await SeedVerifiedAgreementAsync();
        var property = await SeedPropertyInAsync(owner, comune);
        using var client = _factory.CreateAuthenticatedClient(owner, "LongTermLandlord");

        // 65 mq, sub-fascia 2, 3 years: 1.300,00-5.525,00 euro a year, 108,34-460,41 a month.
        var response = await client.PostAsJsonAsync("/api/leases", ConcordatoBody(property.Id, monthlyRent: 900m));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await ReadJson(response);
        Assert.Equal("concordato_rent_out_of_range", problem.GetProperty("code").GetString());
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        // No tenant in this scope: the filter is bypassed on purpose to see every lease of the property.
        Assert.False(await db.LeaseContracts.IgnoreQueryFilters().AnyAsync(l => l.PropertyId == property.Id));
    }

    [PostgresFact]
    public async Task CreateLease_ConcordatoRentWithinRangeWithVerifiedData_StoresTheServerRange()
    {
        var owner = UniqueOwner("verified-ok");
        var comune = await SeedVerifiedAgreementAsync();
        var property = await SeedPropertyInAsync(owner, comune);
        using var client = _factory.CreateAuthenticatedClient(owner, "LongTermLandlord");

        var response = await client.PostAsJsonAsync("/api/leases", ConcordatoBody(property.Id, monthlyRent: 400m));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var lease = await ReadJson(response);
        Assert.Equal("Concordato", lease.GetProperty("contractType").GetString());
        Assert.Equal("CedolareSecca", lease.GetProperty("taxRegime").GetString());
        Assert.Equal("CanoneConcordato", lease.GetProperty("fiscalRegime").GetString());
        var assessment = lease.GetProperty("concordatoAssessment");
        Assert.False(assessment.GetProperty("indicative").GetBoolean());
        Assert.True(assessment.GetProperty("rentWithinRange").GetBoolean());
        Assert.Equal(3, assessment.GetProperty("contractYears").GetInt32());
        Assert.Equal(108.34m, assessment.GetProperty("canoneMinMensile").GetDecimal());
        Assert.Equal(460.41m, assessment.GetProperty("canoneMaxMensile").GetDecimal());
    }

    [PostgresFact]
    public async Task CreateLease_ConcordatoRentOutsideRangeWithPartialData_CreatesTheLeaseWithAnIndicativeRange()
    {
        // Seveso comes from the migrated MB data: Partial (RS-8), so the range is a guide and never blocks (A7-23).
        var owner = UniqueOwner("partial");
        var property = await SeedPropertyInAsync(owner, "Seveso");
        using var client = _factory.CreateAuthenticatedClient(owner, "LongTermLandlord");

        var eligibility = await client.GetAsync(
            $"/api/properties/{property.Id}/canone-concordato/eligibility?sqm=65&typeACount=2&typeBCount=3&startDate=2026-09-01&endDate=2029-08-31");
        Assert.Equal(HttpStatusCode.OK, eligibility.StatusCode);
        var range = await ReadJson(eligibility);
        Assert.True(range.GetProperty("indicative").GetBoolean());
        Assert.Contains("partial_data", range.GetProperty("warnings").EnumerateArray().Select(w => w.GetString()));
        Assert.Equal(3, range.GetProperty("contractYears").GetInt32());
        Assert.Equal(460.41m, range.GetProperty("canoneMaxMensile").GetDecimal());
        Assert.Equal(CanoneConcordatoMbSeed.SourceUrl, range.GetProperty("sourceUrl").GetString());

        var response = await client.PostAsJsonAsync("/api/leases", ConcordatoBody(property.Id, monthlyRent: 900m));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var assessment = (await ReadJson(response)).GetProperty("concordatoAssessment");
        Assert.True(assessment.GetProperty("indicative").GetBoolean());
        Assert.False(assessment.GetProperty("rentWithinRange").GetBoolean());
        Assert.Equal("Partial", assessment.GetProperty("dataCompleteness").GetString());
        Assert.Equal(460.41m, assessment.GetProperty("canoneMaxMensile").GetDecimal());
    }

    [PostgresFact]
    public async Task CreateLease_ConcordatoShorterThanThreeYears_Returns422TermTooShort()
    {
        var owner = UniqueOwner("short");
        var property = await SeedPropertyInAsync(owner, "Seveso");
        using var client = _factory.CreateAuthenticatedClient(owner, "LongTermLandlord");

        var response = await client.PostAsJsonAsync(
            "/api/leases", ConcordatoBody(property.Id, monthlyRent: 400m, endDate: "2027-08-31"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("lease_term_too_short", (await ReadJson(response)).GetProperty("code").GetString());
    }

    [PostgresFact]
    public async Task ContractData_CadastralApeAndDeposit_AreStoredAndUsedByTheTemplate()
    {
        var owner = UniqueOwner("data");
        var property = await SeedPropertyInAsync(owner, "Seveso");
        using var client = _factory.CreateAuthenticatedClient(owner, "LongTermLandlord");
        var upload = await client.PostAsync($"/api/properties/{property.Id}/documents", ApeForm());
        Assert.Equal(HttpStatusCode.Created, upload.StatusCode);
        var apeId = (await ReadJson(upload)).GetProperty("id").GetGuid();

        var created = await client.PostAsJsonAsync("/api/leases", new
        {
            propertyId = property.Id,
            contractType = "Libero",
            taxRegime = "Ordinario",
            startDate = "2026-09-01",
            endDate = "2030-08-31",
            monthlyRent = 800m,
            securityDeposit = 2400m,
            parties = Parties(),
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var leaseId = (await ReadJson(created)).GetProperty("id").GetGuid();

        // The approved template of this factory uses the cadastral data and the APE: without them no final contract.
        var blocked = await client.GetAsync($"/api/leases/{leaseId}/contract.pdf");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, blocked.StatusCode);
        Assert.Equal("contract_data_missing", (await ReadJson(blocked)).GetProperty("code").GetString());

        var cadastral = await client.PutAsJsonAsync($"/api/properties/{property.Id}/cadastral", new
        {
            sheet = " 12 ",
            parcel = "345",
            subaltern = "6",
            category = "a/2",
            income = 512.30m,
        });
        Assert.Equal(HttpStatusCode.NoContent, cadastral.StatusCode);
        var ape = await client.PutAsJsonAsync(
            $"/api/properties/{property.Id}/documents/{apeId}/ape", new { code = "1510800012345", energyClass = "b" });
        Assert.Equal(HttpStatusCode.OK, ape.StatusCode);
        Assert.Equal("B", (await ReadJson(ape)).GetProperty("apeEnergyClass").GetString());

        var contract = await client.GetAsync($"/api/leases/{leaseId}/contract.pdf");
        Assert.Equal(HttpStatusCode.OK, contract.StatusCode);
        Assert.Equal("application/pdf", contract.Content.Headers.ContentType?.MediaType);

        using var scope = _factory.Services.CreateScope();
        var lease = await scope.ServiceProvider.GetRequiredService<ILeaseContractRepository>().GetByIdWithDetailsAsync(leaseId);
        var data = LeaseContractDocument.ResolveData(lease!);
        Assert.Equal("Foglio 12, particella 345, subalterno 6, categoria A/2, rendita catastale euro 512,30",
            data["dati_catastali"]);
        Assert.Equal("codice 1510800012345, classe energetica B", data["ape_estremi"]);
        Assert.Equal("2.400,00", data["deposito_cauzionale"]);
        var stored = await ReadJson(await client.GetAsync($"/api/properties/{property.Id}"));
        Assert.Equal("12", stored.GetProperty("cadastralSheet").GetString());
        Assert.Equal("A/2", stored.GetProperty("cadastralCategory").GetString());
    }

    [PostgresFact]
    public async Task UpdateApeIdentification_NotAnApeOrInvalidClass_Returns422()
    {
        var owner = UniqueOwner("ape-invalid");
        var property = await SeedPropertyInAsync(owner, "Seveso");
        using var client = _factory.CreateAuthenticatedClient(owner, "LongTermLandlord");
        var ape = await ReadJson(await client.PostAsync($"/api/properties/{property.Id}/documents", ApeForm()));
        var other = await ReadJson(await client.PostAsync($"/api/properties/{property.Id}/documents", ApeForm("Other")));

        var invalidClass = await client.PutAsJsonAsync(
            $"/api/properties/{property.Id}/documents/{ape.GetProperty("id").GetGuid()}/ape", new { code = "123", energyClass = "A-" });
        var notApe = await client.PutAsJsonAsync(
            $"/api/properties/{property.Id}/documents/{other.GetProperty("id").GetGuid()}/ape", new { code = "123", energyClass = "A4" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, invalidClass.StatusCode);
        Assert.Equal("ape_identification_invalid", (await ReadJson(invalidClass)).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, notApe.StatusCode);
        Assert.Equal("document_not_ape", (await ReadJson(notApe)).GetProperty("code").GetString());
    }

    /// <summary>The body the lease form sends (dates without time, characteristics, no year count).</summary>
    private static object ConcordatoBody(Guid propertyId, decimal monthlyRent, string endDate = "2029-08-31") => new
    {
        propertyId,
        contractType = "Concordato",
        taxRegime = "CedolareSecca",
        startDate = "2026-09-01",
        endDate,
        monthlyRent,
        parties = Parties(),
        canoneConcordato = new
        {
            sqm = 65m,
            typeAElementCount = 2,
            typeBElementCount = 3,
            typeCElementCount = 0,
            typeDElementCount = 0,
            isFurnished = false,
            zoneName = (string?)null,
            cadastralSheet = (string?)null,
        },
    };

    private static object[] Parties() =>
    [
        new { role = "Landlord", firstName = "Mario", lastName = "Rossi", fiscalCode = "RSSMRA80A01H501Z", citizenship = "IT", contactEmail = "mario@example.com" },
        new { role = "Tenant", firstName = "Giulia", lastName = "Verdi", fiscalCode = "VRDGLI85B02F205X", citizenship = "IT", contactEmail = "giulia@example.com" },
    ];

    private async Task<Property> SeedPropertyInAsync(string owner, string city)
    {
        var property = await _factory.SeedPropertyAsync(owner);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        // Test setup outside a request (no tenant): the filter is bypassed on purpose to move the seeded property.
        await db.Properties.IgnoreQueryFilters()
            .Where(p => p.Id == property.Id)
            .ExecuteUpdateAsync(p => p.SetProperty(x => x.City, city));
        property.City = city;
        return property;
    }

    /// <summary>
    /// A comune whose agreement data are verified (Complete): the Seveso tables under another name, so the check blocks.
    /// </summary>
    private async Task<string> SeedVerifiedAgreementAsync()
    {
        var comune = $"Verificato {Guid.NewGuid():N}"[..20];
        var seveso = CanoneConcordatoMbSeed.BuildAgreements().Single(a => a.Comune == "Seveso");
        var agreementId = Guid.NewGuid();
        seveso.Id = agreementId;
        seveso.Comune = comune;
        seveso.DataCompleteness = DataCompleteness.Complete;
        seveso.Signatories = [];
        foreach (var band in seveso.Bands)
        {
            band.Id = Guid.NewGuid();
            band.TerritorialRentAgreementId = agreementId;
        }

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.TerritorialRentAgreements.Add(seveso);
        await db.SaveChangesAsync();
        return comune;
    }

    private static MultipartFormDataContent ApeForm(string documentType = "Ape")
    {
        var form = new MultipartFormDataContent();
        var pdf = new ByteArrayContent(Encoding.ASCII.GetBytes("%PDF-1.4\n1 0 obj <<>> endobj\n%%EOF"));
        pdf.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        form.Add(pdf, "file", "ape.pdf");
        form.Add(new StringContent(documentType), "documentType");
        return form;
    }

    private static string UniqueOwner(string suffix) => $"auth0|lt10-{suffix}-{Guid.NewGuid():N}";

    private static async Task<JsonElement> ReadJson(HttpResponseMessage response) =>
        JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync(), JsonOpts);
}

/// <summary>
/// Lease flow whose <c>RegimeOrdinario</c> template is approved and uses the data LT-10 added to the model: cadastral
/// data, APE identification, security deposit (fixture <c>RegimeOrdinario/test-fixture-data-v1</c>, test texts only).
/// </summary>
public class LeaseContractDataWebApplicationFactory : LeaseFlowWebApplicationFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["LeaseTemplates:Variants:RegimeOrdinario:VersionId"] = "test-fixture-data-v1",
                ["LeaseTemplates:Variants:RegimeOrdinario:Approved"] = "true",
                ["LeaseTemplates:Variants:RegimeOrdinario:ApprovalReference"] = "integration test fixture",
            }));
    }
}
