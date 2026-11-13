using System.Net;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Leases;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.External;
using Casazen.Infrastructure.Repositories;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Integration;
using Casazen.Tests.Unit.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Stripe;
using Stripe.Checkout;
using Xunit;
using static Casazen.Tests.Unit.Services.OrgTeamTestData;

namespace Casazen.Tests.Unit.Leases;

/// <summary>
/// The filing-fee checkout asks Stripe before opening another session, and the webhook completes only the platform
/// Checkout session that row stores. A Connect PaymentIntent with the same kind does not pay the fee.
/// </summary>
public class OpenapiRegistrationPaymentTests
{
    private static readonly Guid OrgId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid LeaseId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly DateTime Now = new(2026, 11, 8, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Checkout_IncompletePreparation_DoesNotContactStripe()
    {
        await using var db = NewDb();
        await SeedLeaseAsync(db);
        var stripe = new StripeDouble();
        var preparation = new Mock<IOpenapiRegistrationPreparation>();
        preparation.Setup(p => p.EnsureReadyAsync(It.IsAny<LeaseContract>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DomainRuleException("openapi_registration_incomplete", "OpenapiRegistrationIncomplete"));
        var error = await Assert.ThrowsAsync<DomainRuleException>(() => Checkout(db, stripe.Client, preparation.Object)
            .CreateCheckoutSessionAsync(LeaseId, OrgId, "auth0|owner"));
        Assert.Equal("openapi_registration_incomplete", error.Code);
        Assert.Empty(stripe.Requests);
        Assert.Empty(await db.OpenapiLeaseRegistrationPayments.ToListAsync());
    }

    [Fact]
    public async Task Checkout_PreviousSessionAlreadyPaid_DoesNotOpenAnother()
    {
        await using var db = NewDb();
        await SeedLeaseAsync(db);
        db.OpenapiLeaseRegistrationPayments.Add(Pending(amountCents: 10_000, sessionId: "cs_old", createdAt: Now.AddMinutes(-31)));
        await db.SaveChangesAsync();
        var stripe = new StripeDouble();
        stripe.Sessions["cs_old"] = new Session
        {
            Id = "cs_old",
            Status = "complete",
            PaymentStatus = "paid",
            AmountTotal = 10_000,
            Currency = "eur",
            PaymentIntentId = "pi_old",
        };

        var error = await Assert.ThrowsAsync<DomainConflictException>(() =>
            Checkout(db, stripe.Client).CreateCheckoutSessionAsync(LeaseId, OrgId, "auth0|owner"));

        Assert.Equal(OpenapiRegistrationFee.AlreadyPaidCode, error.Code);
        Assert.DoesNotContain(stripe.Requests, r => r.Method == HttpMethod.Post);
        var row = await db.OpenapiLeaseRegistrationPayments.SingleAsync();
        Assert.Equal(OpenapiRegistrationPaymentStatus.Completed, row.Status);
        Assert.Equal("pi_old", row.StripePaymentIntentId);
    }

    [Fact]
    public async Task Webhook_LateSuccess_CompletesOnlyThatPendingSession()
    {
        await using var db = NewDb();
        var older = Pending(amountCents: 28_800, sessionId: "cs_old", createdAt: Now.AddMinutes(-40));
        var newer = Pending(amountCents: 28_800, sessionId: "cs_new", createdAt: Now.AddMinutes(-5));
        db.OpenapiLeaseRegistrationPayments.AddRange(older, newer);
        await db.SaveChangesAsync();
        var stripe = new StripeDouble();
        stripe.SessionsByPaymentIntent["pi_old"] = "cs_old";

        await Handler(db, stripe.Client).HandlePaymentIntentSucceededAsync(
            Intent("pi_old"), WebhookSource.Platform, Now);

        Assert.Equal(OpenapiRegistrationPaymentStatus.Completed, (await Reload(db, older.Id)).Status);
        Assert.Equal(OpenapiRegistrationPaymentStatus.Pending, (await Reload(db, newer.Id)).Status);
    }

    [Fact]
    public async Task Webhook_LateSuccess_DoesNotReviveAFailedSession()
    {
        await using var db = NewDb();
        var failed = Pending(amountCents: 28_800, sessionId: "cs_old", createdAt: Now.AddMinutes(-40));
        failed.Status = OpenapiRegistrationPaymentStatus.Failed;
        var newer = Pending(amountCents: 28_800, sessionId: "cs_new", createdAt: Now.AddMinutes(-5));
        db.OpenapiLeaseRegistrationPayments.AddRange(failed, newer);
        await db.SaveChangesAsync();
        var stripe = new StripeDouble();
        stripe.SessionsByPaymentIntent["pi_old"] = "cs_old";

        await Handler(db, stripe.Client).HandlePaymentIntentSucceededAsync(
            Intent("pi_old"), WebhookSource.Platform, Now);

        Assert.Equal(OpenapiRegistrationPaymentStatus.Failed, (await Reload(db, failed.Id)).Status);
        Assert.Equal(OpenapiRegistrationPaymentStatus.Pending, (await Reload(db, newer.Id)).Status);
    }

    [Fact]
    public async Task Webhook_ConnectedPaymentIntent_DoesNotMarkTheQuotePaid()
    {
        await using var db = NewDb();
        await SeedLeaseAsync(db);
        var quote = Pending(amountCents: 28_800, sessionId: "cs_platform", createdAt: Now);
        db.OpenapiLeaseRegistrationPayments.Add(quote);
        await db.SaveChangesAsync();
        var config = new ConfigurationBuilder().AddInMemoryCollection().Build();
        var handler = new StripeWebhookHandler(
            new PaymentRepository(db),
            new BookingRepository(db),
            Mock.Of<IConnectOnboardingService>(),
            db,
            new FakeStripeBillingService(config),
            new EntitlementService(db, config, activityLog: null),
            TestPlatformInvoices.Create(db, new FakeStripeBillingService(config)),
            Mock.Of<IRentBillingService>(),
            Mock.Of<IPaymentRefundService>(),
            TestCheckoutPaymentSettlement.Create(db),
            TestDeferredCharges.Create(db),
            Mock.Of<ISupplierPaymentWebhookService>(),
            NullLogger<StripeWebhookHandler>.Instance);

        await handler.HandleEventAsync(new Event
        {
            Id = "evt_connect_openapi",
            Type = "payment_intent.succeeded",
            Account = "acct_host",
            Data = new EventData { Object = Intent("pi_connect") },
        }, WebhookSource.Connected);

        Assert.Equal(OpenapiRegistrationPaymentStatus.Pending, (await Reload(db, quote.Id)).Status);
    }

    private static PaymentIntent Intent(string id) => new()
    {
        Id = id,
        Amount = 28_800,
        Currency = "eur",
        Created = Now,
        Metadata = new Dictionary<string, string>
        {
            ["kind"] = OpenapiRegistrationFee.StripeKind,
            ["leaseId"] = LeaseId.ToString(),
            ["orgId"] = OrgId.ToString(),
        },
    };

    private static OpenapiLeaseRegistrationPayment Pending(int amountCents, string sessionId, DateTime createdAt) => new()
    {
        OrgId = OrgId,
        LeaseContractId = LeaseId,
        FeePercent = 0.02m,
        AmountCents = amountCents,
        Currency = "eur",
        Status = OpenapiRegistrationPaymentStatus.Pending,
        StripeCheckoutSessionId = sessionId,
        SessionUrl = "https://checkout.stripe.com/" + sessionId,
        CreatedAt = createdAt,
    };

    private static async Task<OpenapiLeaseRegistrationPayment> Reload(AppDbContext db, Guid id)
    {
        db.ChangeTracker.Clear();
        return await db.OpenapiLeaseRegistrationPayments.SingleAsync(p => p.Id == id);
    }

    private static OpenapiRegistrationCheckoutService Checkout(AppDbContext db, Stripe.IStripeClient client, IOpenapiRegistrationPreparation? preparation = null) =>
        new(
            db,
            Options.Create(new CedolareAdvisoryOptions
            {
                Registro = new RegistroTaxOptions { Rate = 0.02m, FirstYearMinimumEur = 67m, ConcordatoAtaBaseShare = 0.70m },
            }),
            Options.Create(new ConcordatoFacileOptions { BaseUrl = "https://app.casazen.it" }),
            new ConfigurationBuilder().AddInMemoryCollection().Build(),
            NullLogger<OpenapiRegistrationCheckoutService>.Instance,
            client,
            new FixedClock(),
            preparation ?? Mock.Of<IOpenapiRegistrationPreparation>());

    private static OpenapiRegistrationPaymentHandler Handler(AppDbContext db, Stripe.IStripeClient client) =>
        new(db, NullLogger<OpenapiRegistrationPaymentHandler>.Instance, client);

    private static async Task SeedLeaseAsync(AppDbContext db)
    {
        var org = AddOrg(db);
        org.Id = OrgId;
        db.LeaseContracts.Add(new LeaseContract
        {
            Id = LeaseId,
            OrgId = OrgId,
            Property = new Property { OrgId = OrgId, Address = "Via Roma 1", City = "Milano" },
            Status = LeaseStatus.Signed,
            ContractType = LeaseContractType.Libero,
            FiscalRegime = FiscalRegime.CedolareSecca,
            StartDate = Now.Date,
            EndDate = Now.Date.AddYears(4),
            MonthlyRent = 1_200m,
        });
        await db.SaveChangesAsync();
    }

    private static AppDbContext NewDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(Now, TimeSpan.Zero);
    }

    private sealed class StripeDouble
    {
        public List<(HttpMethod Method, string Path)> Requests { get; } = [];
        public Dictionary<string, Session> Sessions { get; } = [];
        public Dictionary<string, string> SessionsByPaymentIntent { get; } = [];
        public Stripe.IStripeClient Client { get; }

        public StripeDouble()
        {
            var mock = new Mock<Stripe.IStripeClient>();
            mock.SetupGet(c => c.ApiBase).Returns("https://api.stripe.com");
            mock.Setup(c => c.RequestAsync<Session>(
                    It.IsAny<HttpMethod>(), It.IsAny<string>(), It.IsAny<BaseOptions>(), It.IsAny<RequestOptions>(), It.IsAny<CancellationToken>()))
                .Returns((HttpMethod method, string path, BaseOptions _, RequestOptions _, CancellationToken _) =>
                {
                    Requests.Add((method, path));
                    if (method == HttpMethod.Post)
                    {
                        return Task.FromResult(new Session { Id = "cs_should_not_open", Url = "https://checkout.stripe.com/new", Status = "open" });
                    }

                    var id = path[(path.LastIndexOf('/') + 1)..];
                    return Sessions.TryGetValue(id, out var session)
                        ? Task.FromResult(session)
                        : Task.FromException<Session>(new StripeException(
                            HttpStatusCode.NotFound,
                            new StripeError { Code = "resource_missing" },
                            "missing"));
                });
            mock.Setup(c => c.RequestAsync<StripeList<Session>>(
                    It.IsAny<HttpMethod>(), It.IsAny<string>(), It.IsAny<BaseOptions>(), It.IsAny<RequestOptions>(), It.IsAny<CancellationToken>()))
                .Returns((HttpMethod _, string _, BaseOptions options, RequestOptions _, CancellationToken _) =>
                {
                    var paymentIntent = (options as SessionListOptions)?.PaymentIntent;
                    var id = paymentIntent is not null && SessionsByPaymentIntent.TryGetValue(paymentIntent, out var sessionId)
                        ? sessionId
                        : null;
                    return Task.FromResult(new StripeList<Session>
                    {
                        Data = id is null ? [] : [new Session { Id = id }],
                    });
                });
            Client = mock.Object;
        }
    }
}
