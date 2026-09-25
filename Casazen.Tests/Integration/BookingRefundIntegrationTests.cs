using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.External;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using PaymentMethod = Casazen.Core.Entities.PaymentMethod;

namespace Casazen.Tests.Integration;

/// <summary>
/// BK-02 (A3-05, A9-15) over the real pipeline (auth, TN-3, ProblemDetails, PostgreSQL): refunds and cancellations
/// reach Stripe (fake) with the host's connected account, and the answers never claim a refund Stripe has not confirmed.
/// </summary>
public class BookingRefundIntegrationTests : IClassFixture<CasazenWebApplicationFactory>
{
    private const string Account = "acct_it_refunds";

    private readonly CasazenWebApplicationFactory _factory;

    public BookingRefundIntegrationTests(CasazenWebApplicationFactory factory) => _factory = factory;

    private FakeStripeService Stripe => (FakeStripeService)_factory.Services.GetRequiredService<IStripeService>();

    [Fact]
    public async Task PostRefund_PaidStripePayment_RefundsOnConnectedAccountAndReportsStripeStatus()
    {
        var seed = await SeedAsync(BookingStatus.Confirmed, PaymentStatus.Completed, 300m);
        using var host = _factory.CreateAuthenticatedClient(seed.HostId, "PropertyOwner");

        var response = await host.PostAsJsonAsync($"/api/payments/{seed.PaymentId}/refund", new { amount = 100m, reason = "Guasto" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Succeeded", body.GetProperty("status").GetString());
        var request = Assert.Single(Stripe.RefundRequests, r => r.PaymentIntentId == seed.PaymentIntentId);
        Assert.Equal(Account, request.ConnectedAccountId);
        Assert.Equal(10_000, request.AmountCents);
        Assert.Equal($"payment-refund:{body.GetProperty("id").GetGuid():N}", request.IdempotencyKey);

        var refunds = await host.GetFromJsonAsync<JsonElement>($"/api/payments/{seed.PaymentId}/refunds");
        Assert.Equal(100m, refunds.GetProperty("refundedAmount").GetDecimal());
        Assert.Equal(200m, refunds.GetProperty("refundableAmount").GetDecimal());
        var payment = await host.GetFromJsonAsync<JsonElement>($"/api/payments/{seed.PaymentId}");
        Assert.Equal("PartiallyRefunded", payment.GetProperty("status").GetString());
    }

    [Fact]
    public async Task PostRefund_AmountAboveRefundable_Returns422WithCodeAndCallsNoStripe()
    {
        var seed = await SeedAsync(BookingStatus.Confirmed, PaymentStatus.Completed, 300m);
        using var host = _factory.CreateAuthenticatedClient(seed.HostId, "PropertyOwner");

        var response = await host.PostAsJsonAsync($"/api/payments/{seed.PaymentId}/refund", new { amount = 300.5m });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("refund_amount_exceeds_refundable", problem.GetProperty("code").GetString());
        Assert.DoesNotContain(Stripe.RefundRequests, r => r.PaymentIntentId == seed.PaymentIntentId);
    }

    [Fact]
    public async Task PostRefund_ProcessEndpoint_NoLongerExists()
    {
        var seed = await SeedAsync(BookingStatus.Confirmed, PaymentStatus.Pending, 300m);
        using var host = _factory.CreateAuthenticatedClient(seed.HostId, "PropertyOwner");

        var response = await host.PostAsync($"/api/payments/{seed.PaymentId}/process", null);

        Assert.True(response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(PaymentStatus.Pending, (await db.Payments.IgnoreQueryFilters().SingleAsync(p => p.Id == seed.PaymentId)).Status);
    }

    [Fact]
    public async Task PostCancel_PaidBookingWithChosenRefund_CancelsAndRefundsOnConnectedAccount()
    {
        var seed = await SeedAsync(BookingStatus.Confirmed, PaymentStatus.Completed, 400m);
        using var host = _factory.CreateAuthenticatedClient(seed.HostId, "PropertyOwner");

        var quote = await host.GetFromJsonAsync<JsonElement>($"/api/bookings/{seed.BookingId}/cancellation");
        Assert.True(quote.GetProperty("requiresRefundDecision").GetBoolean());
        Assert.Equal(400m, quote.GetProperty("refundableAmount").GetDecimal());

        var missingDecision = await host.PostAsJsonAsync($"/api/bookings/{seed.BookingId}/cancel", new { });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, missingDecision.StatusCode);
        Assert.Equal(
            "booking_cancel_refund_required",
            (await missingDecision.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

        var response = await host.PostAsJsonAsync($"/api/bookings/{seed.BookingId}/cancel", new { refundAmount = 250m });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Cancelled", body.GetProperty("status").GetString());
        var refund = Assert.Single(body.GetProperty("refunds").EnumerateArray());
        Assert.Equal("Succeeded", refund.GetProperty("status").GetString());
        Assert.Equal("BookingCancellation", refund.GetProperty("origin").GetString());
        var request = Assert.Single(Stripe.RefundRequests, r => r.PaymentIntentId == seed.PaymentIntentId);
        Assert.Equal(Account, request.ConnectedAccountId);
        Assert.Equal(25_000, request.AmountCents);
    }

    [Fact]
    public async Task PostCancel_PendingBookingWithUnpaidIntent_CancelsIntentOnConnectedAccount()
    {
        var seed = await SeedAsync(BookingStatus.Pending, PaymentStatus.Pending, 180m);
        using var host = _factory.CreateAuthenticatedClient(seed.HostId, "PropertyOwner");

        var response = await host.PostAsync($"/api/bookings/{seed.BookingId}/cancel", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(Stripe.CanceledPaymentIntents, c => c.PaymentIntentId == seed.PaymentIntentId && c.AccountId == Account);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(BookingStatus.Cancelled, (await db.Bookings.IgnoreQueryFilters().SingleAsync(b => b.Id == seed.BookingId)).Status);
        Assert.Equal(PaymentStatus.Canceled, (await db.Payments.IgnoreQueryFilters().SingleAsync(p => p.Id == seed.PaymentId)).Status);
    }

    [Fact]
    public async Task DeleteBooking_OldCancelWithoutRefund_NoLongerExists()
    {
        var seed = await SeedAsync(BookingStatus.Confirmed, PaymentStatus.Completed, 200m);
        using var host = _factory.CreateAuthenticatedClient(seed.HostId, "PropertyOwner");

        var response = await host.DeleteAsync($"/api/bookings/{seed.BookingId}");

        Assert.True(response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(BookingStatus.Confirmed, (await db.Bookings.IgnoreQueryFilters().SingleAsync(b => b.Id == seed.BookingId)).Status);
    }

    private sealed record Seed(string HostId, Guid BookingId, Guid PaymentId, string PaymentIntentId);

    private async Task<Seed> SeedAsync(BookingStatus bookingStatus, PaymentStatus paymentStatus, decimal amount)
    {
        var hostId = $"auth0|bk02-{Guid.NewGuid():N}";
        var org = await _factory.SeedOrgForOwnerAsync(hostId);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var storedOrg = await db.Orgs.SingleAsync(o => o.Id == org.Id);
        storedOrg.StripeConnectedAccountId = Account;
        storedOrg.ConnectChargesEnabled = true;

        var property = new Property
        {
            OwnerId = hostId,
            OrgId = org.Id,
            Name = "BK-02 Villa",
            Address = $"Via Rimborsi {Guid.NewGuid():N}",
            City = "Roma",
            PostalCode = "00100",
            MaxGuests = 4,
            NightlyRate = 100m,
            IsActive = true,
        };
        var guest = new Guest
        {
            OrgId = org.Id,
            FirstName = "Anna",
            LastName = "Verdi",
            Email = $"bk02-{Guid.NewGuid():N}@example.com",
        };
        var booking = new Booking
        {
            PropertyId = property.Id,
            OrgId = org.Id,
            GuestId = guest.Id,
            CheckInDate = TimeProvider.System.TodayInRome().AddDays(3),
            CheckOutDate = TimeProvider.System.TodayInRome().AddDays(5),
            NumberOfGuests = 2,
            Status = bookingStatus,
            Source = BookingSource.Direct,
            TotalPrice = amount,
            FreeRefundDeadline = TimeProvider.System.TodayInRome().AddDays(-4),
        };
        var paymentIntentId = $"pi_it_{Guid.NewGuid():N}";
        var payment = new Payment
        {
            BookingId = booking.Id,
            OrgId = org.Id,
            Amount = amount,
            Status = paymentStatus,
            Method = PaymentMethod.CreditCard,
            TransactionId = paymentIntentId,
            StripePaymentIntentId = paymentIntentId,
            StripeAccountId = Account,
        };

        db.AddRange(property, guest, booking, payment);
        await db.SaveChangesAsync();
        return new Seed(hostId, booking.Id, payment.Id, paymentIntentId);
    }
}
