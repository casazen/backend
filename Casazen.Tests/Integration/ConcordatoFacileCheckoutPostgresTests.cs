using System.Collections.Concurrent;
using System.Net;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Integration.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Stripe.Checkout;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// CF-BE (closes CF-FIX-FU3) on real PostgreSQL, what memory cannot show: two clicks on "pay" at the same moment for the same lease open
/// ONE Stripe Checkout Session and write ONE pending purchase. The checkout of a lease runs under <c>pg_advisory_xact_lock</c>, Stripe calls
/// included: the second request waits, then finds the purchase and the open Session of the first and reuses them. Stripe is a mocked client
/// whose creation of a Session takes a moment, so that without the lock both requests would be inside it together.
/// </summary>
public class ConcordatoFacileCheckoutPostgresTests
{
    private const string UserId = "auth0|test_user";

    [PostgresFact]
    public async Task TwoClicksOnPayAtTheSameMoment_OpenOneSession_AndWriteOnePendingPurchase()
    {
        await using var database = PostgresTestDatabase.CreateMigrated("cfcheckout");
        var (orgId, leaseId) = await SeedLeaseAsync(database);
        var stripe = new SlowStripe();

        // Two requests, each with its own context and connection, as two requests of the API are.
        async Task<CheckoutResult> ClickAsync()
        {
            await using var db = database.CreateContext();
            var service = new ConcordatoFacileCheckoutService(
                db,
                Options.Create(new ConcordatoFacileOptions
                {
                    StripePriceId = "price_test_concordato_facile_119",
                    BaseUrl = "https://app.casazen.it",
                    StripeApiKey = "sk_test_fake_key_for_unit_test",
                }),
                NullLogger<ConcordatoFacileCheckoutService>.Instance,
                stripe.Client);
            return await service.CreateCheckoutSessionAsync(leaseId, orgId, UserId);
        }

        var results = await Task.WhenAll(ClickAsync(), ClickAsync());

        Assert.Equal(1, stripe.SessionsCreated);
        Assert.Single(results.Select(r => r.SessionId).Distinct());
        Assert.Equal(1, results.Count(r => r.Reused));

        await using var check = database.CreateContext();
        var purchase = Assert.Single(await check.ConcordatoFacilePurchases.ToListAsync());
        Assert.Equal(ConcordatoFacilePurchaseStatus.Pending, purchase.Status);
        Assert.Equal(results[0].SessionId, purchase.StripeCheckoutSessionId);
    }

    private static async Task<(Guid OrgId, Guid LeaseId)> SeedLeaseAsync(PostgresTestDatabase database)
    {
        var orgId = Guid.NewGuid();
        var propertyId = Guid.NewGuid();
        var leaseId = Guid.NewGuid();

        await using var db = database.CreateContext();
        db.Orgs.Add(new OrgEntity { Id = orgId, Name = "CfCheckoutOrg", Slug = $"cf-checkout-{orgId:N}", CreatedAt = DateTime.UtcNow });
        db.Properties.Add(new Property
        {
            Id = propertyId,
            OrgId = orgId,
            Name = "CfCheckoutProperty",
            City = "Monza",
            Address = "Via Roma 1",
            OwnerId = "auth0|owner_cf_checkout",
        });
        db.LeaseContracts.Add(new LeaseContract { Id = leaseId, OrgId = orgId, PropertyId = propertyId, CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
        return (orgId, leaseId);
    }

    /// <summary>A Stripe where creating a Session takes a moment and a Session already created can be read back, thread-safe.</summary>
    private sealed class SlowStripe
    {
        private readonly ConcurrentDictionary<string, Session> _sessions = new();
        private int _created;

        public SlowStripe()
        {
            var mock = new Mock<Stripe.IStripeClient>();
            mock.SetupGet(c => c.ApiBase).Returns("https://api.stripe.com");
            mock.Setup(c => c.RequestAsync<Session>(
                    It.IsAny<HttpMethod>(), It.IsAny<string>(), It.IsAny<Stripe.BaseOptions>(), It.IsAny<Stripe.RequestOptions>(), It.IsAny<CancellationToken>()))
                .Returns(async (HttpMethod method, string path, Stripe.BaseOptions options, Stripe.RequestOptions requestOptions, CancellationToken cancellationToken) =>
                {
                    if (method == HttpMethod.Post)
                    {
                        await Task.Delay(400, cancellationToken);
                        var id = $"cs_test_slow_{Interlocked.Increment(ref _created)}";
                        var session = new Session { Id = id, Status = "open", Url = $"https://checkout.stripe.com/c/pay/{id}" };
                        _sessions[id] = session;
                        return session;
                    }

                    var requested = path[(path.LastIndexOf('/') + 1)..];
                    return _sessions.TryGetValue(requested, out var found)
                        ? found
                        : throw new Stripe.StripeException(
                            HttpStatusCode.NotFound,
                            new Stripe.StripeError { Type = "invalid_request_error", Code = "resource_missing" },
                            "No such checkout.session");
                });
            Client = mock.Object;
        }

        public Stripe.IStripeClient Client { get; }

        public int SessionsCreated => Volatile.Read(ref _created);
    }
}
