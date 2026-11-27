using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Casazen.Web.Controllers;

public sealed class GuestPortalPaymentsController(IBookingPaymentLinkService payments, IConfiguration configuration)
    : GuestPortalControllerBase
{
    [HttpGet("payments")]
    public async Task<ActionResult<GuestStayBalance>> Get(CancellationToken ct)
    {
        var balance = await payments.ReadGuestBalanceAsync(Portal.OrgId, Portal.BookingId, ct);
        if (balance is null)
            return LinkInvalid();

        return balance with { CanPay = balance.CanPay && PublishableKey is not null };
    }

    [HttpPost("payments/payment-session")]
    [EnableRateLimiting(RateLimitPolicies.GuestPortalWrite)]
    public async Task<ActionResult<BalanceSession>> Prepare(CancellationToken ct)
    {
        var key = PublishableKey;
        if (key is null)
            throw new DomainRuleException(BookingPaymentLinkErrorCodes.Unavailable, BookingPaymentLinkErrorCodes.UnavailableMessageKey);

        try
        {
            var intent = await payments.PrepareGuestBalanceAsync(Portal.OrgId, Portal.BookingId, ct);
            return new BalanceSession(intent.ClientSecret, key, intent.StripeAccountId, intent.Amount);
        }
        catch (NotFoundException)
        {
            return LinkInvalid();
        }
    }

    private string? PublishableKey => configuration["Stripe:PublishableKey"] is { Length: > 0 } key ? key : null;

    public sealed record BalanceSession(string ClientSecret, string PublishableKey, string StripeAccountId, decimal Amount);
}
