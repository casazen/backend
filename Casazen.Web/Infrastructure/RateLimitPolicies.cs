namespace Casazen.Web.Infrastructure;

/// <summary>
/// Names of the rate limiting policies of the anonymous endpoints (<c>[EnableRateLimiting(...)]</c>). Every policy is
/// partitioned by client IP (<see cref="ClientIp.GetRateLimitKey"/>); the guest check-in policies also by token, and
/// <see cref="GlobalSearch"/>, the one of a signed-in endpoint, by user.
/// Limits and windows come from configuration: see
/// <see cref="Extensions.RateLimitingServiceCollectionExtensions"/> and <c>docs/runbooks/proxy-ip.md</c>.
/// </summary>
public static class RateLimitPolicies
{
    /// <summary>Anonymous catalogue reads: public org, its properties, property search/detail, availability, supplier showcase.</summary>
    public const string PublicRead = "PublicRead";

    /// <summary>Direct booking creation (<c>POST api/public/bookings</c>).</summary>
    public const string PublicBookingCreate = "PublicBookingCreate";

    /// <summary>Checkout outcome polling, payment resume and email confirmation of the public checkout, separate from creation.</summary>
    public const string PublicBookingLookup = "PublicBookingLookup";

    /// <summary>
    /// "Le mie prenotazioni": booking code + email lookup and check-in link resend (BK-11). Tighter than
    /// <see cref="PublicBookingLookup"/> and paired with a per-email limit (<see cref="GuestBookingEmailRateLimiter"/>).
    /// </summary>
    public const string PublicGuestBookingLookup = "PublicGuestBookingLookup";

    /// <summary>Guest check-in form reads (partitioned by IP and token hash).</summary>
    public const string GuestCheckIn = "GuestCheckIn";

    /// <summary>Guest check-in submission (partitioned by IP and token hash).</summary>
    public const string GuestCheckInSubmit = "GuestCheckInSubmit";

    /// <summary>Public tourist tax calculator.</summary>
    public const string PublicTouristTaxCalc = "PublicTouristTaxCalc";

    /// <summary>Events of the SEO funnel (<c>POST api/public/seo/events</c>, SE-04): one per CTA click or signup start.</summary>
    public const string PublicSeoEvents = "PublicSeoEvents";

    /// <summary>Host → tenant resolution for custom domains and subdomains.</summary>
    public const string PublicResolveHost = "PublicResolveHost";

    /// <summary>Public iCal export feeds polled by the OTAs.</summary>
    public const string PublicIcal = "PublicIcal";

    /// <summary>Anonymous sign-ups (<c>api/suppliers/register</c>).</summary>
    public const string PublicRegistration = "PublicRegistration";

    /// <summary>Search of the official comuni list for the pickers (<c>api/comuni</c>, SU-04): one request per pause in typing.</summary>
    public const string PublicComuni = "PublicComuni";

    /// <summary>
    /// Free slots of a supplier's service (<c>GET api/public/suppliers/{slug}/slots</c>, SP-09): each read runs the slot planner
    /// (cached 30 seconds per service), so it has a limit of its own, tighter than <see cref="PublicRead"/>.
    /// </summary>
    public const string PublicSupplierSlots = "PublicSupplierSlots";

    /// <summary>
    /// Price estimate of a supplier's service (<c>POST api/public/suppliers/{slug}/quote</c>, SP-09): the form asks again at
    /// every change of an option, and every call may look up the comune, so it has a limit of its own.
    /// </summary>
    public const string PublicSupplierQuote = "PublicSupplierQuote";

    /// <summary>
    /// Booking of a supplier from its public showcase (<c>POST api/public/suppliers/{slug}/bookings</c>, SP-10): each call holds a
    /// slot of the supplier's agenda for the minutes of the e-mail check, so it is the tightest of the public limits (5 per 10
    /// minutes per client IP), and it is paired with a limit per e-mail address and supplier (<see cref="SupplierBookingEmailRateLimiter"/>,
    /// 3 per hour). The check of the e-mail itself uses <see cref="PublicBookingLookup"/>.
    /// </summary>
    public const string PublicSupplierBookingCreate = "PublicSupplierBookingCreate";

    /// <summary>
    /// What an org invitation link is for (<c>POST api/org-invitations/lookup</c>, AM-02): anonymous, a token in the body.
    /// Tighter than <see cref="PublicRead"/>: a page asks once, and the token is 256 random bits, so nobody has a reason
    /// to ask often.
    /// </summary>
    public const string PublicInvitationLookup = "PublicInvitationLookup";

    /// <summary>
    /// The global search of the palette (<c>GET api/search</c>, UI-13a). The only policy of an authenticated endpoint: it is
    /// partitioned by <b>user</b> (a hash of the subject), not by IP, because the palette asks at every pause in typing and the
    /// people of one office share an address. Each call runs up to seven short queries, so it has a limit of its own.
    /// </summary>
    public const string GlobalSearch = "GlobalSearch";
}
