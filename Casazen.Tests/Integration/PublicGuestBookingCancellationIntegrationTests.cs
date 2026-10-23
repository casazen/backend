using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.External;
using Casazen.Tests.Unit.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using PaymentMethod = Casazen.Core.Entities.PaymentMethod;

namespace Casazen.Tests.Integration;

/// <summary>
/// DB-08h (BK-02, BK-07) over the real pipeline (routing, rate limit, ProblemDetails, EF) with a fake Stripe: the link a
/// host sends cancels a paid booking and answers 200 with the policy's refund (percent of the base price, tourist tax
/// excluded). Before the fix every booking whose guest had paid the tourist tax answered 422 and burned the link.
/// </summary>
public class PublicGuestBookingCancellationIntegrationTests : IClassFixture<CasazenWebApplicationFactory>
{
    private const string Account = "acct_it_guest_cancel";

    private readonly CasazenWebApplicationFactory _factory;

    public PublicGuestBookingCancellationIntegrationTests(CasazenWebApplicationFactory factory) => _factory = factory;

    private FakeStripeService Stripe => (FakeStripeService)_factory.Services.GetRequiredService<IStripeService>();

    [Fact]
    public async Task Cancel_PaidBookingWithTouristTaxInThePartialWindow_Returns200AndRefundsHalfOfTheBasePrice()
    {
        var seed = await SeedAsync(daysToCheckIn: 10); // Contenuta: 50% from 14 to 7 days before check-in
        using var guest = _factory.CreateClient();

        var response = await guest.PostAsync(CancelUrl(seed), null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(seed.BookingId, body.GetProperty("bookingId").GetGuid());
        Assert.Equal("Cancelled", body.GetProperty("status").GetString());
        Assert.Equal(200m, body.GetProperty("refundAmount").GetDecimal());
        var request = Assert.Single(Stripe.RefundRequests, r => r.PaymentIntentId == seed.PaymentIntentId);
        Assert.Equal(Account, request.ConnectedAccountId);
        Assert.Equal(20_000, request.AmountCents);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var booking = await db.Bookings.IgnoreQueryFilters().SingleAsync(b => b.Id == seed.BookingId);
        Assert.Equal(BookingStatus.Cancelled, booking.Status);
        Assert.Equal(GuestCancellationTokens.HashToken(seed.Token), booking.GuestCancelTokenHash); // recognised again, but spent
        Assert.True(booking.GuestCancelTokenExpiresAt <= DateTime.UtcNow);
        var refund = await db.PaymentRefunds.IgnoreQueryFilters().SingleAsync(r => r.PaymentId == seed.PaymentId);
        Assert.Equal(200m, refund.Amount);
        Assert.Equal(PaymentRefundOrigin.BookingCancellation, refund.Origin);
    }

    [Fact]
    public async Task Cancel_PaidBookingWithTouristTaxInTheFullRefundWindow_RefundsTheBasePriceAndNotTheTax()
    {
        var seed = await SeedAsync(daysToCheckIn: 30);
        using var guest = _factory.CreateClient();

        var response = await guest.PostAsync(CancelUrl(seed), null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(400m, body.GetProperty("refundAmount").GetDecimal()); // paid 420: the 20 of tourist tax stays out
        Assert.Equal(40_000, Assert.Single(Stripe.RefundRequests, r => r.PaymentIntentId == seed.PaymentIntentId).AmountCents);
    }

    [Fact]
    public async Task Cancel_AfterTheRefundWindows_Returns200WithNothingRefunded()
    {
        var seed = await SeedAsync(daysToCheckIn: 3); // Contenuta: 0% within 7 days
        using var guest = _factory.CreateClient();

        var response = await guest.PostAsync(CancelUrl(seed), null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Cancelled", body.GetProperty("status").GetString());
        Assert.Equal(0m, body.GetProperty("refundAmount").GetDecimal());
        Assert.DoesNotContain(Stripe.RefundRequests, r => r.PaymentIntentId == seed.PaymentIntentId);
    }

    [Fact]
    public async Task Cancel_WrongToken_Returns422AndTheRightLinkStillWorks()
    {
        var seed = await SeedAsync(daysToCheckIn: 10);
        using var guest = _factory.CreateClient();

        var wrong = await guest.PostAsync($"/api/public/bookings/{seed.BookingId}/cancel?token=not-the-token", null);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, wrong.StatusCode);
        var problem = await wrong.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("guest_cancel_token_invalid", problem.GetProperty("code").GetString());
        Assert.DoesNotContain(Stripe.RefundRequests, r => r.PaymentIntentId == seed.PaymentIntentId);

        var right = await guest.PostAsync(CancelUrl(seed), null);
        Assert.Equal(HttpStatusCode.OK, right.StatusCode);
    }

    [Fact]
    public async Task Cancel_PaymentBeingConfirmed_Returns409AndTheSameLinkWorksOnceThePaymentLands()
    {
        var seed = await SeedAsync(daysToCheckIn: 10, paymentStatus: PaymentStatus.Pending, bookingStatus: BookingStatus.Pending);
        FakeStripeService.SetIntentStatus(seed.PaymentIntentId, "processing");
        using var guest = _factory.CreateClient();

        var busy = await guest.PostAsync(CancelUrl(seed), null);

        Assert.Equal(HttpStatusCode.Conflict, busy.StatusCode);
        var problem = await busy.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("booking_payment_in_progress", problem.GetProperty("code").GetString());
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var booking = await db.Bookings.IgnoreQueryFilters().SingleAsync(b => b.Id == seed.BookingId);
            Assert.Equal(BookingStatus.Pending, booking.Status);
            Assert.Equal(GuestCancellationTokens.HashToken(seed.Token), booking.GuestCancelTokenHash);

            // The webhook records the payment.
            var payment = await db.Payments.IgnoreQueryFilters().SingleAsync(p => p.Id == seed.PaymentId);
            payment.Status = PaymentStatus.Completed;
            await db.SaveChangesAsync();
        }

        FakeStripeService.SetIntentStatus(seed.PaymentIntentId, "succeeded");
        var retry = await guest.PostAsync(CancelUrl(seed), null);

        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        var body = await retry.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(200m, body.GetProperty("refundAmount").GetDecimal());
    }

    [Fact]
    public async Task Cancel_LinkUsedAgain_AnswersTheSameOutcomeAndRefundsOnce()
    {
        var seed = await SeedAsync(daysToCheckIn: 10);
        using var guest = _factory.CreateClient();

        var first = await guest.PostAsync(CancelUrl(seed), null);
        var second = await guest.PostAsync(CancelUrl(seed), null);
        var third = await guest.PostAsync(CancelUrl(seed), null);

        foreach (var response in new[] { first, second, third })
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(seed.BookingId, body.GetProperty("bookingId").GetGuid());
            Assert.Equal("Cancelled", body.GetProperty("status").GetString());
            Assert.Equal(200m, body.GetProperty("refundAmount").GetDecimal());
        }

        Assert.Single(Stripe.RefundRequests, r => r.PaymentIntentId == seed.PaymentIntentId);
    }

    [Fact]
    public async Task Cancel_BookingAlreadyCancelledByTheHost_Returns200WithThatCancellationsRefundAndRefundsNothingMore()
    {
        var seed = await SeedAsync(daysToCheckIn: 10, bookingStatus: BookingStatus.Cancelled);
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.PaymentRefunds.Add(new PaymentRefund
            {
                PaymentId = seed.PaymentId,
                OrgId = seed.OrgId,
                Amount = 420m,
                Status = PaymentRefundStatus.Succeeded,
                Origin = PaymentRefundOrigin.BookingCancellation,
                IdempotencyKey = $"payment-refund:{Guid.NewGuid():N}",
            });
            await db.SaveChangesAsync();
        }

        using var guest = _factory.CreateClient();

        var response = await guest.PostAsync(CancelUrl(seed), null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Cancelled", body.GetProperty("status").GetString());
        Assert.Equal(420m, body.GetProperty("refundAmount").GetDecimal());
        Assert.DoesNotContain(Stripe.RefundRequests, r => r.PaymentIntentId == seed.PaymentIntentId);
    }

    [Fact]
    public async Task Cancel_WithoutToken_Returns400()
    {
        var seed = await SeedAsync(daysToCheckIn: 10);
        using var guest = _factory.CreateClient();

        var response = await guest.PostAsync($"/api/public/bookings/{seed.BookingId}/cancel", null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("guest_cancel_token_missing", problem.GetProperty("code").GetString());
    }

    private static string CancelUrl(Seed seed) => $"/api/public/bookings/{seed.BookingId}/cancel?token={seed.Token}";

    private sealed record Seed(Guid OrgId, Guid BookingId, Guid PaymentId, string PaymentIntentId, string Token);

    /// <summary>
    /// A booking of 400 (lodging and cleaning) + 20 of tourist tax, paid 420 through Stripe, under the policy "Contenuta"
    /// (100% from 14 days before check-in, 50% from 7 days, 0% after) with a cancel link the host has sent.
    /// </summary>
    private async Task<Seed> SeedAsync(
        int daysToCheckIn,
        PaymentStatus paymentStatus = PaymentStatus.Completed,
        BookingStatus bookingStatus = BookingStatus.Confirmed)
    {
        var hostId = $"auth0|db08h-{Guid.NewGuid():N}";
        var org = await _factory.SeedOrgForOwnerAsync(hostId);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var storedOrg = await db.Orgs.SingleAsync(o => o.Id == org.Id);
        storedOrg.StripeConnectedAccountId = Account;
        storedOrg.ConnectChargesEnabled = true;

        var policy = CatalogPolicies.Get(CatalogPolicies.Contenuta);
        var property = new Property
        {
            OwnerId = hostId,
            OrgId = org.Id,
            Name = "DB-08h Villa",
            Address = $"Via Annullo {Guid.NewGuid():N}",
            City = "Roma",
            PostalCode = "00100",
            MaxGuests = 4,
            NightlyRate = 100m,
            IsActive = true,
            CancellationPolicy = policy,
        };
        var guest = new Guest
        {
            OrgId = org.Id,
            FirstName = "Anna",
            LastName = "Verdi",
            Email = $"db08h-{Guid.NewGuid():N}@example.com",
        };
        var token = GuestCancellationTokens.NewToken();
        var today = TimeProvider.System.TodayInRome();
        var booking = new Booking
        {
            PropertyId = property.Id,
            OrgId = org.Id,
            GuestId = guest.Id,
            CheckInDate = today.AddDays(daysToCheckIn),
            CheckOutDate = today.AddDays(daysToCheckIn + 3),
            NumberOfGuests = 2,
            Status = bookingStatus,
            Source = BookingSource.Direct,
            BasePrice = 400m,
            TouristTax = 20m,
            TotalPrice = 420m,
            GuestCancelTokenHash = GuestCancellationTokens.HashToken(token),
            GuestCancelTokenExpiresAt = DateTime.UtcNow.AddDays(7),
        };
        var paymentIntentId = $"pi_it_{Guid.NewGuid():N}";
        var payment = new Payment
        {
            BookingId = booking.Id,
            OrgId = org.Id,
            Amount = 420m,
            Status = paymentStatus,
            Method = PaymentMethod.CreditCard,
            TransactionId = paymentIntentId,
            StripePaymentIntentId = paymentIntentId,
            StripeAccountId = Account,
        };

        db.AddRange(property, guest, booking, payment);
        await db.SaveChangesAsync();
        return new Seed(org.Id, booking.Id, payment.Id, paymentIntentId, token);
    }
}
