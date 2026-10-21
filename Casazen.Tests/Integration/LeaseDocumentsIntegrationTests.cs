using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Infrastructure.Data;
using Casazen.Tests.Integration.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Casazen.Tests.Integration;

public class LeaseDocumentsIntegrationTests(LeaseFlowWebApplicationFactory factory) : IClassFixture<LeaseFlowWebApplicationFactory>
{
    [PostgresFact]
    public async Task Upload_IsPersistedOnLease_DownloadableOnlyUnderThatLease_AndDoesNotPollutePropertyDocuments()
    {
        var owner = $"auth0|lease-documents-{Guid.NewGuid():N}";
        var property = await factory.SeedPropertyAsync(owner);
        using var client = factory.CreateAuthenticatedClient(owner, "LongTermLandlord");
        var created = await client.PostAsJsonAsync("/api/leases", new
        {
            propertyId = property.Id,
            fiscalRegime = "CedolareSecca",
            startDate = "2026-09-01T00:00:00Z",
            endDate = "2030-08-31T00:00:00Z",
            monthlyRent = 1200m,
            parties = new[] {
                new { role = "Landlord", firstName = "Mario", lastName = "Rossi", fiscalCode = "RSSMRA80A01H501U", citizenship = "IT", contactEmail = "mario@example.com" },
                new { role = "Tenant", firstName = "Giulia", lastName = "Verdi", fiscalCode = "VRDGLI85B02F205A", citizenship = "IT", contactEmail = "giulia@example.com" } }
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var leaseId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var pdf = Encoding.ASCII.GetBytes("%PDF-1.7\noriginal attestation\n%%EOF");
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(pdf); file.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        form.Add(file, "file", "attestazione.pdf"); form.Add(new StringContent("Attestation"), "kind");
        var uploaded = await client.PostAsync($"/api/leases/{leaseId}/documents", form);
        Assert.Equal(HttpStatusCode.Created, uploaded.StatusCode);
        var documentId = (await uploaded.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var list = await client.GetFromJsonAsync<JsonElement>($"/api/leases/{leaseId}/documents");
        Assert.Equal(leaseId, Assert.Single(list.EnumerateArray()).GetProperty("leaseId").GetGuid());
        Assert.Equal("Attestation", list[0].GetProperty("kind").GetString());
        var downloaded = await client.GetAsync($"/api/leases/{leaseId}/documents/{documentId}/download");
        Assert.Equal(HttpStatusCode.OK, downloaded.StatusCode);
        Assert.Equal(pdf, await downloaded.Content.ReadAsByteArrayAsync());
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Equal(leaseId, (await db.LeaseDocuments.SingleAsync(d => d.Id == documentId)).LeaseContractId);
            Assert.False(await db.PropertyDocuments.AnyAsync(d => d.FileName == "attestazione.pdf"));
        }
        using var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"/api/leases/{leaseId}/documents/{documentId}/download")).StatusCode);
        var other = $"auth0|other-documents-{Guid.NewGuid():N}";
        await factory.SeedOrgForOwnerAsync(other);
        using var outsider = factory.CreateAuthenticatedClient(other, "LongTermLandlord");
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.GetAsync($"/api/leases/{leaseId}/documents")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.GetAsync($"/api/leases/{leaseId}/documents/{documentId}/download")).StatusCode);
        var detail = await client.GetFromJsonAsync<JsonElement>($"/api/leases/{leaseId}");
        Assert.Equal("Draft", detail.GetProperty("status").GetString());
    }
}
