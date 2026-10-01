using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// PL-13 (A1-08, SB-AC8): billing profile with the e-invoice data, the EU VAT id no longer refused by a VIES stub, and
/// the admin queue of the e-invoices to issue by hand.
/// </summary>
public class BillingTaxIntegrationTests(CasazenWebApplicationFactory factory) : IClassFixture<CasazenWebApplicationFactory>
{
    [Fact]
    public async Task UpdateBillingProfile_EInvoiceData_IsStoredNormalizedAndReturned()
    {
        var (client, org) = await NewBillingAdminAsync();
        using var _ = client;

        var response = await client.PutAsJsonAsync("/api/billing/profile", new
        {
            billingCountry = "it",
            vatId = "IT 0123-4567.890",
            sdiRecipientCode = "abc 1234",
            pecEmail = "fatture@pec.example.it",
            fiscalCode = "rssmra80a01h501u",
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ABC1234", body.GetProperty("sdiRecipientCode").GetString());
        Assert.Equal("RSSMRA80A01H501U", body.GetProperty("fiscalCode").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("viesValidated").ValueKind);

        var stored = await ReadOrgAsync(org.Id);
        Assert.Equal("IT", stored.BillingCountry);
        Assert.Equal("IT01234567890", stored.VatId);
        Assert.Equal("ABC1234", stored.BillingSdiRecipientCode);
        Assert.Equal("fatture@pec.example.it", stored.BillingPecEmail);

        var subscription = await (await client.GetAsync("/api/billing/subscription")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ABC1234", subscription.GetProperty("sdiRecipientCode").GetString());
    }

    [Fact]
    public async Task UpdateBillingProfile_EInvoiceFieldsOmitted_KeepsThemAndEmptyClears()
    {
        // The web app (PL-12) sends only country and VAT id: saving its form must not wipe the e-invoice data.
        var (client, org) = await NewBillingAdminAsync();
        using var _ = client;
        Assert.Equal(
            HttpStatusCode.OK,
            (await client.PutAsJsonAsync("/api/billing/profile", new { billingCountry = "IT", sdiRecipientCode = "ABC1234", pecEmail = "a@pec.example.it" })).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync("/api/billing/profile", new { billingCountry = "IT" })).StatusCode);
        var kept = await ReadOrgAsync(org.Id);
        Assert.Equal("ABC1234", kept.BillingSdiRecipientCode);
        Assert.Equal("a@pec.example.it", kept.BillingPecEmail);

        Assert.Equal(
            HttpStatusCode.OK,
            (await client.PutAsJsonAsync("/api/billing/profile", new { billingCountry = "IT", sdiRecipientCode = "", pecEmail = " " })).StatusCode);
        var cleared = await ReadOrgAsync(org.Id);
        Assert.Null(cleared.BillingSdiRecipientCode);
        Assert.Null(cleared.BillingPecEmail);
    }

    [Theory]
    [InlineData("ABCDEFGH", null, null)]
    [InlineData(null, "not-an-email", null)]
    [InlineData(null, null, "RSSMRA80A01H501U99")]
    public async Task UpdateBillingProfile_InvalidEInvoiceData_Returns400AndStoresNothing(
        string? sdiRecipientCode,
        string? pecEmail,
        string? fiscalCode)
    {
        var (client, org) = await NewBillingAdminAsync();
        using var _ = client;

        var response = await client.PutAsJsonAsync(
            "/api/billing/profile",
            new { billingCountry = "IT", sdiRecipientCode, pecEmail, fiscalCode });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("validation_error", problem.GetProperty("code").GetString());
        Assert.Null((await ReadOrgAsync(org.Id)).BillingCountry);
    }

    [Fact]
    public async Task UpdateBillingProfile_EuVatId_IsAcceptedWithoutStubVerification()
    {
        // Before PL-13 a VAT id of another EU country was refused (VIES stub) unless Vies:StubMode accepted anything.
        var (client, org) = await NewBillingAdminAsync();
        using var _ = client;

        var response = await client.PutAsJsonAsync("/api/billing/profile", new { billingCountry = "DE", vatId = "DE123456789" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stored = await ReadOrgAsync(org.Id);
        Assert.Equal("DE123456789", stored.VatId);
        Assert.Null(stored.VatIdValidatedAt);
    }

    [Fact]
    public async Task PlatformInvoices_Admin_ListsManualQueueAndRecordsManualIssue()
    {
        var (hostClient, org) = await NewBillingAdminAsync();
        using var _ = hostClient;
        var invoiceId = await SeedInvoiceAsync(org.Id);

        Assert.Equal(HttpStatusCode.Forbidden, (await hostClient.GetAsync("/api/admin/platform-invoices")).StatusCode);

        using var admin = factory.CreateAuthenticatedClient($"auth0|pl13-admin-{Guid.NewGuid():N}", roles: "Admin");
        var page = await admin.GetFromJsonAsync<JsonElement>("/api/admin/platform-invoices?sdiStatus=manual_required&pageSize=100");
        var item = page.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("id").GetGuid() == invoiceId);
        Assert.Equal("manual_required", item.GetProperty("sdiStatus").GetString());

        var badStatus = await admin.GetAsync("/api/admin/platform-invoices?sdiStatus=sent");
        Assert.Equal(HttpStatusCode.BadRequest, badStatus.StatusCode);

        var empty = await admin.PostAsJsonAsync($"/api/admin/platform-invoices/{invoiceId}/sdi-manual-issued", new { reference = " " });
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);

        var issued = await admin.PostAsJsonAsync(
            $"/api/admin/platform-invoices/{invoiceId}/sdi-manual-issued",
            new { reference = "FT-2026-0001" });
        Assert.Equal(HttpStatusCode.OK, issued.StatusCode);
        var issuedBody = await issued.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("manual_issued", issuedBody.GetProperty("sdiStatus").GetString());
        Assert.Equal("FT-2026-0001", issuedBody.GetProperty("sdiTransmissionId").GetString());

        var again = await admin.PostAsJsonAsync(
            $"/api/admin/platform-invoices/{invoiceId}/sdi-manual-issued",
            new { reference = "FT-2026-0002" });
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        var problem = await again.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("platform_invoice_sdi_already_issued", problem.GetProperty("code").GetString());

        var unknown = await admin.PostAsJsonAsync(
            $"/api/admin/platform-invoices/{Guid.NewGuid()}/sdi-manual-issued",
            new { reference = "FT-2026-0003" });
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    private async Task<(HttpClient Client, OrgEntity Org)> NewBillingAdminAsync()
    {
        var userId = $"auth0|pl13-{Guid.NewGuid():N}";
        var org = await factory.SeedOrgForOwnerAsync(userId);
        return (factory.CreateAuthenticatedClient(userId: userId, roles: "PropertyOwner"), org);
    }

    private async Task<OrgEntity> ReadOrgAsync(Guid orgId)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Orgs.AsNoTracking().SingleAsync(o => o.Id == orgId);
    }

    private async Task<Guid> SeedInvoiceAsync(Guid orgId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var invoice = new PlatformInvoice
        {
            OrgId = orgId,
            StripeInvoiceId = $"in_test_{Guid.NewGuid():N}",
            AmountExVat = 79m,
            VatAmount = 17.38m,
            TotalAmount = 96.38m,
            VatTreatment = PlatformInvoiceVatTreatments.Taxed,
            SdiStatus = PlatformInvoiceSdiStatuses.ManualRequired,
            CreatedAt = DateTime.UtcNow,
        };
        db.PlatformInvoices.Add(invoice);
        await db.SaveChangesAsync();
        return invoice.Id;
    }
}
