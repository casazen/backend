using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Email.Templates;
using Casazen.Infrastructure.External;
using Casazen.Tests.Integration.Postgres;
using Casazen.Tests.Unit.Email;
using Casazen.Tests.Unit.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using PaymentMethod = Casazen.Core.Entities.PaymentMethod;

namespace Casazen.Tests.Integration;

/// <summary>
/// DB-08a on real PostgreSQL, over the real pipeline and a fake Stripe: the money rule of the guest cancelling from the guest area, in one
/// run. The amount the guest is shown is the amount Stripe is asked to refund; the policy is the one of each property; the window of the
/// free cancellation is the date the policy ends it on; a repeated request refunds once, whatever its key; and a guest who is not the
/// holder of the booking gets the same 404 as for a booking that does not exist, can cancel nothing and learns nothing.
/// </summary>
public class GuestSelfCancellationPostgresTests : IClassFixture<GuestSelfCancellationPostgresTests.CancelFactory>
{
    private const string Account = "acct_db08a_guest_cancel";
    private const string SessionPath = "/api/public/guest/session";
    private const string BookingsPath = "/api/public/guest/bookings";

    private static int _nextPeer;

    private readonly CancelFactory _factory;

    public GuestSelfCancellationPostgresTests(CancelFactory factory) => _factory = factory;

    private FakeStripeService Stripe => (FakeStripeService)_factory.Services.GetRequiredService<IStripeService>();

    [PostgresFact]
    public async Task GuestArea_TheAmountShownIsTheAmountRefunded_PerPropertyPolicy_OnceOnly_AndNeverForAnotherGuest()
    {
        // One site, two properties with different policies, a guest with a paid booking at each (400 of stay + 20 of tourist tax = 420,
        // check-in in 10 days), and another guest of the same site with a booking of their own.
        var site = await SeedSiteAsync();
        var intermedia = await SeedBookingAsync(site, site.IntermediaPropertyId, "holder@example.com", daysToCheckIn: 10);
        var anticipata = await SeedBookingAsync(site, site.AnticipataPropertyId, "holder@example.com", daysToCheckIn: 10);
        var stranger = await SeedBookingAsync(site, site.IntermediaPropertyId, "stranger@example.com", daysToCheckIn: 20);
        var holderToken = await OpenSessionAsync(site, intermedia);
        var strangerToken = await OpenSessionAsync(site, stranger);

        // The preview of each booking follows the policy of its property: 10 days ahead is 100% under "Intermedia" (full refund up
        // to 5 days before) and 50% under "Anticipata" (half from 30 to 7 days before). The tourist tax comes back in full besides.
        var previewIntermedia = await ReadAsync(await GetAsync($"{BookingsPath}/{intermedia.Code}/cancellation", holderToken));
        var previewAnticipata = await ReadAsync(await GetAsync($"{BookingsPath}/{anticipata.Code}/cancellation", holderToken));
        Assert.True(previewIntermedia.GetProperty("cancellable").GetBoolean());
        Assert.Equal("cancel", previewIntermedia.GetProperty("kind").GetString());
        Assert.Equal(100m, previewIntermedia.GetProperty("refundPercent").GetDecimal());
        Assert.Equal(420m, previewIntermedia.GetProperty("refundAmount").GetDecimal());
        Assert.Equal(0m, previewIntermedia.GetProperty("retainedAmount").GetDecimal());
        Assert.Equal(50m, previewAnticipata.GetProperty("refundPercent").GetDecimal());
        Assert.Equal(220m, previewAnticipata.GetProperty("refundAmount").GetDecimal());
        Assert.Equal(200m, previewAnticipata.GetProperty("retainedAmount").GetDecimal());
        Assert.Equal("policy", previewAnticipata.GetProperty("retainedBecause").GetString());

        // The window of the free cancellation is the last whole day at least 120 hours before the check-in day starts.
        var expectedFreeUntil = RomeCalendar.DateInRome(RomeCalendar.StartOfDayUtc(intermedia.CheckIn).AddHours(-120)).AddDays(-1);
        Assert.Equal(expectedFreeUntil.ToString("yyyy-MM-dd"), previewIntermedia.GetProperty("fullRefundUntil").GetString());

        // Another guest of the site, with a valid session of their own: the booking of the holder is a 404 like a code that does not
        // exist, for the preview and for the cancellation alike, and nothing happens to it.
        var refusals = new[]
        {
            await GetAsync($"{BookingsPath}/{intermedia.Code}/cancellation", strangerToken),
            await PostCancelAsync(intermedia.Code, strangerToken, expected: 420m, key: Key()),
            await GetAsync($"{BookingsPath}/ZZZZZ-ZZZZZ/cancellation", strangerToken),
        };
        var shapes = new List<string>();
        foreach (var refusal in refusals)
        {
            Assert.Equal(HttpStatusCode.NotFound, refusal.StatusCode);
            var text = await refusal.Content.ReadAsStringAsync();
            var problem = JsonDocument.Parse(text).RootElement;
            Assert.Equal(GuestBookingLookupErrorCodes.NotFound, problem.GetProperty("code").GetString());
            Assert.DoesNotContain(site.IntermediaName, text, StringComparison.Ordinal);
            shapes.Add(JsonSerializer.Serialize(new
            {
                status = problem.GetProperty("status").GetInt32(),
                code = problem.GetProperty("code").GetString(),
                title = problem.GetProperty("title").GetString(),
                detail = problem.GetProperty("detail").GetString(),
            }));
        }

        Assert.Single(shapes.Distinct(StringComparer.Ordinal));
        Assert.Equal(BookingStatus.Confirmed, await StatusOfAsync(intermedia.BookingId));
        Assert.DoesNotContain(Stripe.RefundRequests, r => r.PaymentIntentId == intermedia.IntentId);

        // The guest confirms an amount that is not the one shown: nothing is cancelled and the new figure comes back with the 409.
        var wrong = await PostCancelAsync(intermedia.Code, holderToken, expected: 400m, key: Key());
        Assert.Equal(HttpStatusCode.Conflict, wrong.StatusCode);
        var wrongProblem = await ReadAsync(wrong);
        Assert.Equal("refund_changed", wrongProblem.GetProperty("code").GetString());
        Assert.Equal(420m, wrongProblem.GetProperty("preview").GetProperty("refundAmount").GetDecimal());
        Assert.Equal(BookingStatus.Confirmed, await StatusOfAsync(intermedia.BookingId));
        Assert.DoesNotContain(Stripe.RefundRequests, r => r.PaymentIntentId == intermedia.IntentId);

        // The guest confirms the amount they were shown: that is what Stripe is asked to refund, and the refund is not "done" until
        // Stripe says so (the fake confirms it at once here).
        var firstKey = Key();
        var cancelled = await PostCancelAsync(intermedia.Code, holderToken, expected: 420m, key: firstKey);
        Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);
        var cancelledBody = await ReadAsync(cancelled);
        Assert.Equal("Cancelled", cancelledBody.GetProperty("status").GetString());
        Assert.Equal(420m, cancelledBody.GetProperty("refundAmount").GetDecimal());
        Assert.Equal("confirmed", cancelledBody.GetProperty("refundStatus").GetString());
        var asked = Assert.Single(Stripe.RefundRequests, r => r.PaymentIntentId == intermedia.IntentId);
        Assert.Equal(Account, asked.ConnectedAccountId);
        Assert.Equal(42_000, asked.AmountCents);

        // The same click again, with the same key and with another: the same answer and no second refund.
        foreach (var key in new[] { firstKey, Key() })
        {
            var again = await PostCancelAsync(intermedia.Code, holderToken, expected: 420m, key: key);
            Assert.Equal(HttpStatusCode.OK, again.StatusCode);
            var againBody = await ReadAsync(again);
            Assert.Equal("Cancelled", againBody.GetProperty("status").GetString());
            Assert.Equal(420m, againBody.GetProperty("refundAmount").GetDecimal());
        }

        Assert.Single(Stripe.RefundRequests, r => r.PaymentIntentId == intermedia.IntentId);
        Assert.DoesNotContain(Stripe.RefundRequests, r => r.PaymentIntentId == anticipata.IntentId); // the other booking was not touched
        Assert.Equal(BookingStatus.Confirmed, await StatusOfAsync(anticipata.BookingId));

        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var booking = await db.Bookings.IgnoreQueryFilters().SingleAsync(b => b.Id == intermedia.BookingId);
            Assert.Equal(BookingStatus.Cancelled, booking.Status);
            Assert.Equal(BookingCancellationReason.GuestRequested, booking.CancellationReason);
            var refund = await db.PaymentRefunds.IgnoreQueryFilters().SingleAsync(r => r.Payment.BookingId == intermedia.BookingId);
            Assert.Equal(420m, refund.Amount);
            Assert.Equal(PaymentRefundOrigin.BookingCancellation, refund.Origin);
        }

        // The preview of a cancelled booking says so, with the outcome; the guest and the host were each told once.
        var after = await ReadAsync(await GetAsync($"{BookingsPath}/{intermedia.Code}/cancellation", holderToken));
        Assert.False(after.GetProperty("cancellable").GetBoolean());
        Assert.Equal("already_cancelled", after.GetProperty("reason").GetString());
        Assert.Equal(420m, after.GetProperty("outcome").GetProperty("refundAmount").GetDecimal());
        var emails = _factory.Emails.Snapshot();
        Assert.Single(emails, e => e.Template == EmailTemplates.Names.GuestBookingCancelled && e.To == "holder@example.com");
        Assert.Single(emails, e => e.Template == EmailTemplates.Names.HostBookingCancelledByGuest && e.To == site.ContactEmail);
    }

    private static string Key() => $"k-{Guid.NewGuid():N}";

    private async Task<BookingStatus> StatusOfAsync(Guid bookingId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Bookings.IgnoreQueryFilters().Where(b => b.Id == bookingId).Select(b => b.Status).SingleAsync();
    }

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    private async Task<string> OpenSessionAsync(SiteSeed site, BookingSeed booking)
    {
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, SessionPath)
        {
            Content = JsonContent.Create(new { orgSlug = site.Slug, bookingCode = booking.Code, email = booking.Email }),
        };
        request.Headers.Add(TestPeerIpStartupFilter.HeaderName, NextPeer());
        var opened = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, opened.StatusCode);
        return (await ReadAsync(opened)).GetProperty("accessToken").GetString()!;
    }

    private async Task<HttpResponseMessage> GetAsync(string path, string bearerToken)
    {
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add(TestPeerIpStartupFilter.HeaderName, NextPeer());
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        return await client.SendAsync(request);
    }

    private async Task<HttpResponseMessage> PostCancelAsync(string code, string bearerToken, decimal expected, string key)
    {
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{BookingsPath}/{code}/cancel")
        {
            Content = JsonContent.Create(new { confirm = true, expectedRefundAmount = expected }),
        };
        request.Headers.Add(TestPeerIpStartupFilter.HeaderName, NextPeer());
        request.Headers.Add("Idempotency-Key", key);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        return await client.SendAsync(request);
    }

    private static string NextPeer()
    {
        var n = Interlocked.Increment(ref _nextPeer);
        return $"198.19.{n / 250}.{(n % 250) + 1}";
    }

    private sealed record SiteSeed(
        Guid OrgId,
        string Slug,
        string ContactEmail,
        Guid IntermediaPropertyId,
        string IntermediaName,
        Guid AnticipataPropertyId);

    private sealed record BookingSeed(Guid BookingId, string Code, string Email, string IntentId, DateTime CheckIn);

    /// <summary>One site with two properties: "Intermedia" (full refund up to 5 days before, then 50%) and "Anticipata" (30 days, then 50% down to 7).</summary>
    private async Task<SiteSeed> SeedSiteAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var org = new OrgEntity
        {
            Name = "Villa Annullo Srl",
            Slug = $"annullo-{Guid.NewGuid():N}",
            DisplayName = "Villa Annullo Srl",
            ContactEmail = $"host.{Guid.NewGuid():N}@example.com",
            PlanTier = PlanTier.Starter,
            IsActive = true,
            StripeConnectedAccountId = Account,
            ConnectChargesEnabled = true,
        };
        var intermedia = NewProperty(org, "Casa Intermedia", CatalogPolicies.Intermedia);
        var anticipata = NewProperty(org, "Casa Anticipata", CatalogPolicies.Anticipata);
        db.AddRange(org, intermedia, anticipata);
        await db.SaveChangesAsync();
        return new SiteSeed(org.Id, org.Slug, org.ContactEmail, intermedia.Id, intermedia.Name, anticipata.Id);
    }

    private static Property NewProperty(OrgEntity org, string name, string policy) => new()
    {
        OwnerId = $"auth0|db08a-{Guid.NewGuid():N}",
        OrgId = org.Id,
        Name = $"{name} {Guid.NewGuid():N}"[..28],
        Description = "DB-08a",
        Address = $"Via Annullo {Guid.NewGuid():N}",
        City = "Roma",
        PostalCode = "00100",
        MaxGuests = 4,
        NightlyRate = 100m,
        CleaningFee = 50m,
        CinCode = "IT058091C27G5FFZDZ",
        IsActive = true,
        ComplianceStatus = PropertyComplianceStatus.Active,
        CancellationPolicy = CatalogPolicies.Get(policy),
    };

    /// <summary>A paid booking of the public checkout: 350 of lodging + 50 of cleaning + 20 of tourist tax = 420, collected through Stripe.</summary>
    private async Task<BookingSeed> SeedBookingAsync(SiteSeed site, Guid propertyId, string email, int daysToCheckIn)
    {
        var checkIn = TimeProvider.System.TodayInRome().AddDays(daysToCheckIn);
        var guest = new Guest
        {
            OrgId = site.OrgId,
            FirstName = "Giulia",
            LastName = "Bianchi",
            Email = email,
            PhoneNumber = "+393331112222",
            DataProcessingPurpose = "Direct Booking Checkout",
        };
        var booking = new Booking
        {
            PropertyId = propertyId,
            OrgId = site.OrgId,
            GuestId = guest.Id,
            CheckInDate = checkIn,
            CheckOutDate = checkIn.AddDays(3),
            NumberOfGuests = 2,
            NumberOfAdults = 2,
            Status = BookingStatus.Confirmed,
            Source = BookingSource.Direct,
            PaymentOption = PaymentOption.Immediate,
            BasePrice = 400m,
            CleaningFee = 50m,
            TouristTax = 20m,
            TouristTaxAmount = 20m,
            TotalPrice = 420m,
            FreeRefundDeadline = checkIn.AddDays(-7),
        };
        var intentId = $"pi_db08a_{Guid.NewGuid():N}";
        var payment = new Payment
        {
            BookingId = booking.Id,
            OrgId = site.OrgId,
            Amount = 420m,
            Status = PaymentStatus.Completed,
            Method = PaymentMethod.CreditCard,
            TransactionId = intentId,
            StripePaymentIntentId = intentId,
            StripeAccountId = Account,
            ProcessedAt = DateTime.UtcNow,
        };

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.AddRange(guest, booking, payment);
        await db.SaveChangesAsync();
        return new BookingSeed(booking.Id, BookingCodes.Format(booking.BookingCode), email, intentId, checkIn);
    }

    /// <summary>The integration host with a recording email queue and client IPs from <see cref="TestPeerIpStartupFilter"/>.</summary>
    public sealed class CancelFactory : CasazenWebApplicationFactory
    {
        internal RecordingEmailQueue Emails { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                RemoveAllOf<IEmailQueue>(services);
                services.AddSingleton<IEmailQueue>(Emails);
                services.AddSingleton<IStartupFilter, TestPeerIpStartupFilter>();
            });
        }
    }
}
