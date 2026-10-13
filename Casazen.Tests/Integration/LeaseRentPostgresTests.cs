using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Email.Templates;
using Casazen.Infrastructure.External;
using Casazen.Tests.Integration.Postgres;
using Casazen.Tests.Unit;
using Casazen.Tests.Unit.Email;
using Casazen.Web.BackgroundJobs;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Stripe;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// LT-06 (#269, audit A7-07) on real PostgreSQL: the recurring rent of a signed lease. The schedule is generated from the
/// lease; the daily job emails the tenant a personal link for the installments coming due; the tenant pays on the
/// landlord's Stripe connected account (one PaymentIntent per installment, reused); the installment is paid only when the
/// webhook says <c>succeeded</c>; a payment in flight that fails emails a new link; the landlord can declare an offline
/// payment (the payable PaymentIntent is canceled first); disabling the schedule cancels what is unpaid.
/// </summary>
public class LeaseRentPostgresTests(LeaseRentPostgresTests.RentFactory factory) : IClassFixture<LeaseRentPostgresTests.RentFactory>
{
    /// <summary>10:00 in Rome on 25 September 2026: after the stipula (20/8) and the lease start (1/9).</summary>
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 8, 0, 0, TimeSpan.Zero);

    private const string TenantEmail = "giulia@example.com";
    private const string LandlordEmail = "mario@example.com";

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    [PostgresFact]
    public async Task ConfigureSchedule_SignedLease_GeneratesTheInstallmentsFromTheLease()
    {
        var seeded = await SignedLeaseAsync("configure");

        var response = await seeded.Client.PutAsJsonAsync($"{RentPath(seeded)}/schedule", new { cadence = "Monthly", billingDayOfMonth = 5 });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var ledger = await ReadJson(response);
        Assert.True(ledger.GetProperty("canConfigure").GetBoolean());
        Assert.Equal(1200m, ledger.GetProperty("schedule").GetProperty("amount").GetDecimal());
        var installments = ledger.GetProperty("installments").EnumerateArray().ToList();
        Assert.Equal(48, installments.Count);
        Assert.Equal("2026-09-01", installments[0].GetProperty("periodStart").GetString());
        Assert.Equal("2026-09-30", installments[0].GetProperty("periodEnd").GetString());
        Assert.Equal("2026-09-05", installments[0].GetProperty("dueDate").GetString());
        Assert.All(installments, i => Assert.Equal("Scheduled", i.GetProperty("status").GetString()));
        // Due on 5 September, not paid on the 25th: overdue, computed on read.
        Assert.True(installments[0].GetProperty("isOverdue").GetBoolean());
        Assert.False(installments[1].GetProperty("isOverdue").GetBoolean());
        Assert.Equal(JsonValueKind.Null, ledger.GetProperty("partialFinalPeriod").ValueKind);

        // Quarterly: the amount defaults to three months of rent; nothing was paid, so the cadence can change.
        var quarterly = await ReadJson(await seeded.Client.PutAsJsonAsync($"{RentPath(seeded)}/schedule", new { cadence = "Quarterly", billingDayOfMonth = 5 }));
        var quarters = quarterly.GetProperty("installments").EnumerateArray().ToList();
        Assert.Equal(16, quarters.Count);
        Assert.All(quarters, q => Assert.Equal(3600m, q.GetProperty("amount").GetDecimal()));
        Assert.Equal(16, await CountInstallmentsAsync(seeded.LeaseId));
    }

    [PostgresFact]
    public async Task ConfigureSchedule_LeaseNotSigned_Returns422AndNoInstallment()
    {
        var seeded = await DraftLeaseAsync("draft");

        var response = await seeded.Client.PutAsJsonAsync($"{RentPath(seeded)}/schedule", new { cadence = "Monthly" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(RentBillingErrorCodes.LeaseNotSigned, (await ReadJson(response)).GetProperty("code").GetString());
        Assert.Equal(0, await CountInstallmentsAsync(seeded.LeaseId));
    }

    [PostgresFact]
    public async Task RentEndpoints_OtherOrgOrAnonymous_NotFoundOrUnauthorized()
    {
        var seeded = await SignedLeaseAsync("access");
        var stranger = $"auth0|lt06-stranger-{Guid.NewGuid():N}";
        await factory.SeedOrgForOwnerAsync(stranger);
        using var strangerClient = factory.CreateAuthenticatedClient(stranger, "LongTermLandlord");
        using var anonymous = factory.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound, (await strangerClient.GetAsync(RentPath(seeded))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await strangerClient.PutAsJsonAsync($"{RentPath(seeded)}/schedule", new { cadence = "Monthly" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(RentPath(seeded))).StatusCode);
        Assert.Equal(0, await CountInstallmentsAsync(seeded.LeaseId));
    }

    [PostgresFact]
    public async Task RunCollection_InstallmentComingDue_EmailsTheTenantOnceAndTheWebhookMarksItPaid()
    {
        var seeded = await SignedLeaseAsync("collect", connect: true);
        (await seeded.Client.PutAsJsonAsync($"{RentPath(seeded)}/schedule", new { cadence = "Monthly", billingDayOfMonth = 5 })).EnsureSuccessStatusCode();

        // 25/9: October is due on 5/10, beyond the lead days; September was already due when generated (landlord's call).
        await RunJobAsync();
        Assert.Empty(EmailsTo(TenantEmail, seeded));

        factory.Clock.SetUtcNow(new DateTimeOffset(2026, 10, 1, 6, 0, 0, TimeSpan.Zero));
        await RunJobAsync();
        await RunJobAsync(); // a second run (retry, manual trigger) never emails twice

        var request = Assert.Single(EmailsTo(TenantEmail, seeded));
        Assert.Equal(EmailTemplates.Names.RentPaymentRequest, request.Template);
        var (installmentId, token) = LinkOf(request.Content);
        var october = await InstallmentAsync(installmentId);
        Assert.Equal(new DateOnly(2026, 10, 1), october.PeriodStart);
        Assert.NotNull(october.PaymentRequestedAt);

        using var tenant = factory.CreateClient();
        var page = await ReadJson(await tenant.PostAsJsonAsync($"/api/public/rent-payments/{installmentId}", new { token }));
        Assert.Equal("Payable", page.GetProperty("state").GetString());
        Assert.Equal(1200m, page.GetProperty("amount").GetDecimal());
        Assert.Equal(seeded.OrgName, page.GetProperty("landlordName").GetString());

        var first = await ReadJson(await tenant.PostAsJsonAsync($"/api/public/rent-payments/{installmentId}/payment-session", new { token }));
        var second = await ReadJson(await tenant.PostAsJsonAsync($"/api/public/rent-payments/{installmentId}/payment-session", new { token }));
        Assert.Equal(seeded.Account, first.GetProperty("stripeAccountId").GetString());
        var paymentIntentId = (await InstallmentAsync(installmentId)).StripePaymentIntentId!;
        // The second session reuses the payable PaymentIntent (read back from Stripe): one creation only.
        Assert.StartsWith(paymentIntentId, second.GetProperty("clientSecret").GetString(), StringComparison.Ordinal);
        var created = Assert.Single(
            factory.Stripe.IdempotentPaymentIntents, p => p.Key.StartsWith($"rent-charge:{installmentId:N}:", StringComparison.Ordinal));
        Assert.Equal(paymentIntentId, created.Value.Id);
        Assert.Equal(created.Value.ClientSecret, first.GetProperty("clientSecret").GetString());
        Assert.Equal($"rent-charge:{installmentId:N}:1", created.Key);
        Assert.Equal(120_000, created.Value.Amount);
        Assert.Equal(RentCharges.Kind, created.Value.Metadata["kind"]);

        var paid = RentEvent("payment_intent.succeeded", paymentIntentId, installmentId, seeded.Account);
        await HandleWebhookAsync(paid);
        await HandleWebhookAsync(paid); // duplicate delivery

        var settled = await InstallmentAsync(installmentId);
        Assert.Equal(RentLedgerStatus.Paid, settled.Status);
        Assert.Equal(RentPaymentChannel.Stripe, settled.PaidVia);
        Assert.Equal(new DateOnly(2026, 10, 1), settled.PaidOn);
        Assert.Single(EmailsTo(LandlordEmail, seeded), e => e.Template == EmailTemplates.Names.RentPaymentReceived);
        var after = await ReadJson(await tenant.PostAsJsonAsync($"/api/public/rent-payments/{installmentId}", new { token }));
        Assert.Equal("Paid", after.GetProperty("state").GetString());
        var again = await tenant.PostAsJsonAsync($"/api/public/rent-payments/{installmentId}/payment-session", new { token });
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
    }

    [PostgresFact]
    public async Task MarkPaidOffline_WithAPayablePaymentIntent_CancelsItFirstAndRecordsTheOfflinePayment()
    {
        var seeded = await SignedLeaseAsync("offline", connect: true);
        var installmentId = await FirstInstallmentIdAsync(seeded);
        var token = await SendPaymentRequestAsync(seeded, installmentId);
        using var tenant = factory.CreateClient();
        (await tenant.PostAsJsonAsync($"/api/public/rent-payments/{installmentId}/payment-session", new { token })).EnsureSuccessStatusCode();
        var paymentIntentId = (await InstallmentAsync(installmentId)).StripePaymentIntentId!;

        var response = await seeded.Client.PostAsJsonAsync(
            $"{RentPath(seeded)}/installments/{installmentId}/mark-paid", new { paidOn = "2026-09-24", note = "Bonifico" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var installment = await ReadJson(response);
        Assert.Equal("Paid", installment.GetProperty("status").GetString());
        Assert.Equal("Offline", installment.GetProperty("paidVia").GetString());
        Assert.Equal("2026-09-24", installment.GetProperty("paidOn").GetString());
        Assert.Contains(
            FakeStripeService.CancelledIntents,
            c => c.IntentId == paymentIntentId && c.AccountId == seeded.Account &&
                 c.IdempotencyKey == $"rent-charge-cancel:{installmentId:N}:{paymentIntentId}");
        var stored = await InstallmentAsync(installmentId);
        Assert.StartsWith("auth0|lt06-offline-", stored.MarkedPaidByUserId, StringComparison.Ordinal);

        var twice = await seeded.Client.PostAsJsonAsync(
            $"{RentPath(seeded)}/installments/{installmentId}/mark-paid", new { paidOn = "2026-09-24" });
        Assert.Equal(HttpStatusCode.Conflict, twice.StatusCode);
        // The link no longer opens a payment.
        var page = await ReadJson(await tenant.PostAsJsonAsync($"/api/public/rent-payments/{installmentId}", new { token }));
        Assert.Equal("Paid", page.GetProperty("state").GetString());
    }

    [PostgresFact]
    public async Task MarkPaidOffline_DateInTheFuture_Returns422()
    {
        var seeded = await SignedLeaseAsync("future");
        var installmentId = await FirstInstallmentIdAsync(seeded);

        var response = await seeded.Client.PostAsJsonAsync(
            $"{RentPath(seeded)}/installments/{installmentId}/mark-paid", new { paidOn = "2026-09-26" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(RentLedgerStatus.Scheduled, (await InstallmentAsync(installmentId)).Status);
    }

    [PostgresFact]
    public async Task Webhook_PaymentInFlightThenFailed_FailsTheInstallmentAndEmailsTheTenantANewLink()
    {
        var seeded = await SignedLeaseAsync("sepa", connect: true);
        var installmentId = await FirstInstallmentIdAsync(seeded);
        var token = await SendPaymentRequestAsync(seeded, installmentId);
        using var tenant = factory.CreateClient();
        (await tenant.PostAsJsonAsync($"/api/public/rent-payments/{installmentId}/payment-session", new { token })).EnsureSuccessStatusCode();
        var paymentIntentId = (await InstallmentAsync(installmentId)).StripePaymentIntentId!;

        await HandleWebhookAsync(RentEvent("payment_intent.processing", paymentIntentId, installmentId, seeded.Account));

        Assert.Equal(RentLedgerStatus.Processing, (await InstallmentAsync(installmentId)).Status);
        var whileInFlight = await seeded.Client.PostAsJsonAsync(
            $"{RentPath(seeded)}/installments/{installmentId}/mark-paid", new { paidOn = "2026-09-24" });
        Assert.Equal(HttpStatusCode.Conflict, whileInFlight.StatusCode);
        Assert.Equal(RentBillingErrorCodes.InstallmentInFlight, (await ReadJson(whileInFlight)).GetProperty("code").GetString());

        await HandleWebhookAsync(RentEvent("payment_intent.payment_failed", paymentIntentId, installmentId, seeded.Account, "debit_not_authorized"));

        var failed = await InstallmentAsync(installmentId);
        Assert.Equal(RentLedgerStatus.Failed, failed.Status);
        Assert.Null(failed.PaidOn);
        Assert.Equal("debit_not_authorized", failed.FailureCode);
        var retry = Assert.Single(EmailsTo(TenantEmail, seeded), e => e.Template == EmailTemplates.Names.RentPaymentFailed);
        var (_, newToken) = LinkOf(retry.Content);
        Assert.Equal(HttpStatusCode.NotFound, (await tenant.PostAsJsonAsync($"/api/public/rent-payments/{installmentId}", new { token })).StatusCode);
        var page = await ReadJson(await tenant.PostAsJsonAsync($"/api/public/rent-payments/{installmentId}", new { token = newToken }));
        Assert.Equal("Payable", page.GetProperty("state").GetString());
        Assert.True(page.GetProperty("lastPaymentFailed").GetBoolean());
    }

    [PostgresFact]
    public async Task Webhook_RentEventOfThePlatformOrAnotherAccount_LeavesTheInstallmentUnpaid()
    {
        var seeded = await SignedLeaseAsync("foreign", connect: true);
        var installmentId = await FirstInstallmentIdAsync(seeded);
        var token = await SendPaymentRequestAsync(seeded, installmentId);
        using var tenant = factory.CreateClient();
        (await tenant.PostAsJsonAsync($"/api/public/rent-payments/{installmentId}/payment-session", new { token })).EnsureSuccessStatusCode();
        var paymentIntentId = (await InstallmentAsync(installmentId)).StripePaymentIntentId!;

        await HandleWebhookAsync(RentEvent("payment_intent.succeeded", paymentIntentId, installmentId, account: null), WebhookSource.Platform);
        await HandleWebhookAsync(RentEvent("payment_intent.succeeded", paymentIntentId, installmentId, "acct_someone_else"));

        Assert.Equal(RentLedgerStatus.Scheduled, (await InstallmentAsync(installmentId)).Status);
    }

    [PostgresFact]
    public async Task DisableSchedule_CancelsUnpaidInstallmentsAndKeepsThePaidOnes()
    {
        var seeded = await SignedLeaseAsync("disable");
        var installmentId = await FirstInstallmentIdAsync(seeded);
        (await seeded.Client.PostAsJsonAsync(
            $"{RentPath(seeded)}/installments/{installmentId}/mark-paid", new { paidOn = "2026-09-05" })).EnsureSuccessStatusCode();

        var response = await seeded.Client.PostAsync($"{RentPath(seeded)}/schedule/disable", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var ledger = await ReadJson(response);
        Assert.False(ledger.GetProperty("schedule").GetProperty("isActive").GetBoolean());
        var statuses = ledger.GetProperty("installments").EnumerateArray().Select(i => i.GetProperty("status").GetString()).ToList();
        Assert.Equal("Paid", statuses[0]);
        Assert.All(statuses.Skip(1), s => Assert.Equal("Cancelled", s));
        // With something paid the cadence is locked; the monthly plan itself can be enabled again.
        var quarterly = await seeded.Client.PutAsJsonAsync($"{RentPath(seeded)}/schedule", new { cadence = "Quarterly" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, quarterly.StatusCode);
        var again = await ReadJson(await seeded.Client.PutAsJsonAsync($"{RentPath(seeded)}/schedule", new { cadence = "Monthly", billingDayOfMonth = 5 }));
        Assert.All(again.GetProperty("installments").EnumerateArray().Skip(1), i => Assert.Equal("Scheduled", i.GetProperty("status").GetString()));
    }

    // ---------------------------------------------------------------- helpers

    private static string RentPath(SeededLease seeded) => $"/api/leases/{seeded.LeaseId}/rent";

    private sealed record SeededLease(HttpClient Client, Guid LeaseId, string Account, string OrgName);

    private async Task<SeededLease> DraftLeaseAsync(string suffix, bool connect = false)
    {
        factory.Clock.SetUtcNow(Now);
        var owner = $"auth0|lt06-{suffix}-{Guid.NewGuid():N}";
        var property = await factory.SeedPropertyAsync(owner);
        var client = factory.CreateAuthenticatedClient(owner, "LongTermLandlord");
        var response = await client.PostAsJsonAsync("/api/leases", new
        {
            propertyId = property.Id,
            fiscalRegime = "CedolareSecca",
            startDate = "2026-09-01T00:00:00Z",
            endDate = "2030-08-31T00:00:00Z",
            monthlyRent = 1200m,
            parties = new object[]
            {
                new { role = "Landlord", firstName = "Mario", lastName = "Rossi", fiscalCode = "RSSMRA80A01H501U", citizenship = "IT", contactEmail = LandlordEmail },
                new { role = "Tenant", firstName = "Giulia", lastName = "Verdi", fiscalCode = "VRDGLI85B02F205A", citizenship = "IT", contactEmail = TenantEmail },
            },
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var leaseId = (await ReadJson(response)).GetProperty("id").GetGuid();

        var account = $"acct_lt06_{Guid.NewGuid():N}";
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var org = await db.Orgs.SingleAsync(o => o.Id == property.OrgId);
        if (connect)
        {
            org.StripeConnectedAccountId = account;
            org.ConnectChargesEnabled = true;
            await db.SaveChangesAsync();
        }

        return new SeededLease(client, leaseId, account, org.Name);
    }

    private async Task<SeededLease> SignedLeaseAsync(string suffix, bool connect = false)
    {
        var seeded = await DraftLeaseAsync(suffix, connect);
        (await LeaseSigningTestClient.UploadSignedContractAsync(seeded.Client, seeded.LeaseId)).EnsureSuccessStatusCode();
        return seeded;
    }

    private async Task<Guid> FirstInstallmentIdAsync(SeededLease seeded)
    {
        var ledger = await ReadJson(await seeded.Client.PutAsJsonAsync($"{RentPath(seeded)}/schedule", new { cadence = "Monthly", billingDayOfMonth = 5 }));
        return ledger.GetProperty("installments")[0].GetProperty("id").GetGuid();
    }

    /// <summary>The landlord sends the payment link now; returns the token of the link emailed to the tenant.</summary>
    private async Task<string> SendPaymentRequestAsync(SeededLease seeded, Guid installmentId)
    {
        var response = await seeded.Client.PostAsync($"{RentPath(seeded)}/installments/{installmentId}/payment-request", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var email = factory.Emails.Snapshot().Last(e => e.To == TenantEmail && e.Content.HtmlBody.Contains(installmentId.ToString("D"), StringComparison.Ordinal));
        return LinkOf(email.Content).Token;
    }

    private IReadOnlyList<(string? To, EmailContent Content, string Template)> EmailsTo(string to, SeededLease seeded) =>
        factory.Emails.Snapshot()
            .Where(e => e.To == to && InstallmentIdsOf(seeded).Any(id => e.Content.HtmlBody.Contains(id, StringComparison.Ordinal)))
            .ToList();

    /// <summary>Emails of every test go to the same parties: they are told apart by the installment or lease id in the link.</summary>
    private List<string> InstallmentIdsOf(SeededLease seeded)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var ids = db.RentLedgerEntries.AsNoTracking()
            .Where(e => e.LeaseContractId == seeded.LeaseId)
            .Select(e => e.Id)
            .ToList()
            .Select(id => id.ToString("D"))
            .ToList();
        ids.Add(seeded.LeaseId.ToString("D"));
        return ids;
    }

    private static (Guid InstallmentId, string Token) LinkOf(EmailContent content)
    {
        var match = Regex.Match(content.HtmlBody, @"/rent/pay/([0-9a-f-]{36})\?token=([A-Za-z0-9_-]+)");
        Assert.True(match.Success, "payment link expected in the email");
        return (Guid.Parse(match.Groups[1].Value), match.Groups[2].Value);
    }

    private async Task RunJobAsync()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        await new RentCollectionJob(
                scope.ServiceProvider.GetRequiredService<IRentBillingService>(),
                NullLogger<RentCollectionJob>.Instance)
            .ExecuteAsync();
    }

    private async Task HandleWebhookAsync(Event stripeEvent, WebhookSource source = WebhookSource.Connected)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<StripeWebhookHandler>().HandleEventAsync(stripeEvent, source);
    }

    private static Event RentEvent(string type, string paymentIntentId, Guid installmentId, string? account, string? failureCode = null) => new()
    {
        Id = $"evt_lt06_{Guid.NewGuid():N}",
        Type = type,
        Account = account,
        Data = new EventData
        {
            Object = new PaymentIntent
            {
                Id = paymentIntentId,
                Amount = 120_000,
                Status = type switch
                {
                    "payment_intent.succeeded" => "succeeded",
                    "payment_intent.processing" => "processing",
                    _ => "requires_payment_method",
                },
                LastPaymentError = failureCode is null ? null : new StripeError { Code = failureCode },
                Metadata = new Dictionary<string, string>
                {
                    ["kind"] = RentCharges.Kind,
                    [RentCharges.InstallmentMetadataKey] = installmentId.ToString(),
                },
            },
        },
    };

    private async Task<RentLedgerEntry> InstallmentAsync(Guid installmentId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.RentLedgerEntries.AsNoTracking().SingleAsync(e => e.Id == installmentId);
    }

    private async Task<int> CountInstallmentsAsync(Guid leaseId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.RentLedgerEntries.CountAsync(e => e.LeaseContractId == leaseId);
    }

    private static async Task<JsonElement> ReadJson(HttpResponseMessage response) =>
        JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync(), JsonOpts);

    /// <summary>Lease flow factory with a clock the tests control and an email queue that records what is queued.</summary>
    public sealed class RentFactory : LeaseFlowWebApplicationFactory
    {
        public FakeTimeProvider Clock { get; } = new(Now);

        internal RecordingEmailQueue Emails { get; } = new();

        internal FakeStripeService Stripe => (FakeStripeService)Services.GetRequiredService<IStripeService>();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                RemoveAllOf<TimeProvider>(services);
                services.AddSingleton<TimeProvider>(Clock);
                RemoveAllOf<IEmailQueue>(services);
                services.AddSingleton<IEmailQueue>(Emails);
            });
        }
    }
}
