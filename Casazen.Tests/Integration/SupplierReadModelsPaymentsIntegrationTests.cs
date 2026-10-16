using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// SP-04e on the real pipeline with the payments flag <c>Features:SupplierOnlinePayments</c> on (the host of
/// <see cref="ServicePaymentsIntegrationTests"/>): the summary of the payment of a completed job in the inbox (the split gross,
/// commission, net; where it stands; what the supplier may do), the late payments in the counters, and the money of the overview, all
/// from payments created by the real flow. With the flag off the same endpoints answer with no payment data
/// (<see cref="SupplierReadModelsIntegrationTests"/>).
/// </summary>
[Collection(SupplierCatalogHostsCollection.Name)]
public class SupplierReadModelsPaymentsIntegrationTests(ServicePaymentsFactory factory) : IClassFixture<ServicePaymentsFactory>
{
    private const string SupplierAccount = "acct_supplier_readmodels";

    [Fact]
    public async Task Payment_OfAJobAskedInsideCasaZen_ShowsTheSplitWhereItStandsAndWhatTheSupplierMayDo()
    {
        var (world, id) = await CompletedAsync();
        using var supplier = factory.Supplier(world);

        var item = (await supplier.GetFromJsonAsync<JsonElement>("/api/supplier/inbox?tab=da-incassare")).GetProperty("items")[0];
        var payment = item.GetProperty("payment");

        Assert.Equal(id, item.GetProperty("id").GetGuid());
        Assert.Equal("completed", item.GetProperty("phase").GetString());
        Assert.Equal("Online", item.GetProperty("paymentMode").GetString());
        Assert.NotEqual(JsonValueKind.Null, payment.GetProperty("id").ValueKind);
        Assert.Equal("Requested", payment.GetProperty("status").GetString());
        Assert.Equal(6000, payment.GetProperty("amountCents").GetInt32());
        Assert.Equal(10m, payment.GetProperty("commissionPercent").GetDecimal()); // the configured commission of the host of the tests
        Assert.Equal(600, payment.GetProperty("commissionCents").GetInt32());
        Assert.Equal(5400, payment.GetProperty("netCents").GetInt32());
        Assert.Equal(1, payment.GetProperty("sentCount").GetInt32());
        Assert.NotEqual(JsonValueKind.Null, payment.GetProperty("requestedAt").ValueKind);
        Assert.NotEqual(JsonValueKind.Null, payment.GetProperty("lastSentAt").ValueKind);
        Assert.False(payment.GetProperty("late").GetBoolean());
        Assert.False(payment.GetProperty("needsAmountConfirmation").GetBoolean());
        // The link went out a moment ago: a reminder is for tomorrow. Recording it as paid outside CasaZen is possible now.
        Assert.False(payment.GetProperty("canRequest").GetBoolean());
        Assert.True(payment.GetProperty("canRecordOffline").GetBoolean());
    }

    [Fact]
    public async Task CanRequest_AgreesWithThePaymentRequestEndpoint_BeforeAndAfterADay()
    {
        var (world, id) = await CompletedAsync();
        using var supplier = factory.Supplier(world);

        var tooSoon = await supplier.PostAsync($"/api/supplier/requests/{id}/payment-request", content: null);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, tooSoon.StatusCode);
        Assert.Equal("service_payment_request_too_soon", (await tooSoon.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        Assert.False(await CanRequestAsync(supplier, id));

        await ChangePaymentAsync(id, p => p.LastSentAt = DateTime.UtcNow.AddDays(-2));
        Assert.True(await CanRequestAsync(supplier, id));
        var reminder = await supplier.PostAsync($"/api/supplier/requests/{id}/payment-request", content: null);
        Assert.Equal(HttpStatusCode.OK, reminder.StatusCode);

        // The reminder is on its way: not again today, in the summary and in the endpoint.
        Assert.False(await CanRequestAsync(supplier, id));
        var again = await supplier.PostAsync($"/api/supplier/requests/{id}/payment-request", content: null);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, again.StatusCode);
        var payment = (await supplier.GetFromJsonAsync<JsonElement>($"/api/supplier/inbox/{id}")).GetProperty("payment");
        Assert.Equal(2, payment.GetProperty("sentCount").GetInt32());
    }

    [Fact]
    public async Task Counts_AndTheItem_TellALatePayment_UntilItIsPaid()
    {
        var (world, id) = await CompletedAsync();
        using var supplier = factory.Supplier(world);
        Assert.Equal(0, (await supplier.GetFromJsonAsync<JsonElement>("/api/supplier/inbox/counts")).GetProperty("paymentsLate").GetInt32());

        await ChangePaymentAsync(id, p => p.LateAt = DateTime.UtcNow.AddDays(-1));

        var counts = await supplier.GetFromJsonAsync<JsonElement>("/api/supplier/inbox/counts");
        var payment = (await supplier.GetFromJsonAsync<JsonElement>($"/api/supplier/inbox/{id}")).GetProperty("payment");
        Assert.Equal(1, counts.GetProperty("paymentsLate").GetInt32());
        Assert.Equal(1, counts.GetProperty("toCollect").GetInt32());
        Assert.True(payment.GetProperty("late").GetBoolean());

        var recorded = await supplier.PostAsJsonAsync($"/api/supplier/requests/{id}/payment/offline", new { reason = "Pagato in contanti" });
        Assert.Equal(HttpStatusCode.OK, recorded.StatusCode);

        var after = await supplier.GetFromJsonAsync<JsonElement>("/api/supplier/inbox/counts");
        var paid = (await supplier.GetFromJsonAsync<JsonElement>($"/api/supplier/inbox/{id}")).GetProperty("payment");
        Assert.Equal(0, after.GetProperty("paymentsLate").GetInt32());
        Assert.Equal(0, after.GetProperty("toCollect").GetInt32());
        Assert.Equal(1, after.GetProperty("archive").GetInt32());
        Assert.False(paid.GetProperty("late").GetBoolean());
        Assert.Equal("Paid", paid.GetProperty("status").GetString());
        Assert.Equal("Offline", paid.GetProperty("paidVia").GetString());
        Assert.Equal(0, paid.GetProperty("commissionCents").GetInt32());
    }

    [Fact]
    public async Task Overview_TalksAboutOnlinePaymentsOnlyOnceTheSupplierHasOne_AndSumsTheNetOfThePaidOnes()
    {
        var world = await ReadyWorldAsync();
        using var supplier = factory.Supplier(world);
        var before = await supplier.GetFromJsonAsync<JsonElement>("/api/supplier/dashboard/overview");
        Assert.False(before.GetProperty("onlinePayments").GetBoolean());

        var id = await factory.CreateRequestAsync(world);
        await supplier.PostAsync($"/api/service-requests/{id}/take", content: null);
        await supplier.PostAsJsonAsync($"/api/service-requests/{id}/complete", new { finalAmountCents = 6_000 });

        var asked = await supplier.GetFromJsonAsync<JsonElement>("/api/supplier/dashboard/overview");
        Assert.True(asked.GetProperty("onlinePayments").GetBoolean());
        Assert.Equal(0, asked.GetProperty("current").GetProperty("paidNetCents").GetInt64());
        // Still to be collected: the net of the payment that was asked for, not the gross the supplier declared.
        Assert.Equal(5400, asked.GetProperty("current").GetProperty("toCollectCents").GetInt64());
        Assert.Equal(1, asked.GetProperty("current").GetProperty("toCollectJobs").GetInt32());

        // The payer pays (the webhook is proved in SP-15b; here the state it leaves behind is written to the database).
        await ChangePaymentAsync(id, p =>
        {
            p.Status = ServicePaymentStatus.Paid;
            p.PaidAt = DateTime.UtcNow;
            p.PaidVia = ServicePaymentChannel.Stripe;
        });
        await factory.ChangeRequestAsync(id, r =>
        {
            r.Status = ServiceRequestStatus.Pagato;
            r.PaidAt = DateTime.UtcNow;
            r.PaidBy = ServiceRequestActorParty.Host;
        });

        var paid = (await supplier.GetFromJsonAsync<JsonElement>("/api/supplier/dashboard/overview")).GetProperty("current");
        Assert.Equal(5400, paid.GetProperty("paidNetCents").GetInt64());
        Assert.Equal(6000, paid.GetProperty("paidGrossCents").GetInt64());
        Assert.Equal(600, paid.GetProperty("commissionCents").GetInt64());
        Assert.Equal(1, paid.GetProperty("completedJobs").GetInt32());
        Assert.Equal(0, paid.GetProperty("toCollectJobs").GetInt32());
    }

    [Fact]
    public async Task Payment_NeverReachesAnotherSupplier()
    {
        var (world, id) = await CompletedAsync();
        var stranger = await ReadyWorldAsync();
        using var other = factory.Supplier(stranger);

        var inbox = await other.GetFromJsonAsync<JsonElement>("/api/supplier/inbox?status=all");
        var counts = await other.GetFromJsonAsync<JsonElement>("/api/supplier/inbox/counts");
        var overview = await other.GetFromJsonAsync<JsonElement>("/api/supplier/dashboard/overview");
        var detail = await other.GetAsync($"/api/supplier/inbox/{id}");

        Assert.Empty(inbox.GetProperty("items").EnumerateArray());
        Assert.Equal(0, counts.GetProperty("toCollect").GetInt32());
        Assert.False(overview.GetProperty("onlinePayments").GetBoolean());
        Assert.Equal(0, overview.GetProperty("current").GetProperty("toCollectCents").GetInt64());
        Assert.Equal(HttpStatusCode.NotFound, detail.StatusCode);
        Assert.NotEqual(stranger.SupplierOrgId, world.SupplierOrgId);
    }

    // ─── helpers ───

    /// <summary>A supplier whose Stripe account takes charges and payouts, with a host and a service.</summary>
    private async Task<ServiceRequestWorld> ReadyWorldAsync()
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

    /// <summary>A request paid inside CasaZen, taken and completed at 60 euro: the payment exists and its link was sent.</summary>
    private async Task<(ServiceRequestWorld World, Guid Id)> CompletedAsync()
    {
        var world = await ReadyWorldAsync();
        var id = await factory.CreateRequestAsync(world);
        using var supplier = factory.Supplier(world);
        var taken = await supplier.PostAsync($"/api/service-requests/{id}/take", content: null);
        Assert.Equal(HttpStatusCode.OK, taken.StatusCode);
        var completed = await supplier.PostAsJsonAsync($"/api/service-requests/{id}/complete", new { finalAmountCents = 6_000 });
        Assert.Equal(HttpStatusCode.OK, completed.StatusCode);
        return (world, id);
    }

    private static async Task<bool> CanRequestAsync(HttpClient supplier, Guid id) =>
        (await supplier.GetFromJsonAsync<JsonElement>($"/api/supplier/inbox/{id}")).GetProperty("payment").GetProperty("canRequest").GetBoolean();

    private async Task ChangePaymentAsync(Guid requestId, Action<ServiceRequestPayment> change)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var payment = await db.ServiceRequestPayments.SingleAsync(p => p.ServiceRequestId == requestId && p.Status != ServicePaymentStatus.Canceled);
        change(payment);
        await db.SaveChangesAsync();
    }
}
