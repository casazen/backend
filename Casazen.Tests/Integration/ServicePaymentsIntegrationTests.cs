using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Casazen.Tests.Unit.Email;
using Casazen.Tests.Unit.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// SP-15a on the real pipeline with the flag <c>Features:SupplierOnlinePayments</c> on: a request taken by a supplier whose Stripe
/// account is ready is paid inside CasaZen; completed, it gets its payment and the host its link; the payer opens the page and the
/// Stripe session anonymously with the token; the host has its own session; the supplier asks again and records an offline
/// payment; the host cannot mark an online request paid by hand. Stripe is the fake gateway and the emails are recorded. The JSON
/// contract (who sees what), who may call each endpoint (401, 403, 404) and the localized errors are proved here; the rules are in
/// the unit tests and the races in <see cref="ServicePaymentsPostgresTests"/>.
/// </summary>
[Collection(SupplierCatalogHostsCollection.Name)]
public class ServicePaymentsIntegrationTests(ServicePaymentsFactory factory) : IClassFixture<ServicePaymentsFactory>
{
    private const string Landlord = "LongTermLandlord";
    private const string SupplierAccount = "acct_supplier_http";

    // ─── The mode is decided at the take ───

    [Fact]
    public async Task Take_FlagOnAndAccountReady_IsOnline_InEveryResponseThatShowsTheRequest()
    {
        var world = await ReadyWorldAsync();
        var id = await factory.CreateRequestAsync(world);
        using var supplier = factory.Supplier(world);

        var taken = await supplier.PostAsync($"/api/service-requests/{id}/take", content: null);

        Assert.Equal(HttpStatusCode.OK, taken.StatusCode);
        Assert.Equal("Online", (await Json(taken)).GetProperty("paymentMode").GetString());
        var inbox = await supplier.GetFromJsonAsync<JsonElement>($"/api/supplier/inbox/{id}");
        Assert.Equal("Online", inbox.GetProperty("paymentMode").GetString());
        using var host = factory.Host(world);
        var hostView = await host.GetFromJsonAsync<JsonElement>($"/api/service-requests/{id}");
        Assert.Equal("Online", hostView.GetProperty("paymentMode").GetString());
    }

    [Fact]
    public async Task Take_AccountNotReady_IsManual()
    {
        var world = await ReadyWorldAsync(ready: false);
        var id = await factory.CreateRequestAsync(world);
        using var supplier = factory.Supplier(world);

        var taken = await supplier.PostAsync($"/api/service-requests/{id}/take", content: null);

        Assert.Equal("Manual", (await Json(taken)).GetProperty("paymentMode").GetString());
    }

    [Fact]
    public async Task ARequestOfTheSupplierThatIsNew_ShowsManualUntilItIsTaken()
    {
        var world = await ReadyWorldAsync();
        var id = await factory.CreateRequestAsync(world);
        using var supplier = factory.Supplier(world);

        var inbox = await supplier.GetFromJsonAsync<JsonElement>($"/api/supplier/inbox/{id}");

        Assert.Equal("Manual", inbox.GetProperty("paymentMode").GetString());
    }

    // ─── Complete: the payment and the link ───

    [Fact]
    public async Task Complete_OnlineRequest_CreatesThePaymentAndEmailsTheHostTheLink()
    {
        var (world, id) = await TakenOnlineAsync();
        using var supplier = factory.Supplier(world);

        var completed = await supplier.PostAsJsonAsync($"/api/service-requests/{id}/complete", new { finalAmountCents = 6_000 });

        Assert.Equal(HttpStatusCode.OK, completed.StatusCode);
        var body = await Json(completed);
        Assert.Equal("Completato", body.GetProperty("status").GetString());
        Assert.Equal("Online", body.GetProperty("paymentMode").GetString());
        var payment = await PaymentOfAsync(id);
        Assert.Equal(ServicePaymentStatus.Requested, payment.Status);
        Assert.Equal(6_000, payment.AmountCents);
        Assert.Equal(600, payment.ApplicationFeeCents);
        Assert.Equal(world.HostOrgId, payment.PayerOrgId);
        var email = Assert.Single(factory.Emails.RequestEmails(), e => ServicePaymentTestSupport.LinkOf(e.Content).PaymentId == payment.Id);
        Assert.Equal("owner@example.com", email.To);
        Assert.Contains("/service/pay/", email.Content.HtmlBody);
        // Nothing reaches Stripe before the payer opens the session.
        Assert.DoesNotContain(factory.Gateway.Created, c => c.Metadata[ServiceCharges.PaymentMetadataKey] == payment.Id.ToString());
    }

    [Fact]
    public async Task Complete_AmountAboveTheQuote_WaitsForTheHost_ThenTheConfirmationCreatesThePayment()
    {
        var (world, id) = await TakenOnlineAsync();
        using var supplier = factory.Supplier(world);
        using var host = factory.Host(world);

        var completed = await supplier.PostAsJsonAsync($"/api/service-requests/{id}/complete", new { finalAmountCents = 8_000 });
        Assert.True((await Json(completed)).GetProperty("price").GetProperty("needsCustomerConfirmation").GetBoolean());
        Assert.Empty(await PaymentsOfAsync(id));

        var confirmed = await host.PostAsync($"/api/service-requests/{id}/final-amount/confirm", content: null);
        var again = await host.PostAsync($"/api/service-requests/{id}/final-amount/confirm", content: null);

        Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
        Assert.False((await Json(confirmed)).GetProperty("price").GetProperty("needsCustomerConfirmation").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        var payment = Assert.Single(await PaymentsOfAsync(id));
        Assert.Equal(8_000, payment.AmountCents);
        Assert.Equal(800, payment.ApplicationFeeCents);
        Assert.Single(factory.Emails.RequestEmails(), e => ServicePaymentTestSupport.LinkOf(e.Content).PaymentId == payment.Id);
    }

    [Fact]
    public async Task ConfirmFinalAmount_WhoMayCall_AndWhenThereIsNothingToConfirm()
    {
        var (world, id) = await TakenOnlineAsync();
        using var supplier = factory.Supplier(world);
        await supplier.PostAsJsonAsync($"/api/service-requests/{id}/complete", new { finalAmountCents = 6_000 });
        var stranger = await ReadyWorldAsync();
        using var anonymous = factory.CreateClient();
        using var otherHost = factory.Host(stranger);
        using var host = factory.Host(world);

        var noToken = await anonymous.PostAsync($"/api/service-requests/{id}/final-amount/confirm", content: null);
        var asSupplier = await supplier.PostAsync($"/api/service-requests/{id}/final-amount/confirm", content: null);
        var otherOrg = await otherHost.PostAsync($"/api/service-requests/{id}/final-amount/confirm", content: null);
        var nothing = await host.PostAsync($"/api/service-requests/{id}/final-amount/confirm", content: null);

        Assert.Equal(HttpStatusCode.Unauthorized, noToken.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, asSupplier.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, otherOrg.StatusCode);
        Assert.Equal("service_request_not_found", (await Json(otherOrg)).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, nothing.StatusCode);
        Assert.Equal("service_request_no_confirmation_needed", (await Json(nothing)).GetProperty("code").GetString());
    }

    // ─── The page and the session of the payer (anonymous, the token is the only check) ───

    [Fact]
    public async Task PublicPayment_Lookup_ShowsThePageAndNothingAboutTheCommission()
    {
        var (_, _, paymentId, token) = await CompletedWithLinkAsync();
        using var anonymous = factory.CreateClient();

        var response = await anonymous.PostAsJsonAsync($"/api/public/service-payments/{paymentId}", new { token });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertNeverCached(response);
        var body = await Json(response);
        Assert.Equal(paymentId, body.GetProperty("id").GetGuid());
        Assert.Equal(6_000, body.GetProperty("amountCents").GetInt32());
        Assert.Equal("EUR", body.GetProperty("currency").GetString());
        Assert.Equal("Payable", body.GetProperty("state").GetString());
        Assert.False(body.GetProperty("lastAttemptFailed").GetBoolean());
        Assert.Equal(ServiceRequestWorlds.ServiceName, body.GetProperty("serviceName").GetString());
        Assert.Equal(ServiceRequestWorlds.PropertyName, body.GetProperty("propertyName").GetString());
        Assert.Equal("Fornitore SP-02", body.GetProperty("supplierName").GetString());
        Assert.Equal(6_000, Assert.Single(body.GetProperty("lines").EnumerateArray()).GetProperty("amountCents").GetInt32());
        Assert.NotEqual(JsonValueKind.Null, body.GetProperty("validUntil").ValueKind);

        // The payer never sees the commission, a name, an address, a contact, the Stripe account or the token.
        var names = PropertyNames(body);
        Assert.DoesNotContain(names, n => n.Contains("fee", StringComparison.OrdinalIgnoreCase)
            || n.Contains("commission", StringComparison.OrdinalIgnoreCase)
            || n.Contains("net", StringComparison.OrdinalIgnoreCase)
            || n.Contains("email", StringComparison.OrdinalIgnoreCase)
            || n.Contains("phone", StringComparison.OrdinalIgnoreCase)
            || n.Contains("address", StringComparison.OrdinalIgnoreCase)
            || n.Contains("account", StringComparison.OrdinalIgnoreCase)
            || n.Contains("token", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task PublicPayment_AWrongTokenAndAWrongId_AreTheSame404_InItalianAndEnglish()
    {
        var (_, _, paymentId, token) = await CompletedWithLinkAsync();
        using var anonymous = factory.CreateClient();

        var wrongToken = await anonymous.PostAsJsonAsync($"/api/public/service-payments/{paymentId}", new { token = token + "x" });
        var wrongId = await anonymous.PostAsJsonAsync($"/api/public/service-payments/{Guid.NewGuid()}", new { token });
        using var english = new HttpRequestMessage(HttpMethod.Post, $"/api/public/service-payments/{paymentId}") { Content = JsonContent.Create(new { token = "nope" }) };
        english.Headers.Add("Accept-Language", "en");
        var inEnglish = await anonymous.SendAsync(english);

        Assert.Equal(HttpStatusCode.NotFound, wrongToken.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, wrongId.StatusCode);
        var a = await Json(wrongToken);
        var b = await Json(wrongId);
        Assert.Equal("service_payment_link_invalid", a.GetProperty("code").GetString());
        // Nothing tells a payment that exists from one that does not: same code, same text.
        Assert.Equal(a.GetProperty("code").GetString(), b.GetProperty("code").GetString());
        Assert.Equal(a.GetProperty("detail").GetString(), b.GetProperty("detail").GetString());
        Assert.Contains("link di pagamento", a.GetProperty("detail").GetString());
        Assert.DoesNotContain("ServicePaymentLinkInvalid", a.GetProperty("detail").GetString());
        Assert.Contains("payment link", (await Json(inEnglish)).GetProperty("detail").GetString());
    }

    [Fact]
    public async Task PublicPayment_NoTokenInTheBody_Is400_ForBothEndpoints()
    {
        var (_, _, paymentId, _) = await CompletedWithLinkAsync();
        using var anonymous = factory.CreateClient();

        var lookup = await anonymous.PostAsJsonAsync($"/api/public/service-payments/{paymentId}", new { });
        var session = await anonymous.PostAsJsonAsync($"/api/public/service-payments/{paymentId}/payment-session", new { token = "" });

        Assert.Equal(HttpStatusCode.BadRequest, lookup.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, session.StatusCode);
    }

    [Fact]
    public async Task PublicPaymentSession_CreatesTheDirectChargeOnTheSupplierAccount_AndIsNeverCached()
    {
        var (_, _, paymentId, token) = await CompletedWithLinkAsync();
        using var anonymous = factory.CreateClient();

        var response = await anonymous.PostAsJsonAsync($"/api/public/service-payments/{paymentId}/payment-session", new { token });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertNeverCached(response);
        var body = await Json(response);
        Assert.Equal(paymentId, body.GetProperty("paymentId").GetGuid());
        Assert.Equal("pk_test_integration", body.GetProperty("publishableKey").GetString());
        Assert.Equal(SupplierAccount, body.GetProperty("stripeAccountId").GetString());
        Assert.Equal(6_000, body.GetProperty("amountCents").GetInt32());
        Assert.Equal("EUR", body.GetProperty("currency").GetString());
        var created = Assert.Single(factory.Gateway.Created, c => c.Metadata[ServiceCharges.PaymentMetadataKey] == paymentId.ToString());
        Assert.Equal(SupplierAccount, created.ConnectedAccountId);
        Assert.Equal(600, created.ApplicationFeeCents);
        Assert.Equal(6_000, created.AmountCents);
        var intent = factory.Gateway.Intents.Single(i => i.ClientSecret == body.GetProperty("clientSecret").GetString());
        Assert.Equal(600, intent.ApplicationFeeCents);
        // The payment keeps the PaymentIntent; opening it again reuses it.
        var again = await anonymous.PostAsJsonAsync($"/api/public/service-payments/{paymentId}/payment-session", new { token });
        Assert.Equal(body.GetProperty("clientSecret").GetString(), (await Json(again)).GetProperty("clientSecret").GetString());
        Assert.Single(factory.Gateway.Created, c => c.Metadata[ServiceCharges.PaymentMetadataKey] == paymentId.ToString());
    }

    [Fact]
    public async Task PublicPaymentSession_APaymentThatIsPaid_Is409_AndAWrongTokenIs404WithoutTouchingStripe()
    {
        var (_, id, paymentId, token) = await CompletedWithLinkAsync();
        using var anonymous = factory.CreateClient();
        var wrong = await anonymous.PostAsJsonAsync($"/api/public/service-payments/{paymentId}/payment-session", new { token = "x" + token });
        await ChangePaymentAsync(id, p =>
        {
            p.Status = ServicePaymentStatus.Paid;
            p.PaidAt = DateTime.UtcNow;
            p.PaidVia = ServicePaymentChannel.Stripe;
        });

        var paid = await anonymous.PostAsJsonAsync($"/api/public/service-payments/{paymentId}/payment-session", new { token });
        var page = await anonymous.PostAsJsonAsync($"/api/public/service-payments/{paymentId}", new { token });

        Assert.Equal(HttpStatusCode.NotFound, wrong.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, paid.StatusCode);
        var problem = await Json(paid);
        Assert.Equal("service_payment_not_payable", problem.GetProperty("code").GetString());
        Assert.Contains("già pagato", problem.GetProperty("detail").GetString());
        Assert.Equal("Paid", (await Json(page)).GetProperty("state").GetString());
        Assert.DoesNotContain(factory.Gateway.Created, c => c.Metadata[ServiceCharges.PaymentMetadataKey] == paymentId.ToString());
    }

    // ─── The host's own session ───

    [Fact]
    public async Task HostSession_TheHostThatPays_GetsASession_AnotherOrgAndAnonymousAndASupplierDoNot()
    {
        var (world, id, paymentId, _) = await CompletedWithLinkAsync();
        var stranger = await ReadyWorldAsync();
        using var host = factory.Host(world);
        using var otherHost = factory.Host(stranger);
        using var anonymous = factory.CreateClient();
        using var supplier = factory.Supplier(world);

        var session = await host.PostAsync($"/api/service-requests/{id}/payment-session", content: null);
        var otherOrg = await otherHost.PostAsync($"/api/service-requests/{id}/payment-session", content: null);
        var noToken = await anonymous.PostAsync($"/api/service-requests/{id}/payment-session", content: null);
        var asSupplier = await supplier.PostAsync($"/api/service-requests/{id}/payment-session", content: null);

        Assert.Equal(HttpStatusCode.OK, session.StatusCode);
        AssertNeverCached(session);
        var body = await Json(session);
        Assert.Equal(paymentId, body.GetProperty("paymentId").GetGuid());
        Assert.Equal(SupplierAccount, body.GetProperty("stripeAccountId").GetString());
        Assert.Equal(HttpStatusCode.NotFound, otherOrg.StatusCode);
        Assert.Equal("service_request_not_found", (await Json(otherOrg)).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.Unauthorized, noToken.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, asSupplier.StatusCode);
    }

    [Fact]
    public async Task HostSession_ARequestWithoutAPayment_Is404WithItsOwnCode()
    {
        var (world, id) = await TakenOnlineAsync();
        using var host = factory.Host(world);

        var response = await host.PostAsync($"/api/service-requests/{id}/payment-session", content: null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("service_payment_not_found", (await Json(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task LongRent_TheLandlordConfirmsTheAmountAndPaysWithItsOwnSession()
    {
        var world = await ReadyWorldAsync();
        using var landlord = factory.CreateAuthenticatedClient(world.HostUserId, Landlord);
        var created = await landlord.PostAsJsonAsync("/api/long-rent/service-requests", new
        {
            propertyId = world.PropertyId,
            supplierOrgId = world.SupplierOrgId,
            category = "cleaning",
            serviceListingId = world.ListingId,
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await Json(created)).GetProperty("id").GetGuid();
        using var supplier = factory.Supplier(world);
        await supplier.PostAsync($"/api/service-requests/{id}/take", content: null);
        await supplier.PostAsJsonAsync($"/api/service-requests/{id}/complete", new { finalAmountCents = 8_000 });

        var confirmed = await landlord.PostAsync($"/api/long-rent/service-requests/{id}/final-amount/confirm", content: null);
        var session = await landlord.PostAsync($"/api/long-rent/service-requests/{id}/payment-session", content: null);
        var markPaid = await landlord.PostAsync($"/api/long-rent/service-requests/{id}/mark-paid", content: null);

        Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
        Assert.Equal(HttpStatusCode.OK, session.StatusCode);
        Assert.Equal(8_000, (await Json(session)).GetProperty("amountCents").GetInt32());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, markPaid.StatusCode);
        Assert.Equal("service_request_online_payment", (await Json(markPaid)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task MarkPaid_ARequestPaidInsideCasaZen_Is422InItalianAndEnglish_AndTheRequestStaysCompleted()
    {
        var (world, id) = await CompletedAsync();
        using var host = factory.Host(world);
        using var english = new HttpRequestMessage(HttpMethod.Post, $"/api/service-requests/{id}/mark-paid");
        english.Headers.Add("Accept-Language", "en");

        var italian = await host.PostAsync($"/api/service-requests/{id}/mark-paid", content: null);
        var inEnglish = await host.SendAsync(english);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, italian.StatusCode);
        var it = await Json(italian);
        Assert.Equal("service_request_online_payment", it.GetProperty("code").GetString());
        Assert.Contains("link inviato per email", it.GetProperty("detail").GetString());
        Assert.Contains("link sent by email", (await Json(inEnglish)).GetProperty("detail").GetString());
        Assert.Equal(ServiceRequestStatus.Completato, (await factory.LoadRequestAsync(id)).Status);
    }

    // ─── The supplier asks again ───

    [Fact]
    public async Task PaymentRequest_AskedAgainTheSameDay_Is422WithTheHoursInTheMessage()
    {
        var (world, id) = await CompletedAsync();
        using var supplier = factory.Supplier(world);
        using var english = new HttpRequestMessage(HttpMethod.Post, $"/api/supplier/requests/{id}/payment-request");
        english.Headers.Add("Accept-Language", "en");

        var italian = await supplier.PostAsync($"/api/supplier/requests/{id}/payment-request", content: null);
        var inEnglish = await supplier.SendAsync(english);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, italian.StatusCode);
        var it = await Json(italian);
        Assert.Equal("service_payment_request_too_soon", it.GetProperty("code").GetString());
        Assert.Contains("24 ore", it.GetProperty("detail").GetString());
        Assert.Contains("24 hours", (await Json(inEnglish)).GetProperty("detail").GetString());
    }

    [Fact]
    public async Task PaymentRequest_AfterADay_ReturnsThePaymentOfTheSupplierWithTheSplit()
    {
        var (world, id) = await CompletedAsync();
        await ChangePaymentAsync(id, p => p.LastSentAt = DateTime.UtcNow.AddHours(-25));
        using var supplier = factory.Supplier(world);

        var response = await supplier.PostAsync($"/api/supplier/requests/{id}/payment-request", content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await Json(response);
        Assert.Equal("Requested", body.GetProperty("status").GetString());
        Assert.Equal("Completato", body.GetProperty("requestStatus").GetString());
        // The supplier sees its split: price gross, CasaZen commission, net.
        Assert.Equal(6_000, body.GetProperty("amountCents").GetInt32());
        Assert.Equal(10m, body.GetProperty("commissionPercent").GetDecimal());
        Assert.Equal(600, body.GetProperty("commissionCents").GetInt32());
        Assert.Equal(5_400, body.GetProperty("netCents").GetInt32());
        Assert.Equal("EUR", body.GetProperty("currency").GetString());
        Assert.Equal(2, body.GetProperty("sentCount").GetInt32());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("paidVia").ValueKind);
        var payment = await PaymentOfAsync(id);
        Assert.Single(factory.Emails.ReminderEmails(), e => ServicePaymentTestSupport.LinkOf(e.Content).PaymentId == payment.Id);
    }

    [Theory]
    [InlineData("payment-request")]
    [InlineData("payment/offline")]
    public async Task SupplierPaymentEndpoints_WhoMayCall(string route)
    {
        var (world, id) = await CompletedAsync();
        var stranger = await ReadyWorldAsync();
        var url = $"/api/supplier/requests/{id}/{route}";
        using var anonymous = factory.CreateClient();
        using var host = factory.Host(world);
        using var otherSupplier = factory.Supplier(stranger);
        var unlinked = factory.CreateAuthenticatedClient($"auth0|unlinked-{Guid.NewGuid():N}", "Supplier");

        var noToken = await anonymous.PostAsync(url, content: null);
        var asHost = await host.PostAsync(url, content: null);
        var anotherSupplier = await otherSupplier.PostAsJsonAsync(url, new { reason = "x" });
        var withoutLink = await unlinked.PostAsJsonAsync(url, new { reason = "x" });

        Assert.Equal(HttpStatusCode.Unauthorized, noToken.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, asHost.StatusCode);
        // A request of another supplier is forbidden (403), exactly like every other supplier action on it.
        Assert.Equal(HttpStatusCode.Forbidden, anotherSupplier.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, withoutLink.StatusCode);
        Assert.Equal(ServiceRequestStatus.Completato, (await factory.LoadRequestAsync(id)).Status);
        unlinked.Dispose();
    }

    // ─── Paid outside CasaZen: the traced exception ───

    [Fact]
    public async Task Offline_ARequestPaidInsideCasaZen_NeedsTheReason_ThenIsRecordedWithoutCommission()
    {
        var (world, id) = await CompletedAsync();
        using var supplier = factory.Supplier(world);

        var noReason = await supplier.PostAsync($"/api/supplier/requests/{id}/payment/offline", content: null);
        var recorded = await supplier.PostAsJsonAsync($"/api/supplier/requests/{id}/payment/offline", new { reason = "Il cliente ha pagato in contanti" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, noReason.StatusCode);
        Assert.Equal("service_payment_offline_reason_required", (await Json(noReason)).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.OK, recorded.StatusCode);
        var body = await Json(recorded);
        Assert.Equal("Paid", body.GetProperty("status").GetString());
        Assert.Equal("Offline", body.GetProperty("paidVia").GetString());
        Assert.Equal("Pagato", body.GetProperty("requestStatus").GetString());
        Assert.Equal(0, body.GetProperty("commissionCents").GetInt32());
        Assert.Equal(0m, body.GetProperty("commissionPercent").GetDecimal());
        Assert.Equal(6_000, body.GetProperty("netCents").GetInt32());
        Assert.Equal("Il cliente ha pagato in contanti", body.GetProperty("offlineNote").GetString());
        var request = await factory.LoadRequestAsync(id);
        Assert.Equal(ServiceRequestStatus.Pagato, request.Status);
        Assert.NotNull(request.PaidAt);
        var payments = await PaymentsOfAsync(id);
        Assert.Contains(payments, p => p is { Status: ServicePaymentStatus.Canceled });
        Assert.Single(payments, p => p.Status == ServicePaymentStatus.Paid);
        // The host is told, with the reason.
        var notice = Assert.Single(factory.Emails.Snapshot(), e => e.Template == Casazen.Infrastructure.Email.Templates.EmailTemplates.Names.ServicePaymentOfflineRecorded
            && e.Content.HtmlBody.Contains("Il cliente ha pagato in contanti", StringComparison.Ordinal));
        Assert.Equal("owner@example.com", notice.To);
    }

    [Fact]
    public async Task Offline_ARequestPaidByHand_NeedsNoBody()
    {
        var world = await ReadyWorldAsync(ready: false); // the account is not ready: the request is manual
        var id = await factory.CreateRequestAsync(world);
        using var supplier = factory.Supplier(world);
        await supplier.PostAsync($"/api/service-requests/{id}/take", content: null);
        await supplier.PostAsJsonAsync($"/api/service-requests/{id}/complete", new { finalAmountCents = 6_000 });

        var response = await supplier.PostAsync($"/api/supplier/requests/{id}/payment/offline", content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Offline", (await Json(response)).GetProperty("paidVia").GetString());
        Assert.Equal(ServiceRequestStatus.Pagato, (await factory.LoadRequestAsync(id)).Status);
    }

    [Fact]
    public async Task Offline_AReasonLongerThan500Characters_Is400AndNothingChanges()
    {
        var (world, id) = await CompletedAsync();
        using var supplier = factory.Supplier(world);

        var response = await supplier.PostAsJsonAsync($"/api/supplier/requests/{id}/payment/offline", new { reason = new string('x', 501) });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ServiceRequestStatus.Completato, (await factory.LoadRequestAsync(id)).Status);
        Assert.Single(await PaymentsOfAsync(id));
    }

    [Fact]
    public async Task Offline_APayerWhoOpenedTheSession_CannotPayAfterTheOfflineRecord()
    {
        var (world, id) = await CompletedAsync();
        var payment = await PaymentOfAsync(id);
        var token = TokenOf(payment.Id);
        using var anonymous = factory.CreateClient();
        using var supplier = factory.Supplier(world);
        await anonymous.PostAsJsonAsync($"/api/public/service-payments/{payment.Id}/payment-session", new { token });

        var recorded = await supplier.PostAsJsonAsync($"/api/supplier/requests/{id}/payment/offline", new { reason = "Pagato a mano" });
        var session = await anonymous.PostAsJsonAsync($"/api/public/service-payments/{payment.Id}/payment-session", new { token });
        var page = await anonymous.PostAsJsonAsync($"/api/public/service-payments/{payment.Id}", new { token });

        Assert.Equal(HttpStatusCode.OK, recorded.StatusCode);
        // The PaymentIntent the payer had was canceled on Stripe, and the withdrawn payment cannot be paid.
        Assert.Contains(factory.Gateway.Canceled, c => c.AccountId == SupplierAccount);
        Assert.Equal(HttpStatusCode.Conflict, session.StatusCode);
        Assert.Equal("service_payment_not_payable", (await Json(session)).GetProperty("code").GetString());
        Assert.Equal("Unavailable", (await Json(page)).GetProperty("state").GetString());
    }

    // ─── Helpers ───

    private async Task<ServiceRequestWorld> ReadyWorldAsync(bool ready = true)
    {
        var world = await ServiceRequestWorlds.SeedAsync(factory);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var org = await db.Orgs.SingleAsync(o => o.Id == world.SupplierOrgId);
        org.StripeConnectedAccountId = SupplierAccount;
        org.ConnectChargesEnabled = ready;
        org.ConnectPayoutsEnabled = ready;
        await db.SaveChangesAsync();
        return world;
    }

    /// <summary>A request taken by a supplier that can be paid: paid inside CasaZen.</summary>
    private async Task<(ServiceRequestWorld World, Guid Id)> TakenOnlineAsync()
    {
        var world = await ReadyWorldAsync();
        var id = await factory.CreateRequestAsync(world);
        using var supplier = factory.Supplier(world);
        var taken = await supplier.PostAsync($"/api/service-requests/{id}/take", content: null);
        Assert.Equal(HttpStatusCode.OK, taken.StatusCode);
        Assert.Equal("Online", (await Json(taken)).GetProperty("paymentMode").GetString());
        return (world, id);
    }

    /// <summary>A request completed at its agreed price: the payment exists and its link was sent.</summary>
    private async Task<(ServiceRequestWorld World, Guid Id)> CompletedAsync()
    {
        var (world, id) = await TakenOnlineAsync();
        using var supplier = factory.Supplier(world);
        var completed = await supplier.PostAsJsonAsync($"/api/service-requests/{id}/complete", new { finalAmountCents = 6_000 });
        Assert.Equal(HttpStatusCode.OK, completed.StatusCode);
        return (world, id);
    }

    private async Task<(ServiceRequestWorld World, Guid RequestId, Guid PaymentId, string Token)> CompletedWithLinkAsync()
    {
        var (world, id) = await CompletedAsync();
        var payment = await PaymentOfAsync(id);
        return (world, id, payment.Id, TokenOf(payment.Id));
    }

    /// <summary>The raw token of the link the host was emailed for <paramref name="paymentId"/> (the last one).</summary>
    private string TokenOf(Guid paymentId) =>
        factory.Emails.PaymentEmails()
            .Where(e => e.Template is "service-payment-request" or "service-payment-reminder")
            .Select(e => ServicePaymentTestSupport.LinkOf(e.Content))
            .Last(l => l.PaymentId == paymentId)
            .Token;

    private async Task<List<ServiceRequestPayment>> PaymentsOfAsync(Guid requestId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.ServiceRequestPayments.AsNoTracking().Where(p => p.ServiceRequestId == requestId).OrderBy(p => p.CreatedAt).ToListAsync();
    }

    private async Task<ServiceRequestPayment> PaymentOfAsync(Guid requestId) => Assert.Single(await PaymentsOfAsync(requestId));

    private async Task ChangePaymentAsync(Guid requestId, Action<ServiceRequestPayment> change)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var payment = await db.ServiceRequestPayments.SingleAsync(p => p.ServiceRequestId == requestId && p.Status != ServicePaymentStatus.Canceled);
        change(payment);
        await db.SaveChangesAsync();
    }

    /// <summary>The answer is private and never stored: it carries a state, or a client secret.</summary>
    private static void AssertNeverCached(HttpResponseMessage response)
    {
        Assert.True(response.Headers.CacheControl?.NoStore, "Cache-Control must say no-store");
        Assert.True(response.Headers.CacheControl?.Private, "Cache-Control must say private");
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>();

    private static IEnumerable<string> PropertyNames(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    yield return property.Name;
                    foreach (var nested in PropertyNames(property.Value))
                        yield return nested;
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    foreach (var nested in PropertyNames(item))
                        yield return nested;
                }

                break;
        }
    }
}

/// <summary>The integration host with the supplier flags on and the emails recorded (SP-15a): Stripe is the fake gateway.</summary>
public sealed class ServicePaymentsFactory : CasazenWebApplicationFactory
{
    internal RecordingEmailQueue Emails { get; } = new();

    internal FakeSupplierPaymentGateway Gateway => (FakeSupplierPaymentGateway)Services.GetRequiredService<ISupplierPaymentGateway>();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?> { ["Features:SupplierOnlinePayments"] = "true" }));
        builder.ConfigureTestServices(services =>
        {
            RemoveAllOf<IEmailQueue>(services);
            services.AddSingleton<IEmailQueue>(Emails);
        });
    }
}
