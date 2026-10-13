using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Infrastructure.Data;
using Casazen.Tests.Unit.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// SP-15a with <c>Features:SupplierOnlinePayments</c> off (the default): the flag stops the <b>creation</b> of payments and of
/// payment requests, nothing else. A request is never taken as an online one, the supplier's "ask for the payment" is a 404 like a
/// route that does not exist, but a payment that already exists (money in flight) stays payable on the public page and in the
/// host's console, and the supplier can still record a payment received outside CasaZen.
/// </summary>
[Collection(SupplierCatalogHostsCollection.Name)]
public class ServicePaymentsFlagOffTests(CasazenWebApplicationFactory factory) : IClassFixture<CasazenWebApplicationFactory>
{
    private const string Token = "flag-off-token-0123456789abcdefghijklmnop";
    private const string SupplierAccount = "acct_flag_off";

    [Fact]
    public async Task Take_FlagOffEvenWithAReadyAccount_IsManual()
    {
        var world = await WorldAsync();
        var id = await factory.CreateRequestAsync(world);
        using var supplier = factory.Supplier(world);

        var taken = await supplier.PostAsync($"/api/service-requests/{id}/take", content: null);

        Assert.Equal("Manual", (await taken.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("paymentMode").GetString());
        Assert.DoesNotContain(Gateway.Created, c => c.Metadata[ServiceCharges.SupplierMetadataKey] == world.SupplierOrgId.ToString());
    }

    [Fact]
    public async Task PaymentRequest_FlagOff_Is404BeforeAuthentication_SoAnonymousAndSupplierSeeTheSame()
    {
        var (world, id, _) = await InFlightAsync();
        using var anonymous = factory.CreateClient();
        using var supplier = factory.Supplier(world);

        var asAnonymous = await anonymous.PostAsync($"/api/supplier/requests/{id}/payment-request", content: null);
        var asSupplier = await supplier.PostAsync($"/api/supplier/requests/{id}/payment-request", content: null);

        foreach (var response in new[] { asAnonymous, asSupplier })
        {
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal("not_found", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        }
    }

    [Fact]
    public async Task PublicPageAndSession_FlagOff_StayAvailableForAPaymentThatExists()
    {
        var (_, _, paymentId) = await InFlightAsync();
        using var anonymous = factory.CreateClient();

        var page = await anonymous.PostAsJsonAsync($"/api/public/service-payments/{paymentId}", new { token = Token });
        var session = await anonymous.PostAsJsonAsync($"/api/public/service-payments/{paymentId}/payment-session", new { token = Token });

        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Equal("Payable", (await page.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("state").GetString());
        Assert.Equal(HttpStatusCode.OK, session.StatusCode);
        var body = await session.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(SupplierAccount, body.GetProperty("stripeAccountId").GetString());
        var created = Assert.Single(Gateway.Created, c => c.Metadata[ServiceCharges.PaymentMetadataKey] == paymentId.ToString());
        Assert.Equal(600, created.ApplicationFeeCents);
    }

    [Fact]
    public async Task HostSession_FlagOff_StaysAvailableForAPaymentThatExists()
    {
        var (world, id, paymentId) = await InFlightAsync();
        using var host = factory.Host(world);

        var session = await host.PostAsync($"/api/service-requests/{id}/payment-session", content: null);

        Assert.Equal(HttpStatusCode.OK, session.StatusCode);
        Assert.Equal(paymentId, (await session.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("paymentId").GetGuid());
    }

    [Fact]
    public async Task Offline_FlagOff_StillWorks_ItIsWhatTheSupplierNeedsWhenOnlinePaymentsAreNotAvailable()
    {
        var (world, id, paymentId) = await InFlightAsync();
        using var supplier = factory.Supplier(world);

        var response = await supplier.PostAsJsonAsync($"/api/supplier/requests/{id}/payment/offline", new { reason = "Pagamenti online sospesi" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(ServiceRequestStatus.Pagato, (await factory.LoadRequestAsync(id)).Status);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(ServicePaymentStatus.Canceled, (await db.ServiceRequestPayments.AsNoTracking().SingleAsync(p => p.Id == paymentId)).Status);
    }

    [Fact]
    public async Task Complete_FlagOff_OfARequestThatWasTakenOnline_FallsBackToManual_AndTheHostMarksItPaid()
    {
        var world = await WorldAsync();
        var id = await factory.CreateRequestAsync(world);
        // Taken while the flag was on: the request is Online ...
        await factory.ChangeRequestAsync(id, r => { r.Status = ServiceRequestStatus.PresoInCarico; r.TakenAt = DateTime.UtcNow; r.PaymentMode = ServiceRequestPaymentMode.Online; });
        using var supplier = factory.Supplier(world);

        // ... and completed after it was switched off: nothing is stuck, no payment is created.
        var completed = await supplier.PostAsJsonAsync($"/api/service-requests/{id}/complete", new { finalAmountCents = 6_000 });

        Assert.Equal(HttpStatusCode.OK, completed.StatusCode);
        Assert.Equal("Manual", (await completed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("paymentMode").GetString());
        using var host = factory.Host(world);
        var paid = await host.PostAsync($"/api/service-requests/{id}/mark-paid", content: null);
        Assert.Equal(HttpStatusCode.OK, paid.StatusCode);
    }

    // ─── Helpers ───

    private FakeSupplierPaymentGateway Gateway => (FakeSupplierPaymentGateway)factory.Services.GetRequiredService<ISupplierPaymentGateway>();

    private async Task<ServiceRequestWorld> WorldAsync()
    {
        var world = await ServiceRequestWorlds.SeedAsync(factory);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var org = await db.Orgs.SingleAsync(o => o.Id == world.SupplierOrgId);
        org.StripeConnectedAccountId = SupplierAccount;
        org.ConnectChargesEnabled = true;
        org.ConnectPayoutsEnabled = true;
        await db.SaveChangesAsync();
        return world;
    }

    /// <summary>
    /// A completed request paid inside CasaZen with its payment waiting for the payer, as it was left by a time the flag was on:
    /// written straight to the tables, since with the flag off the API creates none.
    /// </summary>
    private async Task<(ServiceRequestWorld World, Guid RequestId, Guid PaymentId)> InFlightAsync()
    {
        var world = await WorldAsync();
        var id = await factory.CreateRequestAsync(world);
        var now = DateTime.UtcNow;
        await factory.ChangeRequestAsync(id, r =>
        {
            r.Status = ServiceRequestStatus.Completato;
            r.TakenAt = now;
            r.CompletedAt = now;
            r.FinalAmountCents = 6_000;
            r.PaymentMode = ServiceRequestPaymentMode.Online;
        });
        var payment = new ServiceRequestPayment
        {
            ServiceRequestId = id,
            SupplierOrgId = world.SupplierOrgId,
            PayerKind = ServicePayerKind.Host,
            PayerOrgId = world.HostOrgId,
            AmountCents = 6_000,
            CommissionPercent = 10m,
            ApplicationFeeCents = 600,
            NetCents = 5_400,
            Status = ServicePaymentStatus.Requested,
            PaymentTokenHash = CheckoutOutcomes.HashToken(Token),
            RequestedAt = now,
            LastSentAt = now,
            SentCount = 1,
            LineItemsJson = """[{"kind":"base","label":"Pulizia appartamento","amountCents":6000}]""",
        };
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.ServiceRequestPayments.Add(payment);
        await db.SaveChangesAsync();
        return (world, id, payment.Id);
    }
}
