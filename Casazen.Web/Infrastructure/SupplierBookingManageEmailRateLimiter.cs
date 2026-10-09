using Casazen.Core.Suppliers;
using Casazen.Web.Extensions;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Infrastructure;

/// <summary>
/// Second limit of the customer's own area of a booking made from a supplier's showcase (SP-11): a fixed window per e-mail address
/// <b>and supplier</b> over its five endpoints (find, cancel, move, accept and turn down a proposed time), on top of the per-IP policy
/// <see cref="RateLimitPolicies.PublicGuestBookingLookup"/>, so that trying codes for one address from many IPs is bounded too.
/// Every attempt counts, found or not, and the 429 always names the whole window (never the time that is left of it), so the limit
/// tells nothing about whether the address has a booking, or when somebody last looked for one.
/// </summary>
/// <remarks>
/// Configuration (runbook <c>docs/runbooks/proxy-ip.md</c>): <c>RateLimiting:SupplierBookingManagePerEmail:PermitLimit</c> and
/// <c>WindowSeconds</c> (default 10 per 15 minutes: finding the booking, a time that is not free, then the right one). The partition key
/// is a hash of the supplier's slug and the normalized address, never the address. Counters live in memory, per replica.
/// </remarks>
public sealed class SupplierBookingManageEmailRateLimiter(IConfiguration configuration) : PerEmailRateLimiter(Policy, configuration)
{
    public static RateLimitPolicyDefinition Policy { get; } = new("SupplierBookingManagePerEmail", 10, TimeSpan.FromMinutes(15));

    public override bool NamesTheWholeWindow => true;

    // The budget is counted under the very forms the identification of a booking looks the address and the slug up in (Core), so that
    // every spelling that finds a booking shares one budget, whatever those forms become.
    protected override string NormalizeEmail(string email) => ShowcaseBookingRules.NormalizeEmail(email);

    protected override string NormalizeScope(string scope) => SupplierShowcaseSlug.Normalize(scope);
}

/// <summary>
/// Puts an action of the customer's own area under <see cref="SupplierBookingManageEmailRateLimiter"/>, for the address and the
/// supplier of its body: over the limit it answers 429 <c>rate_limited</c> with <c>Retry-After</c> and nothing is read or changed.
/// Runs after the model validation, next to <c>[EnableRateLimiting(PublicGuestBookingLookup)]</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class SupplierBookingManageRateLimitAttribute()
    : TypeFilterAttribute(typeof(PerEmailRateLimitFilter<SupplierBookingManageEmailRateLimiter>));
