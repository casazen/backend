using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Casazen.Tests.Integration.Postgres;
using Casazen.Tests.Unit.Documents;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// LT-15 (A7-29): the real lease flow on PostgreSQL with the providers as production has them (RLI and e-signature
/// features off, so the registration and the signature are the manual ones; no provider is ever called), from the
/// multi-party lease (LT-14) to the rent installments (LT-06): create, contract PDF with every party, signed upload with
/// the stipula date, manual RLI with receipt, installments, offline payment. Unlike the older flow test, nothing here
/// confirms on the landlord's behalf and a dishonest step fails the test.
/// </summary>
public class LeaseRealFlowPostgresTests(LeaseFlowWebApplicationFactory factory) : IClassFixture<LeaseFlowWebApplicationFactory>
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private static readonly byte[] ReceiptPdf = Encoding.ASCII.GetBytes("%PDF-1.4\n% ricevuta RLI del test di flusso\n%%EOF\n");

    [PostgresFact]
    public async Task Flow_MultiPartyLease_ContractSignatureManualRliAndRent_EndsWithAHonestLedger()
    {
        var owner = $"auth0|lt15-flow-{Guid.NewGuid():N}";
        var property = await factory.SeedPropertyAsync(owner);
        using var client = factory.CreateAuthenticatedClient(owner, "LongTermLandlord");

        // 1. Lease with two landlords and two tenants; fiscal codes are validated and stored normalized (LT-14).
        var create = await client.PostAsJsonAsync("/api/leases", new
        {
            propertyId = property.Id,
            fiscalRegime = "CedolareSecca",
            startDate = "2026-09-01T00:00:00Z",
            endDate = "2030-08-31T00:00:00Z",
            monthlyRent = 1200m,
            parties = new object[]
            {
                new { role = "Landlord", firstName = "Mario", lastName = "Rossi", fiscalCode = "rssmra80a01h501u", citizenship = "IT", contactEmail = "mario@example.com" },
                new { role = "Landlord", firstName = "Anna", lastName = "Bianchi", fiscalCode = "BNCNNA82A41F205W", citizenship = "IT", contactEmail = "anna@example.com" },
                new { role = "Tenant", firstName = "Giulia", lastName = "Verdi", fiscalCode = "VRDGLI85B02F205A", citizenship = "IT", contactEmail = "giulia@example.com" },
                new { role = "Tenant", firstName = "Acme", lastName = "Srl", fiscalCode = "00123456782", citizenship = "IT", contactEmail = "acme@example.com" },
            },
        });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var leaseId = (await ReadJson(create)).GetProperty("id").GetGuid();
        Assert.Equal("Draft", (await GetAsync(client, $"/api/leases/{leaseId}")).GetProperty("status").GetString());

        // No installments for a lease nobody signed.
        var early = await client.PutAsJsonAsync($"/api/leases/{leaseId}/rent/schedule", new { cadence = "Monthly" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, early.StatusCode);

        // 2. The contract to sign is the approved template with all four parties, not a draft.
        var contract = await client.GetAsync($"/api/leases/{leaseId}/contract.pdf");
        Assert.Equal(HttpStatusCode.OK, contract.StatusCode);
        var contractText = PdfTestReader.Text(await contract.Content.ReadAsByteArrayAsync());
        Assert.DoesNotContain("BOZZA", contractText, StringComparison.Ordinal);
        foreach (var name in new[] { "Rossi", "Bianchi", "Verdi", "Srl" })
            Assert.Contains(name, contractText, StringComparison.Ordinal);

        // 3. Manual signature: a file that is not a PDF is refused and the lease stays Draft; the real one signs it.
        var notPdf = await LeaseSigningTestClient.UploadSignedContractAsync(client, leaseId, content: Encoding.ASCII.GetBytes("non sono un pdf"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, notPdf.StatusCode);
        Assert.Equal("Draft", (await GetAsync(client, $"/api/leases/{leaseId}")).GetProperty("status").GetString());
        (await LeaseSigningTestClient.UploadSignedContractAsync(client, leaseId)).EnsureSuccessStatusCode();
        var signed = await GetAsync(client, $"/api/leases/{leaseId}");
        Assert.Equal("Signed", signed.GetProperty("status").GetString());
        Assert.Equal(new DateTime(2026, 9, 19), signed.GetProperty("registrationDeadline").GetDateTime().Date);
        Assert.Equal(0, factory.ESignProvider.Calls);

        // 4. Manual RLI: nothing is confirmed until the landlord declares the number and uploads the receipt.
        Assert.Equal(0, factory.RegistrationProvider.CallsFor(leaseId));
        var withoutReceipt = await client.GetAsync($"/api/leases/{leaseId}/registration/receipt");
        Assert.NotEqual(HttpStatusCode.OK, withoutReceipt.StatusCode);
        using var form = new MultipartFormDataContent
        {
            { new StringContent("24091234567890123-000001"), "registrationCode" },
            { new StringContent("2026-09-18"), "registrationDate" },
        };
        var file = new ByteArrayContent(ReceiptPdf);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        form.Add(file, "receipt", "ricevuta-rli.pdf");
        (await client.PostAsync($"/api/leases/{leaseId}/registration/manual", form)).EnsureSuccessStatusCode();
        var registered = await GetAsync(client, $"/api/leases/{leaseId}");
        Assert.Equal("Registered", registered.GetProperty("status").GetString());
        var receipt = await client.GetAsync($"/api/leases/{leaseId}/registration/receipt");
        Assert.Equal(ReceiptPdf, await receipt.Content.ReadAsByteArrayAsync());
        Assert.Equal(0, factory.RegistrationProvider.CallsFor(leaseId));

        // 5. Rent: installments from the lease; the offline payment is what the landlord declares, nothing else is paid.
        var ledger = await ReadJson(await client.PutAsJsonAsync($"/api/leases/{leaseId}/rent/schedule", new { cadence = "Monthly", billingDayOfMonth = 5 }));
        var installments = ledger.GetProperty("installments").EnumerateArray().ToList();
        Assert.Equal(48, installments.Count);
        Assert.All(installments, i => Assert.Equal("Scheduled", i.GetProperty("status").GetString()));
        Assert.Equal(1200m, installments.Sum(i => i.GetProperty("amount").GetDecimal()) / 48);
        var first = installments[0].GetProperty("id").GetGuid();
        var paid = await client.PostAsJsonAsync($"/api/leases/{leaseId}/rent/installments/{first}/mark-paid", new { paidOn = "2026-09-05" });
        Assert.Equal(HttpStatusCode.OK, paid.StatusCode);
        var after = (await GetAsync(client, $"/api/leases/{leaseId}/rent")).GetProperty("installments").EnumerateArray().ToList();
        Assert.Equal("Paid", after[0].GetProperty("status").GetString());
        Assert.All(after.Skip(1), i => Assert.Equal("Scheduled", i.GetProperty("status").GetString()));

        // Another host sees none of it.
        var stranger = $"auth0|lt15-stranger-{Guid.NewGuid():N}";
        await factory.SeedOrgForOwnerAsync(stranger);
        using var strangerClient = factory.CreateAuthenticatedClient(stranger, "LongTermLandlord");
        Assert.Equal(HttpStatusCode.NotFound, (await strangerClient.GetAsync($"/api/leases/{leaseId}/rent")).StatusCode);
    }

    private static async Task<JsonElement> GetAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        response.EnsureSuccessStatusCode();
        return await ReadJson(response);
    }

    private static async Task<JsonElement> ReadJson(HttpResponseMessage response) =>
        JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync(), JsonOpts);
}
