using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Multitenancy;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.External;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Stripe;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// The payment link of a booking the host entered (E08/F01/US01/T05, NF-74): the link opens the checkout page of the booking site with a token
/// that is the only access check of an anonymous page. The rule that costs if it fails: a token opens the payment of the booking it was made
/// for and of no other, and a new link makes the previous one stop working.
/// </summary>
public class BookingPaymentLinkProofTests
{
    private static readonly Guid OrgA = Guid.Parse("4f1e2d3c-6b5a-4978-8a1b-2c3d4e5f6a51");
    private const string Account = "acct_host_a";

    /// <summary>
    /// PROOF TEST: permission rule (the token of a link). Two host bookings get a link each. The token of the first resumes the payment of the
    /// first, for exactly its balance, and opens neither the page nor the payment of the second (the same 404 as a wrong token). After a new link
    /// for the first booking its old token stops working and the new one works.
    /// </summary>
    [Fact]
    public async Task BookingPaymentLink_TokenOpensOnlyItsBooking_Test()
    {
        var h = new Harness();
        var first = await h.SeedHostBookingAsync(total: 300.00m);
        var second = await h.SeedHostBookingAsync(total: 150.00m);

        var firstLink = await h.LinkAsync(first);
        var secondLink = await h.LinkAsync(second);
        Assert.Equal(300.00m, firstLink.Amount);
        Assert.Equal(150.00m, secondLink.Amount);
        Assert.True(firstLink.EmailQueued);
        var firstToken = TokenOf(firstLink.Url);
        var secondToken = TokenOf(secondLink.Url);

        // The token of the first booking pays the first booking, for exactly its balance.
        var session = await h.Outcomes().ResumePaymentAsync(first, firstToken);
        Assert.Equal(h.ClientSecretOf(30000), session.ClientSecret);
        Assert.Equal(Account, session.StripeAccountId);
        Assert.Equal(CheckoutOutcomeState.AwaitingPayment, (await h.Outcomes().GetOutcomeAsync(first, firstToken)).State);

        // ... and opens nothing of the second booking: the page and the payment answer the 404 of a wrong link.
        await Assert.ThrowsAsync<NotFoundException>(() => h.Outcomes().GetOutcomeAsync(second, firstToken));
        await Assert.ThrowsAsync<NotFoundException>(() => h.Outcomes().ResumePaymentAsync(second, firstToken));
        Assert.Equal(h.ClientSecretOf(15000), (await h.Outcomes().ResumePaymentAsync(second, secondToken)).ClientSecret);

        // A new link of the first booking: the old token stops working, the new one works.
        var newLink = await h.LinkAsync(first);
        var newToken = TokenOf(newLink.Url);
        Assert.NotEqual(firstToken, newToken);
        await Assert.ThrowsAsync<NotFoundException>(() => h.Outcomes().ResumePaymentAsync(first, firstToken));
        await Assert.ThrowsAsync<NotFoundException>(() => h.Outcomes().GetOutcomeAsync(first, firstToken));
        Assert.NotNull((await h.Outcomes().ResumePaymentAsync(first, newToken)).ClientSecret);

        // The raw token is never stored: only its hash is on the booking.
        await using var read = h.NewDb(null);
        var stored = await read.Bookings.AsNoTracking().SingleAsync(b => b.Id == first);
        Assert.NotEqual(newToken, stored.CheckoutTokenHash);
        Assert.Equal(CheckoutOutcomes.HashToken(newToken), stored.CheckoutTokenHash);
        Assert.Equal(HostPaymentArrangement.PaymentLink, stored.HostPaymentArrangement);
        Assert.NotNull(stored.PaymentLinkSentAt);
    }

    /// <summary>
    /// PROOF TEST: money rule. The host records a receipt by hand on a booking whose guest has a payment link: the open PaymentIntent of the
    /// link is cancelled on Stripe (under the same lock) and its row marked Canceled, so a page the guest already has open cannot collect the
    /// balance a second time. While the guest is paying (the PaymentIntent is processing, or succeeded and not recorded yet) the receipt waits
    /// instead, and nothing is cancelled or written.
    /// </summary>
    [Fact]
    public async Task ManualReceipt_OnABookingWithAPaymentLink_CancelsTheOpenIntent_AndWaitsForAPaymentInProgress_Test()
    {
        var h = new Harness();
        var booking = await h.SeedHostBookingAsync(total: 300.00m);
        await h.LinkAsync(booking);
        var intentId = h.LastIntentId;

        // The guest is paying right now: the receipt waits, nothing is cancelled and nothing is written.
        h.SetIntentStatus(intentId, "processing");
        var waiting = await Assert.ThrowsAsync<DomainConflictException>(() => h.RecordCashAsync(booking, 100m, "receipt-1"));
        Assert.Equal("payment_in_progress", waiting.Code);
        Assert.Empty(h.CancelledIntents);
        await using (var read = h.NewDb(null))
        {
            var link = await read.Payments.AsNoTracking().SingleAsync();
            Assert.Equal(PaymentStatus.Pending, link.Status);
        }

        // The guest has not paid: the receipt is recorded and the open PaymentIntent of the link is cancelled, its row marked Canceled.
        h.SetIntentStatus(intentId, "requires_payment_method");
        var receipt = await h.RecordCashAsync(booking, 100m, "receipt-2");
        Assert.True(receipt.Created);
        Assert.Equal(PaymentStatus.Completed, receipt.Payment.Status);
        Assert.Equal(intentId, Assert.Single(h.CancelledIntents));
        await using (var read = h.NewDb(null))
        {
            var rows = await read.Payments.AsNoTracking().Where(p => p.BookingId == booking).ToListAsync();
            Assert.Equal(2, rows.Count);
            Assert.Equal(PaymentStatus.Canceled, rows.Single(p => p.StripePaymentIntentId == intentId).Status);
            Assert.Equal(PaymentStatus.Completed, rows.Single(p => p.StripePaymentIntentId == null).Status);
        }

        // The same receipt sent again records nothing and cancels nothing more.
        var again = await h.RecordCashAsync(booking, 100m, "receipt-2");
        Assert.False(again.Created);
        Assert.Single(h.CancelledIntents);
    }

    private static string TokenOf(string url)
    {
        var query = new Uri(url).Query;
        Assert.StartsWith("?token=", query, StringComparison.Ordinal);
        return Uri.UnescapeDataString(query["?token=".Length..]);
    }

    private sealed class Harness
    {
        private readonly string _database = $"payment-link-{Guid.NewGuid():N}";
        private readonly Dictionary<string, PaymentIntent> _intents = new(StringComparer.Ordinal);
        private readonly Mock<IStripeService> _stripe = new();
        private readonly Guid _propertyId;
        private int _created;

        public Harness()
        {
            using var seed = NewDb(null);
            seed.Orgs.Add(new OrgEntity
            {
                Id = OrgA,
                Name = "Casa Org",
                Slug = "casa-org",
                StripeConnectedAccountId = Account,
                ConnectChargesEnabled = true,
            });
            var property = new Property
            {
                OrgId = OrgA,
                OwnerId = "auth0|owner-a",
                Name = "Casa del Lago",
                Address = "Via del Lago 1",
                City = "Como",
                MaxGuests = 4,
                Currency = "EUR",
            };
            seed.Properties.Add(property);
            seed.SaveChanges();
            _propertyId = property.Id;

            _stripe
                .Setup(s => s.CreateConnectedAccountPaymentIntentAsync(
                    It.IsAny<string>(),
                    It.IsAny<long>(),
                    It.IsAny<string>(),
                    It.IsAny<Dictionary<string, string>>(),
                    It.IsAny<string>(),
                    It.IsAny<string?>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync((string _, long amount, string _, Dictionary<string, string> _, string _, string? _, CancellationToken _) =>
                {
                    var intent = new PaymentIntent
                    {
                        Id = $"pi_link_{++_created}",
                        ClientSecret = ClientSecretOf(amount),
                        Amount = amount,
                        Status = "requires_payment_method",
                    };
                    _intents[intent.Id] = intent;
                    LastIntentId = intent.Id;
                    return intent;
                });
            _stripe
                .Setup(s => s.GetPaymentIntentAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string id, string? _, CancellationToken _) => _intents[id]);
            _stripe
                .Setup(s => s.CancelPaymentIntentAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string id, string? _, string _, CancellationToken _) =>
                {
                    CancelledIntents.Add(id);
                    _intents[id].Status = "canceled";
                    return _intents[id];
                });
        }

        /// <summary>The PaymentIntent the last link made.</summary>
        public string LastIntentId { get; private set; } = string.Empty;

        /// <summary>The PaymentIntents cancelled on Stripe, in order.</summary>
        public List<string> CancelledIntents { get; } = [];

        /// <summary>What Stripe says of a PaymentIntent from now on (the guest pays, or is paying).</summary>
        public void SetIntentStatus(string intentId, string status) => _intents[intentId].Status = status;

        /// <summary>A cash receipt the host records by hand, in an authenticated request of org A.</summary>
        public async Task<ManualPaymentResult> RecordCashAsync(Guid bookingId, decimal amount, string key)
        {
            await using var db = NewDb(new HostTenant(OrgA));
            var service = new ManualPaymentService(
                db,
                Mock.Of<IFiscalRegimeService>(),
                NullLogger<ManualPaymentService>.Instance,
                new FixedTimeProvider(new DateTimeOffset(2026, 11, 6, 9, 0, 0, TimeSpan.Zero)),
                null,
                _stripe.Object);
            return await service.RecordAsync(
                new ManualPaymentCommand(bookingId, amount, Casazen.Core.Entities.PaymentMethod.CashOnArrival, null, null, key));
        }

        public string ClientSecretOf(long amountCents) => $"secret_for_{amountCents}";

        public AppDbContext NewDb(ITenantContext? tenant) =>
            new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(_database).Options, tenant ?? NullTenantContext.Instance);

        public async Task<Guid> SeedHostBookingAsync(decimal total)
        {
            await using var db = NewDb(null);
            var guest = new Guest { OrgId = OrgA, FirstName = "Giulia", LastName = "Rossi", Email = $"{Guid.NewGuid():N}@example.com" };
            var booking = new Booking
            {
                OrgId = OrgA,
                PropertyId = _propertyId,
                GuestId = guest.Id,
                CheckInDate = new DateTime(2026, 12, 10, 0, 0, 0, DateTimeKind.Utc),
                CheckOutDate = new DateTime(2026, 12, 13, 0, 0, 0, DateTimeKind.Utc),
                NumberOfGuests = 2,
                NumberOfAdults = 2,
                Status = BookingStatus.Confirmed,
                Source = BookingSource.Manual,
                HostPaymentArrangement = HostPaymentArrangement.PaymentLink,
                BasePrice = total,
                TotalPrice = total,
            };
            db.Guests.Add(guest);
            db.Bookings.Add(booking);
            await db.SaveChangesAsync();
            return booking.Id;
        }

        /// <summary>A link made in an authenticated request of org A (the tenant filter of the host).</summary>
        public async Task<BookingPaymentLink> LinkAsync(Guid bookingId)
        {
            await using var db = NewDb(new HostTenant(OrgA));
            var emails = new Mock<IEmailQueue>();
            emails.Setup(e => e.Enqueue(It.IsAny<string?>(), It.IsAny<EmailContent>(), It.IsAny<string>())).Returns(true);
            var links = new PublicSiteLinks(Microsoft.Extensions.Options.Options.Create(new PublicSiteOptions { PublicSiteBaseUrl = "https://casazen.test" }));
            var service = new BookingPaymentLinkService(
                db,
                _stripe.Object,
                emails.Object,
                links,
                NullLogger<BookingPaymentLinkService>.Instance,
                new FixedTimeProvider(new DateTimeOffset(2026, 11, 6, 9, 0, 0, TimeSpan.Zero)));
            return await service.CreateAsync(bookingId, CancellationToken.None);
        }

        /// <summary>The anonymous checkout page of the guest (no tenant: the token is the only check).</summary>
        public CheckoutOutcomeService Outcomes()
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Stripe:PublishableKey"] = "pk_test_link" })
                .Build();
            return new CheckoutOutcomeService(
                NewDb(null),
                _stripe.Object,
                configuration,
                NullLogger<CheckoutOutcomeService>.Instance,
                new FixedTimeProvider(new DateTimeOffset(2026, 11, 6, 9, 0, 0, TimeSpan.Zero)));
        }
    }

    private sealed class HostTenant(Guid orgId) : ITenantContext
    {
        public Guid? OrgId { get; } = orgId;

        public bool FilterEnabled => true;
    }
}
