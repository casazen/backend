using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Tests.Unit.Email;
using Casazen.Tests.Unit.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// SP-15b on the real pipeline: the platform admin's tools for the payment of the suppliers' services (list, detail, refund,
/// the commission of a supplier, the monthly commission export). Who may call each endpoint (401, 403, 404), the JSON contract
/// (money in cents, no personal data), the localized errors and the CSV are proved here; the rules are in the unit tests
/// (<c>SupplierPaymentAdminServiceTests</c>, <c>SupplierPaymentRefundTests</c>). Stripe is the fake gateway.
/// </summary>
[Collection(SupplierCatalogHostsCollection.Name)]
public class AdminSupplierPaymentsIntegrationTests(ServicePaymentsFactory factory) : IClassFixture<ServicePaymentsFactory>
{
    private const string SupplierAccount = "acct_supplier_admin_http";

    private HttpClient Admin() => factory.CreateAuthenticatedClient($"auth0|sp15b-admin-{Guid.NewGuid():N}", roles: "Admin");

    // ─── Who may call ───

    public static TheoryData<string, string> Endpoints => new()
    {
        { "GET", "/api/admin/supplier-payments" },
        { "GET", "/api/admin/supplier-payments/export?month=2026-01" },
        { "GET", "/api/admin/service-payments/00000000-0000-0000-0000-000000000001" },
        { "POST", "/api/admin/service-payments/00000000-0000-0000-0000-000000000001/refund" },
        { "GET", "/api/admin/suppliers/00000000-0000-0000-0000-000000000001/commission" },
        { "PUT", "/api/admin/suppliers/00000000-0000-0000-0000-000000000001/commission" },
    };

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task Endpoint_WithoutAToken_Is401(string method, string url)
    {
        using var anonymous = factory.CreateClient();

        var response = await anonymous.SendAsync(new HttpRequestMessage(new HttpMethod(method), url) { Content = Body(method) });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task Endpoint_AsAHostOrASupplier_Is403(string method, string url)
    {
        var world = await ServiceRequestWorlds.SeedAsync(factory);
        using var host = factory.Host(world);
        using var supplier = factory.Supplier(world);
        using var plainUser = factory.CreateAuthenticatedClient($"auth0|sp15b-plain-{Guid.NewGuid():N}");

        foreach (var client in new[] { host, supplier, plainUser })
        {
            var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), url) { Content = Body(method) });

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
    }

    private static HttpContent? Body(string method) =>
        method is "POST" or "PUT" ? JsonContent.Create(new { reason = "x", percent = 5 }) : null;

    // ─── The list ───

    [Fact]
    public async Task List_ShowsThePaymentWithItsMoney_AndNoPersonalDataOfThePayerOrTheSupplier()
    {
        var paid = await PaidAsync();
        using var admin = Admin();

        var response = await admin.GetAsync($"/api/admin/supplier-payments?supplierOrgId={paid.World.SupplierOrgId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertNeverCached(response);
        var page = await Json(response);
        Assert.Equal(1, page.GetProperty("totalCount").GetInt32());
        var item = page.GetProperty("items")[0];
        Assert.Equal(paid.PaymentId, item.GetProperty("id").GetGuid());
        Assert.Equal(paid.RequestId, item.GetProperty("serviceRequestId").GetGuid());
        Assert.Equal("Paid", item.GetProperty("status").GetString());
        Assert.Equal("Stripe", item.GetProperty("paidVia").GetString());
        Assert.Equal(6_000, item.GetProperty("amountCents").GetInt32());
        Assert.Equal(600, item.GetProperty("commissionCents").GetInt32());
        Assert.Equal(5_400, item.GetProperty("netCents").GetInt32());
        Assert.Equal(10m, item.GetProperty("commissionPercent").GetDecimal());
        Assert.Equal("EUR", item.GetProperty("currency").GetString());
        Assert.Equal(0, item.GetProperty("refundedCents").GetInt32());
        Assert.Equal(paid.IntentId, item.GetProperty("stripePaymentIntentId").GetString());
        Assert.Equal(SupplierAccount, item.GetProperty("connectedAccountId").GetString());
        // Ids and names only: never an email, a phone, an address, the token of the link or the secret of the session.
        Assert.DoesNotContain(
            PropertyNames(page),
            name => name.Contains("email", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("phone", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("address", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("token", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("secret", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task List_FiltersByStatus_AndIgnoresAStatusItDoesNotKnow()
    {
        var paid = await PaidAsync();
        using var admin = Admin();
        var supplier = $"supplierOrgId={paid.World.SupplierOrgId}";

        var refunded = await Json(await admin.GetAsync($"/api/admin/supplier-payments?{supplier}&status=Refunded"));
        var paidOnes = await Json(await admin.GetAsync($"/api/admin/supplier-payments?{supplier}&status=Paid"));
        var unknown = await Json(await admin.GetAsync($"/api/admin/supplier-payments?{supplier}&status=Nonsense"));
        var numeric = await Json(await admin.GetAsync($"/api/admin/supplier-payments?{supplier}&status=99"));

        Assert.Equal(0, refunded.GetProperty("totalCount").GetInt32());
        Assert.Equal(1, paidOnes.GetProperty("totalCount").GetInt32());
        // A status that is not one leaves the filter off, it does not fail and does not filter by nothing.
        Assert.Equal(1, unknown.GetProperty("totalCount").GetInt32());
        Assert.Equal(1, numeric.GetProperty("totalCount").GetInt32());
    }

    [Theory]
    [InlineData("page=0&pageSize=0")]
    [InlineData("page=-3&pageSize=100000")]
    public async Task List_OutOfRangePaging_IsClamped_NotA500(string paging)
    {
        using var admin = Admin();

        var response = await admin.GetAsync($"/api/admin/supplier-payments?{paging}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var page = await Json(response);
        Assert.Equal(1, page.GetProperty("page").GetInt32());
        Assert.InRange(page.GetProperty("pageSize").GetInt32(), 1, 100);
    }

    // ─── The detail and the refund ───

    private static string RefundUrl(Guid paymentId) => $"/api/admin/service-payments/{paymentId}/refund";

    [Fact]
    public async Task Refund_InPart_GoesToTheAccountOfTheSupplier_WithTheCommissionRefundedInProportion()
    {
        var paid = await PaidAsync();
        using var admin = Admin();

        var response = await admin.PostAsJsonAsync(RefundUrl(paid.PaymentId), new { amountCents = 1_500, reason = "Servizio non completato" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertNeverCached(response);
        var refund = await Json(response);
        Assert.Equal("Succeeded", refund.GetProperty("status").GetString());
        Assert.Equal(1_500, refund.GetProperty("amountCents").GetInt32());
        Assert.Equal(1, refund.GetProperty("sequence").GetInt32());
        Assert.Equal("Admin", refund.GetProperty("origin").GetString());
        Assert.Equal("Servizio non completato", refund.GetProperty("reason").GetString());
        // 10 % of 15 euro: the commission goes back with the refund, in proportion.
        Assert.Equal(150, refund.GetProperty("commissionRefundedCents").GetInt32());
        Assert.StartsWith("re_", refund.GetProperty("stripeRefundId").GetString(), StringComparison.Ordinal);

        var request = Assert.Single(factory.Gateway.RefundRequests, r => r.PaymentIntentId == paid.IntentId);
        Assert.Equal(SupplierAccount, request.ConnectedAccountId);
        Assert.True(request.RefundApplicationFee);
        Assert.Equal(1_500, request.AmountCents);
        Assert.Equal(ServiceCharges.RefundIdempotencyKey(paid.PaymentId, 1), request.IdempotencyKey);
        // The note of the admin is internal: it never reaches Stripe.
        Assert.DoesNotContain(request.Metadata.Values, value => value.Contains("Servizio", StringComparison.Ordinal));

        var payment = await PaymentAsync(paid.PaymentId);
        Assert.Equal(ServicePaymentStatus.PartiallyRefunded, payment.Status);
        Assert.Equal(1_500, payment.RefundedCents);
    }

    [Fact]
    public async Task Refund_WithoutAnAmount_RefundsWhatIsLeft_AndAfterThatThereIsNothingToRefund()
    {
        var paid = await PaidAsync();
        using var admin = Admin();
        await admin.PostAsJsonAsync(RefundUrl(paid.PaymentId), new { amountCents = 1_000 });

        var rest = await admin.PostAsJsonAsync(RefundUrl(paid.PaymentId), new { });
        var again = await admin.PostAsJsonAsync(RefundUrl(paid.PaymentId), new { });

        Assert.Equal(HttpStatusCode.OK, rest.StatusCode);
        var body = await Json(rest);
        Assert.Equal(5_000, body.GetProperty("amountCents").GetInt32());
        Assert.Equal(2, body.GetProperty("sequence").GetInt32());
        // 600 in all, 100 went back with the first refund: the rest brings the commission to the total.
        Assert.Equal(500, body.GetProperty("commissionRefundedCents").GetInt32());
        Assert.Equal(ServicePaymentStatus.Refunded, (await PaymentAsync(paid.PaymentId)).Status);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, again.StatusCode);
        Assert.Equal("service_payment_nothing_to_refund", (await Json(again)).GetProperty("code").GetString());
        Assert.Equal(2, factory.Gateway.RefundRequests.Count(r => r.PaymentIntentId == paid.IntentId));
    }

    [Fact]
    public async Task Detail_ShowsThePaymentAndItsRefunds_InTheOrderTheyWereMade()
    {
        var paid = await PaidAsync();
        using var admin = Admin();
        await admin.PostAsJsonAsync(RefundUrl(paid.PaymentId), new { amountCents = 1_000, reason = "primo" });
        await admin.PostAsJsonAsync(RefundUrl(paid.PaymentId), new { amountCents = 500, reason = "secondo" });

        var response = await admin.GetAsync($"/api/admin/service-payments/{paid.PaymentId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertNeverCached(response);
        var detail = await Json(response);
        var payment = detail.GetProperty("payment");
        Assert.Equal("PartiallyRefunded", payment.GetProperty("status").GetString());
        Assert.Equal(1_500, payment.GetProperty("refundedCents").GetInt32());
        Assert.Equal(150, payment.GetProperty("commissionRefundedCents").GetInt32());
        var refunds = detail.GetProperty("refunds").EnumerateArray().ToList();
        Assert.Equal(new[] { 1, 2 }, refunds.Select(r => r.GetProperty("sequence").GetInt32()).ToArray());
        Assert.Equal(new[] { "primo", "secondo" }, refunds.Select(r => r.GetProperty("reason").GetString()!).ToArray());
        Assert.Equal(new[] { 100, 50 }, refunds.Select(r => r.GetProperty("commissionRefundedCents").GetInt32()).ToArray());
    }

    [Fact]
    public async Task Detail_AndRefund_OfAPaymentThatDoesNotExist_Are404()
    {
        using var admin = Admin();

        var detail = await admin.GetAsync($"/api/admin/service-payments/{Guid.NewGuid()}");
        var refund = await admin.PostAsJsonAsync(RefundUrl(Guid.NewGuid()), new { amountCents = 100 });

        Assert.Equal(HttpStatusCode.NotFound, detail.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, refund.StatusCode);
        Assert.Equal("service_payment_not_found", (await Json(detail)).GetProperty("code").GetString());
        Assert.Equal("service_payment_not_found", (await Json(refund)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Refund_AboveWhatCanBeRefunded_Is422_InItalianAndEnglish_AndNothingIsSentToStripe()
    {
        var paid = await PaidAsync();
        using var admin = Admin();
        using var english = new HttpRequestMessage(HttpMethod.Post, RefundUrl(paid.PaymentId)) { Content = JsonContent.Create(new { amountCents = 6_001 }) };
        english.Headers.Add("Accept-Language", "en");

        var italian = await admin.PostAsJsonAsync(RefundUrl(paid.PaymentId), new { amountCents = 6_001 });
        var inEnglish = await admin.SendAsync(english);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, italian.StatusCode);
        var it = await Json(italian);
        Assert.Equal("service_payment_refund_amount_exceeds", it.GetProperty("code").GetString());
        Assert.Contains("60,00", it.GetProperty("detail").GetString());
        Assert.Contains("supera", it.GetProperty("detail").GetString());
        Assert.Contains("60.00", (await Json(inEnglish)).GetProperty("detail").GetString());
        Assert.DoesNotContain(factory.Gateway.RefundRequests, r => r.PaymentIntentId == paid.IntentId);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-100)]
    public async Task Refund_ANonPositiveAmount_Is400_AndNothingIsSentToStripe(int amountCents)
    {
        var paid = await PaidAsync();
        using var admin = Admin();

        var response = await admin.PostAsJsonAsync(RefundUrl(paid.PaymentId), new { amountCents });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain(factory.Gateway.RefundRequests, r => r.PaymentIntentId == paid.IntentId);
        Assert.Equal(ServicePaymentStatus.Paid, (await PaymentAsync(paid.PaymentId)).Status);
    }

    [Fact]
    public async Task Refund_ANoteLongerThan500Characters_Is400_AndNothingIsSentToStripe()
    {
        var paid = await PaidAsync();
        using var admin = Admin();
        var longNote = string.Concat(Enumerable.Repeat("x", 501));

        var response = await admin.PostAsJsonAsync(RefundUrl(paid.PaymentId), new { amountCents = 100, reason = longNote });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain(factory.Gateway.RefundRequests, r => r.PaymentIntentId == paid.IntentId);
    }

    [Fact]
    public async Task Refund_APaymentNotPaidYet_Is422NotRefundable()
    {
        var requested = await RequestedAsync();
        using var admin = Admin();

        var response = await admin.PostAsJsonAsync(RefundUrl(requested.PaymentId), new { amountCents = 100 });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await Json(response);
        Assert.Equal("service_payment_not_refundable", body.GetProperty("code").GetString());
        Assert.Contains("non risulta pagato online", body.GetProperty("detail").GetString());
        Assert.DoesNotContain(factory.Gateway.RefundRequests, r => r.Metadata.GetValueOrDefault("serviceRequestPaymentId") == requested.PaymentId.ToString());
    }

    [Fact]
    public async Task Refund_APaymentRecordedAsReceivedOutsideCasaZen_Is422_NothingWentThroughCasaZen()
    {
        var paid = await PaidAsync();
        await ChangePaymentAsync(paid.PaymentId, p => p.PaidVia = ServicePaymentChannel.Offline);
        using var admin = Admin();

        var response = await admin.PostAsJsonAsync(RefundUrl(paid.PaymentId), new { amountCents = 100 });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("service_payment_refund_offline", (await Json(response)).GetProperty("code").GetString());
        Assert.DoesNotContain(factory.Gateway.RefundRequests, r => r.PaymentIntentId == paid.IntentId);
    }

    // ─── The commission of a supplier ───

    private static string CommissionUrl(Guid orgId) => $"/api/admin/suppliers/{orgId}/commission";

    [Fact]
    public async Task Commission_Get_ShowsThePercentageOfThePlatform_WhenTheSupplierHasNoneOfItsOwn()
    {
        var world = await ServiceRequestWorlds.SeedAsync(factory);
        using var admin = Admin();

        var response = await admin.GetAsync(CommissionUrl(world.SupplierOrgId));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertNeverCached(response);
        var body = await Json(response);
        Assert.Equal(world.SupplierOrgId, body.GetProperty("supplierOrgId").GetGuid());
        Assert.Equal(10m, body.GetProperty("platformPercent").GetDecimal());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("overridePercent").ValueKind);
        Assert.False(body.GetProperty("overrideActive").GetBoolean());
        Assert.Equal(10m, body.GetProperty("effectivePercent").GetDecimal());
    }

    [Fact]
    public async Task Commission_Put_SetsTheOverride_TheNextPaymentUsesIt_AndTheAuditTrailKeepsTheChange()
    {
        var world = await ReadyWorldAsync();
        using var admin = Admin();

        var put = await admin.PutAsJsonAsync(CommissionUrl(world.SupplierOrgId), new { percent = 5, reason = "Accordo commerciale" });

        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var body = await Json(put);
        Assert.Equal(5m, body.GetProperty("overridePercent").GetDecimal());
        Assert.True(body.GetProperty("overrideActive").GetBoolean());
        Assert.Equal(5m, body.GetProperty("effectivePercent").GetDecimal());
        Assert.Equal(10m, body.GetProperty("platformPercent").GetDecimal());

        // A payment created afterwards is charged the override, and keeps it.
        var requested = await RequestedAsync(world);
        var payment = await PaymentAsync(requested.PaymentId);
        Assert.Equal(5m, payment.CommissionPercent);
        Assert.Equal(300, payment.ApplicationFeeCents);

        var audit = await admin.GetFromJsonAsync<JsonElement>($"/api/admin/suppliers/{world.SupplierOrgId}/audit");
        var entry = audit.EnumerateArray().Single(e => e.GetProperty("action").GetString() == "CommissionChanged");
        Assert.Contains("Accordo commerciale", entry.GetProperty("reason").GetString());
        Assert.StartsWith("auth0|sp15b-admin-", entry.GetProperty("actorUserId").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Commission_Put_AFreePeriod_IsAZeroUntilADate_AndNullRemovesTheOverride()
    {
        var world = await ServiceRequestWorlds.SeedAsync(factory);
        using var admin = Admin();
        var until = DateTime.UtcNow.AddDays(30);

        var free = await Json(await admin.PutAsJsonAsync(CommissionUrl(world.SupplierOrgId), new { percent = 0, until, reason = "Lancio" }));
        var removed = await Json(await admin.PutAsJsonAsync(CommissionUrl(world.SupplierOrgId), new { percent = (decimal?)null, reason = "Fine accordo" }));

        Assert.Equal(0m, free.GetProperty("overridePercent").GetDecimal());
        Assert.Equal(0m, free.GetProperty("effectivePercent").GetDecimal());
        Assert.True(free.GetProperty("overrideActive").GetBoolean());
        Assert.Equal(JsonValueKind.Null, removed.GetProperty("overridePercent").ValueKind);
        Assert.Equal(10m, removed.GetProperty("effectivePercent").GetDecimal());
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(50.01)]
    [InlineData(100)]
    [InlineData(10.123)]
    public async Task Commission_Put_APercentageOutsideTheRange_Is422_InItalianAndEnglish(double percent)
    {
        var world = await ServiceRequestWorlds.SeedAsync(factory);
        using var admin = Admin();
        using var english = new HttpRequestMessage(HttpMethod.Put, CommissionUrl(world.SupplierOrgId)) { Content = JsonContent.Create(new { percent, reason = "x" }) };
        english.Headers.Add("Accept-Language", "en");

        var italian = await admin.PutAsJsonAsync(CommissionUrl(world.SupplierOrgId), new { percent, reason = "x" });
        var inEnglish = await admin.SendAsync(english);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, italian.StatusCode);
        var it = await Json(italian);
        Assert.Equal("supplier_commission_invalid", it.GetProperty("code").GetString());
        Assert.Contains("percentuale da 0 a 50", it.GetProperty("detail").GetString());
        Assert.Contains("percentage from 0 to 50", (await Json(inEnglish)).GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Commission_Put_WithoutAReason_Is400_AndTheEndOfAPeriodNeedsAPercentageAndAFutureDate()
    {
        var world = await ServiceRequestWorlds.SeedAsync(factory);
        using var admin = Admin();

        var noReason = await admin.PutAsJsonAsync(CommissionUrl(world.SupplierOrgId), new { percent = 5 });
        var untilInThePast = await admin.PutAsJsonAsync(CommissionUrl(world.SupplierOrgId), new { percent = 5, until = DateTime.UtcNow.AddDays(-1), reason = "x" });
        var untilWithoutPercent = await admin.PutAsJsonAsync(CommissionUrl(world.SupplierOrgId), new { until = DateTime.UtcNow.AddDays(5), reason = "x" });

        Assert.Equal(HttpStatusCode.BadRequest, noReason.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, untilInThePast.StatusCode);
        Assert.Equal("supplier_commission_until_invalid", (await Json(untilInThePast)).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, untilWithoutPercent.StatusCode);
        // Nothing was saved by the three calls.
        var current = await Json(await admin.GetAsync(CommissionUrl(world.SupplierOrgId)));
        Assert.Equal(JsonValueKind.Null, current.GetProperty("overridePercent").ValueKind);
    }

    [Fact]
    public async Task Commission_OfAnOrgThatIsNotASupplier_Is404()
    {
        using var admin = Admin();

        var get = await admin.GetAsync(CommissionUrl(Guid.NewGuid()));
        var put = await admin.PutAsJsonAsync(CommissionUrl(Guid.NewGuid()), new { percent = 5, reason = "x" });

        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, put.StatusCode);
        Assert.Equal("supplier_not_found", (await Json(get)).GetProperty("code").GetString());
    }

    // ─── The export of the month ───

    [Fact]
    public async Task Export_IsACsvWithAByteOrderMark_WithOneLineForEachPaymentAndRefundOfTheMonth()
    {
        var paid = await PaidAsync();
        using var admin = Admin();
        await admin.PostAsJsonAsync(RefundUrl(paid.PaymentId), new { amountCents = 1_500 });
        var payment = await PaymentAsync(paid.PaymentId);
        var month = RomeCalendar.DateInRome(payment.PaidAt!.Value).ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture);

        var response = await admin.GetAsync($"/api/admin/supplier-payments/export?month={month}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertNeverCached(response);
        Assert.Equal("text/csv", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("utf-8", response.Content.Headers.ContentType.CharSet);
        Assert.Equal($"commissioni-{month}.csv", response.Content.Headers.ContentDisposition!.FileName!.Replace("\"", string.Empty));
        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
        var lines = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(string.Join(",", CommissionExportCsv.Columns), lines[0]);

        // The payment: gross 60 euro, 10 %, commission 6 euro, net 54, and the P.IVA of the supplier. No VAT: it is open.
        var paymentLine = Assert.Single(lines, l => l.StartsWith("payment,", StringComparison.Ordinal) && l.Contains(paid.PaymentId.ToString("D"), StringComparison.Ordinal)).Split(",");
        Assert.Equal(CommissionExportCsv.Columns.Count, paymentLine.Length);
        Assert.Equal(new[] { "6000", "10", "600", string.Empty, "5400" }, paymentLine[10..]);
        Assert.Equal(paid.World.SupplierOrgId.ToString("D"), paymentLine[4]);
        // The refund: negative amounts, the part of the commission that went back with it.
        var refundLine = Assert.Single(lines, l => l.StartsWith("refund,", StringComparison.Ordinal) && l.Contains(paid.PaymentId.ToString("D"), StringComparison.Ordinal)).Split(",");
        Assert.Equal(new[] { "-1500", "10", "-150", string.Empty, "-1350" }, refundLine[10..]);
    }

    [Theory]
    [InlineData("2026-13")]
    [InlineData("2026-00")]
    [InlineData("202610")]
    [InlineData("2026-1")]
    [InlineData("26-10")]
    [InlineData("1999-12")]
    [InlineData("abcd-ef")]
    [InlineData("")]
    public async Task Export_AMonthThatIsNotYyyyMm_Is422_InItalianAndEnglish(string month)
    {
        using var admin = Admin();
        using var english = new HttpRequestMessage(HttpMethod.Get, $"/api/admin/supplier-payments/export?month={month}");
        english.Headers.Add("Accept-Language", "en");

        var italian = await admin.GetAsync($"/api/admin/supplier-payments/export?month={month}");
        var inEnglish = await admin.SendAsync(english);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, italian.StatusCode);
        var it = await Json(italian);
        Assert.Equal("service_payment_export_month_invalid", it.GetProperty("code").GetString());
        Assert.Contains("aaaa-mm", it.GetProperty("detail").GetString());
        Assert.Contains("yyyy-mm", (await Json(inEnglish)).GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Export_WithoutAMonth_Or_AMonthInTheFuture_Is422()
    {
        using var admin = Admin();
        var future = RomeCalendar.DateInRome(DateTime.UtcNow).AddMonths(2).ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture);

        var none = await admin.GetAsync("/api/admin/supplier-payments/export");
        var later = await admin.GetAsync($"/api/admin/supplier-payments/export?month={future}");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, none.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, later.StatusCode);
        Assert.Equal("service_payment_export_month_invalid", (await Json(later)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Export_AMonthWithNothingInIt_IsJustTheHeader()
    {
        using var admin = Admin();

        var response = await admin.GetAsync("/api/admin/supplier-payments/export?month=2020-01");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal(string.Join(",", CommissionExportCsv.Columns) + "\r\n", Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3));
    }

    // ─── Helpers ───

    private sealed record RequestedPayment(ServiceRequestWorld World, Guid RequestId, Guid PaymentId);

    private sealed record PaidPayment(ServiceRequestWorld World, Guid RequestId, Guid PaymentId, string IntentId);

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

    /// <summary>A payment asked for: the request was taken and completed inside CasaZen (60 euro) and the link was emailed to the host.</summary>
    private async Task<RequestedPayment> RequestedAsync(ServiceRequestWorld? existing = null)
    {
        var world = existing ?? await ReadyWorldAsync();
        var id = await factory.CreateRequestAsync(world);
        using var supplier = factory.Supplier(world);
        (await supplier.PostAsync($"/api/service-requests/{id}/take", content: null)).EnsureSuccessStatusCode();
        (await supplier.PostAsJsonAsync($"/api/service-requests/{id}/complete", new { finalAmountCents = 6_000 })).EnsureSuccessStatusCode();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var payment = await db.ServiceRequestPayments.AsNoTracking().SingleAsync(p => p.ServiceRequestId == id);
        return new RequestedPayment(world, id, payment.Id);
    }

    /// <summary>A payment paid: the payer opened the session, the PaymentIntent succeeded and the webhook was applied.</summary>
    private async Task<PaidPayment> PaidAsync()
    {
        var requested = await RequestedAsync();
        var token = factory.Emails.PaymentEmails()
            .Where(e => e.Template is "service-payment-request" or "service-payment-reminder")
            .Select(e => ServicePaymentTestSupport.LinkOf(e.Content))
            .Last(link => link.PaymentId == requested.PaymentId)
            .Token;
        using var anonymous = factory.CreateClient();
        (await anonymous.PostAsJsonAsync($"/api/public/service-payments/{requested.PaymentId}/payment-session", new { token })).EnsureSuccessStatusCode();

        var payment = await PaymentAsync(requested.PaymentId);
        factory.Gateway.SetStatus(payment.StripePaymentIntentId!, "succeeded");
        var intent = factory.Gateway.Intent(payment.StripePaymentIntentId!);
        await using var scope = factory.Services.CreateAsyncScope();
        var webhook = scope.ServiceProvider.GetRequiredService<ISupplierPaymentWebhookService>();
        var notices = await webhook.ApplyPaymentIntentEventAsync(new ServicePaymentIntentEvent(
            $"evt_{Guid.NewGuid():N}",
            "payment_intent.succeeded",
            intent.Id,
            intent.ConnectedAccountId,
            payment.Id,
            intent.AmountCents,
            intent.AmountReceivedCents ?? intent.AmountCents,
            intent.Currency,
            intent.ApplicationFeeCents,
            null,
            null));
        await webhook.CompleteAsync(notices);

        Assert.Equal(ServicePaymentStatus.Paid, (await PaymentAsync(payment.Id)).Status);
        return new PaidPayment(requested.World, requested.RequestId, requested.PaymentId, intent.Id);
    }

    private async Task<ServiceRequestPayment> PaymentAsync(Guid paymentId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.ServiceRequestPayments.AsNoTracking().SingleAsync(p => p.Id == paymentId);
    }

    private async Task ChangePaymentAsync(Guid paymentId, Action<ServiceRequestPayment> change)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var payment = await db.ServiceRequestPayments.SingleAsync(p => p.Id == paymentId);
        change(payment);
        await db.SaveChangesAsync();
    }

    /// <summary>The answer describes money and names suppliers: it is private and never stored.</summary>
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
