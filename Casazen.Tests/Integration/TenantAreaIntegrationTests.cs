using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// The tenant's own area over HTTP (<c>api/tenant-area</c>): the lease a landlord is still preparing is not the tenant's to read, by any route,
/// even when the tenant knows its id; every lease that is no longer a draft is.
/// </summary>
public sealed class TenantAreaIntegrationTests(CasazenWebApplicationFactory factory) : IClassFixture<CasazenWebApplicationFactory>
{
    private static readonly byte[] Pdf = Encoding.ASCII.GetBytes("%PDF-1.7\ntenant area contract\n%%EOF");

    [Fact]
    public async Task ADraftIsNeitherListedNorServedToItsTenant_AndAppearsOnceItIsNoLongerADraft()
    {
        var owner = $"auth0|tenant-area-owner-{Guid.NewGuid():N}";
        var email = $"tenant-{Guid.NewGuid():N}@example.test";
        var draft = await SeedLeaseAsync(await factory.SeedPropertyAsync(owner), LeaseStatus.Draft, email);
        var registered = await SeedLeaseAsync(await factory.SeedPropertyAsync(owner), LeaseStatus.Registered, email);
        using var tenant = factory.CreateAuthenticatedClient($"auth0|tenant-area-{Guid.NewGuid():N}", null, email, true);

        // The list holds the lease in force only.
        Assert.Equal(new[] { registered.LeaseId }, await ListedLeasesAsync(tenant));

        // The draft is not found by its id on the sheet, the document and the maintenance requests, as if it were somebody else's.
        Assert.Equal(HttpStatusCode.NotFound, (await tenant.GetAsync($"/api/tenant-area/contracts/{draft.LeaseId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await tenant.GetAsync($"/api/tenant-area/contracts/{draft.LeaseId}/documents/{draft.DocumentId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await tenant.GetAsync($"/api/tenant-area/contracts/{draft.LeaseId}/maintenance")).StatusCode);

        // The lease in force is served: the sheet and its contract.
        var sheet = await tenant.GetAsync($"/api/tenant-area/contracts/{registered.LeaseId}");
        Assert.Equal(HttpStatusCode.OK, sheet.StatusCode);
        var download = await tenant.GetAsync($"/api/tenant-area/contracts/{registered.LeaseId}/documents/{registered.DocumentId}");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal(Pdf, await download.Content.ReadAsByteArrayAsync());

        // The landlord sends the draft out for signature: it is no longer a draft, so the tenant reads it.
        await SetStatusAsync(draft.LeaseId, LeaseStatus.AwaitingSignature);
        Assert.Equal(
            new[] { draft.LeaseId, registered.LeaseId }.Order(),
            (await ListedLeasesAsync(tenant)).Order());
        Assert.Equal(HttpStatusCode.OK, (await tenant.GetAsync($"/api/tenant-area/contracts/{draft.LeaseId}")).StatusCode);
    }

    [Fact]
    public async Task ATenantWhoseOnlyLeaseIsADraftSeesAnEmptyAreaNotAnError()
    {
        var email = $"tenant-{Guid.NewGuid():N}@example.test";
        await SeedLeaseAsync(await factory.SeedPropertyAsync($"auth0|tenant-area-owner-{Guid.NewGuid():N}"), LeaseStatus.Draft, email);
        using var tenant = factory.CreateAuthenticatedClient($"auth0|tenant-area-{Guid.NewGuid():N}", null, email, true);

        Assert.Empty(await ListedLeasesAsync(tenant));
    }

    private static async Task<List<Guid>> ListedLeasesAsync(HttpClient tenant)
    {
        var response = await tenant.GetAsync("/api/tenant-area");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("contracts").EnumerateArray().Select(c => c.GetProperty("id").GetGuid()).ToList();
    }

    /// <summary>A lease of the property with the tenant on it by e-mail and a contract document in the private storage.</summary>
    private async Task<(Guid LeaseId, Guid DocumentId)> SeedLeaseAsync(Property property, LeaseStatus status, string tenantEmail)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var storage = scope.ServiceProvider.GetRequiredService<IFileStorage>();
        var lease = new LeaseContract
        {
            PropertyId = property.Id,
            OrgId = property.OrgId,
            Status = status,
            FiscalRegime = FiscalRegime.CedolareSecca,
            ContractType = LeaseContractType.Libero,
            TaxRegime = LeaseTaxRegime.CedolareSecca,
            StartDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            EndDate = new DateTime(2029, 12, 31, 0, 0, 0, DateTimeKind.Utc),
            MonthlyRent = 900m,
            Parties =
            [
                new Party
                {
                    Role = PartyRole.Landlord,
                    FirstName = "Mario",
                    LastName = "Rossi",
                    FiscalCode = "RSSMRA80A01H501U",
                    Citizenship = "IT",
                    ContactEmail = "landlord@example.test",
                },
                new Party
                {
                    Role = PartyRole.Tenant,
                    FirstName = "Giulia",
                    LastName = "Verdi",
                    FiscalCode = "VRDGLI85B02F205A",
                    Citizenship = "IT",
                    ContactEmail = tenantEmail,
                },
            ],
        };
        var document = new LeaseDocument
        {
            LeaseContractId = lease.Id,
            OrgId = lease.OrgId,
            Kind = LeaseDocumentKind.Contract,
            FileName = "contratto.pdf",
            StorageKey = $"tenant-area-it/{lease.Id:N}/contratto.pdf",
            UploadedBy = "auth0|landlord",
            SizeBytes = Pdf.Length,
        };
        await using (var content = new MemoryStream(Pdf))
            await storage.PutAsync(StorageBucket.Private, document.StorageKey, content, "application/pdf");
        db.LeaseContracts.Add(lease);
        db.LeaseDocuments.Add(document);
        await db.SaveChangesAsync();
        return (lease.Id, document.Id);
    }

    private async Task SetStatusAsync(Guid leaseId, LeaseStatus status)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        (await db.LeaseContracts.IgnoreQueryFilters().SingleAsync(l => l.Id == leaseId)).Status = status;
        await db.SaveChangesAsync();
    }
}
