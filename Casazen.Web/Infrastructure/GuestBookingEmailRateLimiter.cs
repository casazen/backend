using Casazen.Web.Extensions;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Infrastructure;

/// <summary>
/// Second limit of "Le mie prenotazioni" (BK-11, FD-10): a fixed window per email address, on top of the per-IP policy
/// <see cref="RateLimitPolicies.PublicGuestBookingLookup"/>, so that trying codes for one guest's email from many IPs is
/// bounded too. Every attempt counts, found or not, so the limit never tells whether a booking exists. Since SP-11 the mechanism
/// is the general <see cref="PerEmailRateLimiter"/>; this is its instance for the guests.
/// </summary>
/// <remarks>
/// Configuration (runbook <c>docs/runbooks/direct-booking.md</c>): <c>RateLimiting:GuestBookingLookupPerEmail:PermitLimit</c>
/// and <c>WindowSeconds</c> (default 5 per 15 minutes). The partition key is a hash of the normalized email, never the
/// address. Counters live in memory, per replica.
/// </remarks>
public sealed class GuestBookingEmailRateLimiter(IConfiguration configuration) : PerEmailRateLimiter(Policy, configuration)
{
    public static RateLimitPolicyDefinition Policy { get; } = new("GuestBookingLookupPerEmail", 5, TimeSpan.FromMinutes(15));
}

/// <summary>
/// Puts an action of "Le mie prenotazioni" under <see cref="GuestBookingEmailRateLimiter"/>, for the email of its
/// <see cref="DTOs.GuestBookingLookupRequest"/>: over the limit it answers 429 <c>rate_limited</c> with <c>Retry-After</c> (the
/// contract of the per-IP policies) and the booking is not read. Runs after the model validation.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class GuestBookingEmailRateLimitAttribute() : TypeFilterAttribute(typeof(PerEmailRateLimitFilter<GuestBookingEmailRateLimiter>));
