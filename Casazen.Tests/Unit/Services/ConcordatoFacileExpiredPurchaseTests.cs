using System.Net;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Leases;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Stripe.Checkout;
using Xunit;

namespace Casazen.Tests.Unit.Services;

public class ConcordatoFacileExpiredPurchaseTests
{
    private const string OldSessionId = "cs_test_expired_session";
    private const string NewSessionId = "cs_test_new_session";
    private const string UserId = "auth0|test_user";

    private static readonly Guid OrgId = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);

    private readonly string _databaseName = $"concordato-facile-expired-{Guid.NewGuid()}";
    private readonly FakeTimeProvider _clock = new(Now);
    private readonly Mock<Stripe.IStripeClient> _stripe = new();
    private readonly List<(HttpMethod Method, string Path, Stripe.BaseOptions? Options)> _requests = [];
    private readonly Dictionary<string, Session> _stripeSessions = [];

    public ConcordatoFacileExpiredPurchaseTests()
    {
        _stripe.SetupGet(c => c.ApiBase).Returns("https://api.stripe.com");
        _stripe
            .Setup(c => c.RequestAsync<Session>(
                It.IsAny<HttpMethod>(), It.IsAny<string>(), It.IsAny<Stripe.BaseOptions>(), It.IsAny<Stripe.RequestOptions>(), It.IsAny<CancellationToken>()))
            .Returns((HttpMethod method, string path, Stripe.BaseOptions options, Stripe.RequestOptions requestOptions, CancellationToken cancellationToken) =>
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
            });
    }

    [Fact]
    public async Task ExpiredPendingPurchase_DoesNotBlockANewCheckout_NorTheDeletionOfTheDraft()
    {
        var checkoutLease = await SeedDraftAsync();
        var deletableLease = await SeedDraftAsync();
        var threeHoursAgo = Now.UtcDateTime.AddHours(-3);
        await SeedPurchaseAsync(checkoutLease, ConcordatoFacilePurchaseStatus.Pending, OldSessionId, threeHoursAgo);
        await SeedPurchaseAsync(deletableLease, ConcordatoFacilePurchaseStatus.Pending, "cs_test_other_expired", threeHoursAgo);
        _stripeSessions[OldSessionId] = new Session { Id = OldSessionId, Status = "expired" };

        var checkout = await Checkout().CreateCheckoutSessionAsync(checkoutLease, OrgId, UserId);

        Assert.False(checkout.Reused);
        Assert.Equal(NewSessionId, checkout.SessionId);
        var created = Assert.IsType<SessionCreateOptions>(Assert.Single(_requests, r => r.Method == HttpMethod.Post).Options);
        Assert.Equal(Now.UtcDateTime.AddHours(2), created.ExpiresAt);
        var purchases = await Db().ConcordatoFacilePurchases.Where(p => p.LeaseContractId == checkoutLease).ToListAsync();
        Assert.Equal(ConcordatoFacilePurchaseStatus.Failed, Assert.Single(purchases, p => p.StripeCheckoutSessionId == OldSessionId).Status);
        Assert.Equal(ConcordatoFacilePurchaseStatus.Pending, Assert.Single(purchases, p => p.StripeCheckoutSessionId == NewSessionId).Status);

        await using (var db = Db())
        {
            var deleter = Deleter(db);
            var lease = await deleter.WithChildren().SingleAsync(l => l.Id == deletableLease);
            Assert.True(await deleter.IsDeletableAsync(lease));

            await deleter.DeleteAsync(lease, CancellationToken.None);
        }

        Assert.False(await Db().LeaseContracts.AnyAsync(l => l.Id == deletableLease));
        Assert.False(await Db().ConcordatoFacilePurchases.AnyAsync(p => p.LeaseContractId == deletableLease));

        await using var after = Db();
        Assert.False(await Deleter(after).IsDeletableAsync(await after.LeaseContracts.SingleAsync(l => l.Id == checkoutLease)));
    }

    [Theory]
    [InlineData(ConcordatoFacilePurchaseStatus.Completed, 24 * 40)]
    [InlineData(ConcordatoFacilePurchaseStatus.Refunded, 24 * 90)]
    [InlineData(ConcordatoFacilePurchaseStatus.Pending, 0)]
    public async Task PaidRefundedOrPendingInItsTime_StaysProtected_FromTheDeletionOfTheDraft(ConcordatoFacilePurchaseStatus status, int ageHours)
    {
        var lease = await SeedDraftAsync();
        await SeedPurchaseAsync(lease, status, "cs_test_protected", Now.UtcDateTime.AddHours(-ageHours).AddMinutes(-30));

        await using var db = Db();
        var deleter = Deleter(db);
        var draft = await deleter.WithChildren().SingleAsync(l => l.Id == lease);

        Assert.False(await deleter.IsDeletableAsync(draft));
        var refused = await Assert.ThrowsAsync<DomainConflictException>(() => deleter.DeleteAsync(draft, CancellationToken.None));
        Assert.Equal(LeaseDraftErrorCodes.NotDeletable, refused.Code);
        Assert.True(await Db().LeaseContracts.AnyAsync(l => l.Id == lease));
        Assert.Equal(1, await Db().ConcordatoFacilePurchases.CountAsync(p => p.LeaseContractId == lease));
    }

    [Fact]
    public async Task PaidPurchase_StaysProtected_FromANewCheckout_AndStripeIsNotCalled()
    {
        var lease = await SeedDraftAsync();
        await SeedPurchaseAsync(lease, ConcordatoFacilePurchaseStatus.Completed, OldSessionId, Now.UtcDateTime.AddDays(-40));

        var refused = await Assert.ThrowsAsync<DomainConflictException>(() => Checkout().CreateCheckoutSessionAsync(lease, OrgId, UserId));

        Assert.Equal("concordatofacile_already_purchased", refused.Code);
        Assert.Empty(_requests);
    }

    private ConcordatoFacileCheckoutService Checkout() => new(
        Db(),
        Options.Create(new ConcordatoFacileOptions
        {
            StripePriceId = "price_test_concordato_facile_119",
            BaseUrl = "https://app.casazen.it",
            StripeApiKey = "sk_test_fake_key_for_unit_test",
        }),
        NullLogger<ConcordatoFacileCheckoutService>.Instance,
        _stripe.Object,
        _clock);

    private LeaseDraftDeleter Deleter(AppDbContext db) =>
        new(db, new Mock<IFileStorage>().Object, NullLogger<LeaseDraftDeleter>.Instance, _clock);

    private AppDbContext Db() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(_databaseName).Options);

    private async Task<Guid> SeedDraftAsync()
    {
        var draft = new LeaseContract
        {
            Id = Guid.NewGuid(),
            OrgId = OrgId,
            PropertyId = Guid.NewGuid(),
            Status = LeaseStatus.Draft,
            IsConcordatoFacileSource = true,
            StartDate = new DateTime(2026, 1, 1),
            EndDate = new DateTime(2029, 1, 1),
            MonthlyRent = 0,
            WizardMaxStep = 1,
            DraftVersion = 1,
        };
        draft.SetContractTerms(LeaseContractType.Concordato, null);

        await using var db = Db();
        db.LeaseContracts.Add(draft);
        await db.SaveChangesAsync();
        return draft.Id;
    }

    private async Task SeedPurchaseAsync(Guid leaseId, ConcordatoFacilePurchaseStatus status, string sessionId, DateTime createdAt)
    {
        await using var db = Db();
        db.ConcordatoFacilePurchases.Add(new ConcordatoFacilePurchase
        {
            Id = Guid.NewGuid(),
            OrgId = OrgId,
            LeaseContractId = leaseId,
            StripeCheckoutSessionId = sessionId,
            AmountCents = 11_900,
            Currency = "eur",
            Status = status,
            CreatedAt = createdAt,
            PaidAt = status is ConcordatoFacilePurchaseStatus.Completed or ConcordatoFacilePurchaseStatus.Refunded ? createdAt.AddMinutes(5) : null,
        });
        await db.SaveChangesAsync();
    }
}
