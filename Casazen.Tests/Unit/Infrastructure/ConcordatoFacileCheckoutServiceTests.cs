using System.Net;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Options;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Stripe.Checkout;
using Xunit;

namespace Casazen.Tests.Unit.Infrastructure;

/// <summary>
/// CF-FIX: what <see cref="ConcordatoFacileCheckoutService"/> sends to Stripe and when it refuses to, checked on a mocked
/// <see cref="Stripe.IStripeClient"/> that plays Stripe (it creates sessions and returns the ones the test has set up). The money
/// rules: the PaymentIntent carries the metadata the webhook reads, a lease that has paid is never charged again, and only a lease
/// of the caller's org can be paid for.
/// </summary>
public class ConcordatoFacileCheckoutServiceTests
{
    private const string PriceId = "price_test_concordato_facile_119";
    private const string OldSessionId = "cs_test_old_session";
    private const string NewSessionId = "cs_test_new_session";
    private const string UserId = "auth0|test_user";

    private static readonly Guid OrgId = Guid.NewGuid();
    private static readonly Guid OtherOrgId = Guid.NewGuid();
    private static readonly Guid LeaseId = Guid.NewGuid();

    private readonly string _databaseName = $"concordato-facile-checkout-{Guid.NewGuid()}";
    private readonly Mock<Stripe.IStripeClient> _client = new();
    private readonly List<(HttpMethod Method, string Path, Stripe.BaseOptions? Options)> _requests = [];
    private readonly Dictionary<string, Session> _stripeSessions = [];

    public ConcordatoFacileCheckoutServiceTests()
    {
        _client.SetupGet(c => c.ApiBase).Returns("https://api.stripe.com");
        _client
            .Setup(c => c.RequestAsync<Session>(
                It.IsAny<HttpMethod>(), It.IsAny<string>(), It.IsAny<Stripe.BaseOptions>(), It.IsAny<Stripe.RequestOptions>(), It.IsAny<CancellationToken>()))
            .Returns((HttpMethod method, string path, Stripe.BaseOptions options, Stripe.RequestOptions requestOptions, CancellationToken cancellationToken) =>
                Respond(method, path, options));
    }

    /// <summary>Stripe: a POST opens a new session, a GET reads one the test has set up or answers resource_missing.</summary>
    private Task<Session> Respond(HttpMethod method, string path, Stripe.BaseOptions? options)
    {
        _requests.Add((method, path, options));
        if (method == HttpMethod.Post)
        {
            return Task.FromResult(new Session
            {
                Id = NewSessionId,
                Status = "open",
                Url = $"https://checkout.stripe.com/c/pay/{NewSessionId}",
            });
        }

        var id = path[(path.LastIndexOf('/') + 1)..];
        return _stripeSessions.TryGetValue(id, out var session)
            ? Task.FromResult(session)
            : Task.FromException<Session>(new Stripe.StripeException(
                HttpStatusCode.NotFound,
                new Stripe.StripeError { Type = "invalid_request_error", Code = "resource_missing" },
                "No such checkout.session"));
    }

    // ─── The PaymentIntent carries the metadata the webhook reads ────────────────

    /// <summary>
    /// Stripe does not copy the metadata of a Checkout Session onto its PaymentIntent, and the webhook finds the purchase from the
    /// PaymentIntent: without <c>payment_intent_data[metadata]</c> a real payment would never be recognized. No <c>kind</c> key: its
    /// absence is what routes the payment to the ConcordatoFacile handler.
    /// </summary>
    [Fact]
    public async Task CreateCheckout_WritesTheMetadataOnThePaymentIntentToo_AndNeverAKind()
    {
        await SeedLeaseAsync(OrgId, LeaseId);

        var result = await Service().CreateCheckoutSessionAsync(LeaseId, OrgId, UserId);

        Assert.False(result.Reused);
        Assert.Equal(NewSessionId, result.SessionId);
        var create = Assert.Single(_requests, r => r.Method == HttpMethod.Post);
        Assert.Equal("/v1/checkout/sessions", create.Path);
        var options = Assert.IsType<SessionCreateOptions>(create.Options);

        var intentMetadata = options.PaymentIntentData.Metadata;
        Assert.Equal(LeaseId.ToString(), intentMetadata["leaseId"]);
        Assert.Equal(OrgId.ToString(), intentMetadata["orgId"]);
        Assert.Equal(UserId, intentMetadata["userId"]);
        Assert.False(intentMetadata.ContainsKey("kind"));
        // The session keeps its own copy.
        Assert.Equal(LeaseId.ToString(), options.Metadata["leaseId"]);
        Assert.Equal(OrgId.ToString(), options.Metadata["orgId"]);
        Assert.False(options.Metadata.ContainsKey("kind"));

        // One payment, with the price of the configuration.
        Assert.Equal("payment", options.Mode);
        Assert.Equal(PriceId, Assert.Single(options.LineItems).Price);
        // CF-END: the person comes back to the page of the guided route, whose address names nobody (not the old one that named the supplier).
        Assert.Equal($"https://app.casazen.it/app/long-rent/leases/{LeaseId}/guided-contract?checkout=success", options.SuccessUrl);
        Assert.Equal($"https://app.casazen.it/app/long-rent/leases/{LeaseId}/guided-contract?checkout=cancelled", options.CancelUrl);
        var purchase = await Db().ConcordatoFacilePurchases.SingleAsync();
        Assert.Equal(ConcordatoFacilePurchaseStatus.Pending, purchase.Status);
        Assert.Equal(NewSessionId, purchase.StripeCheckoutSessionId);
        Assert.Equal(11_900, purchase.AmountCents);
    }

    // ─── No second payment ───────────────────────────────────────────────────────

    [Fact]
    public async Task CreateCheckout_LeaseAlreadyPaid_Is409_AndStripeIsNotCalled()
    {
        await SeedLeaseAsync(OrgId, LeaseId);
        await SeedPurchaseAsync(ConcordatoFacilePurchaseStatus.Completed, OldSessionId);

        var ex = await Assert.ThrowsAsync<DomainConflictException>(() => Service().CreateCheckoutSessionAsync(LeaseId, OrgId, UserId));

        Assert.Equal("concordatofacile_already_purchased", ex.Code);
        Assert.Empty(_requests);
        Assert.Single(await Db().ConcordatoFacilePurchases.ToListAsync());
    }

    [Fact]
    public async Task CreateCheckout_LeaseWithTheGiftConsumed_Is409_AndStripeIsNotCalled()
    {
        await SeedLeaseAsync(OrgId, LeaseId);
        await using (var db = Db())
        {
            db.ConcordatoFacileGifts.Add(new ConcordatoFacileGift
            {
                OrgId = OrgId,
                GrantedBySubscriptionId = "sub_biennial",
                ConsumedAt = DateTime.UtcNow,
                ConsumedForLeaseId = LeaseId,
                Status = ConcordatoFacileGiftStatus.Consumed,
            });
            await db.SaveChangesAsync();
        }

        var ex = await Assert.ThrowsAsync<DomainConflictException>(() => Service().CreateCheckoutSessionAsync(LeaseId, OrgId, UserId));

        Assert.Equal("concordatofacile_already_purchased", ex.Code);
        Assert.Empty(_requests);
    }

    /// <summary>
    /// The customer paid and the webhook has not arrived (or never will): the purchase is completed from the paid session, no new
    /// session is opened, and the answer is the 409 of an access already bought.
    /// </summary>
    [Fact]
    public async Task CreateCheckout_SessionPaidAndWebhookMissing_CompletesThePurchase_AndOpensNoNewSession()
    {
        await SeedLeaseAsync(OrgId, LeaseId);
        await SeedPurchaseAsync(ConcordatoFacilePurchaseStatus.Pending, OldSessionId);
        _stripeSessions[OldSessionId] = new Session
        {
            Id = OldSessionId,
            Status = "complete",
            PaymentStatus = "paid",
            AmountTotal = 11_900,
            Currency = "eur",
            PaymentIntentId = "pi_paid",
        };

        var ex = await Assert.ThrowsAsync<DomainConflictException>(() => Service().CreateCheckoutSessionAsync(LeaseId, OrgId, UserId));

        Assert.Equal("concordatofacile_already_purchased", ex.Code);
        Assert.DoesNotContain(_requests, r => r.Method == HttpMethod.Post);
        var purchase = await Db().ConcordatoFacilePurchases.SingleAsync();
        Assert.Equal(ConcordatoFacilePurchaseStatus.Completed, purchase.Status);
        Assert.NotNull(purchase.PaidAt);
        Assert.Equal("pi_paid", purchase.StripePaymentIntentId);
    }

    /// <summary>A payment still in flight (delayed payment method) is not a reason to open a second session either.</summary>
    [Fact]
    public async Task CreateCheckout_SessionCompleteButNotPaidYet_OpensNoNewSession_AndLeavesThePurchasePending()
    {
        await SeedLeaseAsync(OrgId, LeaseId);
        await SeedPurchaseAsync(ConcordatoFacilePurchaseStatus.Pending, OldSessionId);
        _stripeSessions[OldSessionId] = new Session { Id = OldSessionId, Status = "complete", PaymentStatus = "unpaid" };

        var ex = await Assert.ThrowsAsync<DomainConflictException>(() => Service().CreateCheckoutSessionAsync(LeaseId, OrgId, UserId));

        Assert.Equal("concordatofacile_already_purchased", ex.Code);
        Assert.DoesNotContain(_requests, r => r.Method == HttpMethod.Post);
        Assert.Equal(ConcordatoFacilePurchaseStatus.Pending, (await Db().ConcordatoFacilePurchases.SingleAsync()).Status);
    }

    [Fact]
    public async Task CreateCheckout_OpenSession_IsReused_WithoutANewSessionOrPurchase()
    {
        await SeedLeaseAsync(OrgId, LeaseId);
        await SeedPurchaseAsync(ConcordatoFacilePurchaseStatus.Pending, OldSessionId);
        _stripeSessions[OldSessionId] = new Session { Id = OldSessionId, Status = "open", Url = "https://checkout.stripe.com/c/pay/old" };

        var result = await Service().CreateCheckoutSessionAsync(LeaseId, OrgId, UserId);

        Assert.True(result.Reused);
        Assert.Equal(OldSessionId, result.SessionId);
        Assert.Equal("https://checkout.stripe.com/c/pay/old", result.SessionUrl);
        Assert.DoesNotContain(_requests, r => r.Method == HttpMethod.Post);
        Assert.Single(await Db().ConcordatoFacilePurchases.ToListAsync());
    }

    /// <summary>An expired session, or one Stripe no longer knows, closes its purchase: a lease is left with one pending row, not two.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CreateCheckout_SessionExpiredOrGone_ClosesTheOldPurchase_AndOpensANewOne(bool expiredOnStripe)
    {
        await SeedLeaseAsync(OrgId, LeaseId);
        await SeedPurchaseAsync(ConcordatoFacilePurchaseStatus.Pending, OldSessionId);
        if (expiredOnStripe)
            _stripeSessions[OldSessionId] = new Session { Id = OldSessionId, Status = "expired" };

        var result = await Service().CreateCheckoutSessionAsync(LeaseId, OrgId, UserId);

        Assert.False(result.Reused);
        Assert.Equal(NewSessionId, result.SessionId);
        Assert.Single(_requests, r => r.Method == HttpMethod.Post);
        var purchases = await Db().ConcordatoFacilePurchases.ToListAsync();
        Assert.Equal(2, purchases.Count);
        Assert.Equal(ConcordatoFacilePurchaseStatus.Failed, Assert.Single(purchases, p => p.StripeCheckoutSessionId == OldSessionId).Status);
        Assert.Equal(ConcordatoFacilePurchaseStatus.Pending, Assert.Single(purchases, p => p.StripeCheckoutSessionId == NewSessionId).Status);
    }

    // ─── Only a lease of the caller's org ────────────────────────────────────────

    [Fact]
    public async Task CreateCheckout_LeaseOfAnotherOrgOrMissing_Is404_AndStripeIsNotCalled()
    {
        await SeedLeaseAsync(OtherOrgId, LeaseId);

        var other = await Assert.ThrowsAsync<NotFoundException>(() => Service().CreateCheckoutSessionAsync(LeaseId, OrgId, UserId));
        var missing = await Assert.ThrowsAsync<NotFoundException>(() => Service().CreateCheckoutSessionAsync(Guid.NewGuid(), OrgId, UserId));

        Assert.Equal("lease_not_found", other.Code);
        Assert.Equal("lease_not_found", missing.Code);
        Assert.Empty(_requests);
        Assert.Empty(await Db().ConcordatoFacilePurchases.ToListAsync());
    }

    // ─── helpers ─────────────────────────────────────────────────────────────────

    private ConcordatoFacileCheckoutService Service() => new(
        Db(),
        Options.Create(new ConcordatoFacileOptions
        {
            StripePriceId = PriceId,
            BaseUrl = "https://app.casazen.it",
            StripeApiKey = "sk_test_fake_key_for_unit_test",
        }),
        NullLogger<ConcordatoFacileCheckoutService>.Instance,
        _client.Object);

    /// <summary>A context over the store of this test: the service, the seeds and the assertions each use their own.</summary>
    private AppDbContext Db() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(_databaseName).Options);

    private async Task SeedLeaseAsync(Guid orgId, Guid leaseId)
    {
        await using var db = Db();
        var property = new Property
        {
            Id = Guid.NewGuid(),
            OrgId = orgId,
            Name = "Test Property",
            Address = "Test Address",
            City = "Test City",
            PostalCode = "12345",
        };
        db.Properties.Add(property);
        db.LeaseContracts.Add(new LeaseContract
        {
            Id = leaseId,
            OrgId = orgId,
            PropertyId = property.Id,
            Status = LeaseStatus.Signed,
            FiscalRegime = FiscalRegime.CanoneConcordato,
            StartDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            EndDate = new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            MonthlyRent = 1000m,
        });
        await db.SaveChangesAsync();
    }

    private async Task SeedPurchaseAsync(ConcordatoFacilePurchaseStatus status, string sessionId)
    {
        await using var db = Db();
        db.ConcordatoFacilePurchases.Add(new ConcordatoFacilePurchase
        {
            Id = Guid.NewGuid(),
            OrgId = OrgId,
            LeaseContractId = LeaseId,
            StripeCheckoutSessionId = sessionId,
            AmountCents = 11_900,
            Currency = "eur",
            Status = status,
            CreatedAt = DateTime.UtcNow.AddHours(-1),
            PaidAt = status == ConcordatoFacilePurchaseStatus.Completed ? DateTime.UtcNow.AddHours(-1) : null,
        });
        await db.SaveChangesAsync();
    }
}
