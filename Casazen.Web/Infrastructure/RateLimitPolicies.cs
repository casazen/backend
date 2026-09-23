namespace Casazen.Web.Infrastructure;

/// <summary>
/// Names of the rate limiting policies of the anonymous endpoints (<c>[EnableRateLimiting(...)]</c>). Every policy is
/// partitioned by client IP (<see cref="ClientIp.GetRateLimitKey"/>); the guest check-in policies also by token.
/// Limits and windows come from configuration: see
/// <see cref="Extensions.RateLimitingServiceCollectionExtensions"/> and <c>docs/runbooks/proxy-ip.md</c>.
/// </summary>
public static class RateLimitPolicies
{
    /// <summary>Anonymous catalogue reads: public org, its properties, property search/detail, availability, supplier showcase.</summary>
    public const string PublicRead = "PublicRead";

    /// <summary>Direct booking creation (<c>POST api/public/bookings</c>).</summary>
    public const string PublicBookingCreate = "PublicBookingCreate";

    /// <summary>Guest booking lookup and status polling, separate from creation.</summary>
    public const string PublicBookingLookup = "PublicBookingLookup";

    /// <summary>Guest check-in form reads (partitioned by IP and token hash).</summary>
    public const string GuestCheckIn = "GuestCheckIn";

    /// <summary>Guest check-in submission (partitioned by IP and token hash).</summary>
    public const string GuestCheckInSubmit = "GuestCheckInSubmit";

    /// <summary>Public tourist tax calculator.</summary>
    public const string PublicTouristTaxCalc = "PublicTouristTaxCalc";

    /// <summary>Host → tenant resolution for custom domains and subdomains.</summary>
    public const string PublicResolveHost = "PublicResolveHost";

    /// <summary>Public iCal export feeds polled by the OTAs.</summary>
    public const string PublicIcal = "PublicIcal";

    /// <summary>Anonymous sign-ups (<c>api/suppliers/register</c>, <c>api/auth/register</c>).</summary>
    public const string PublicRegistration = "PublicRegistration";

    /// <summary>Supplier QR check-in/check-out (<c>api/public/check-in</c>).</summary>
    public const string PublicSupplierCheckIn = "PublicSupplierCheckIn";
}
