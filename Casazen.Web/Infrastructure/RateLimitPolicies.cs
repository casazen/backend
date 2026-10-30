namespace Casazen.Web.Infrastructure;

/// <summary>
/// Names of the rate limiting policies of the anonymous endpoints (<c>[EnableRateLimiting(...)]</c>). Every policy is
/// partitioned by client IP (<see cref="ClientIp.GetRateLimitKey"/>); the guest check-in policies also by token, and
/// <see cref="OrgAccessRequest"/> and <see cref="GlobalSearch"/>, the ones of signed-in endpoints, by person.
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

    /// <summary>
    /// Opening a guest session (<c>POST api/public/guest/session</c>, DB-06a): the same proof as <see cref="PublicGuestBookingLookup"/>
    /// (booking code + email), so the same default (10 per 5 minutes per client IP), with a budget of its own. Paired with the per-email
    /// limit of "Le mie prenotazioni" (<see cref="GuestBookingEmailRateLimiter"/>, shared with the lookup) and a per-code limit
    /// (<see cref="GuestSessionCodeRateLimiter"/>). Reading the booking with the token uses <see cref="PublicBookingLookup"/>.
    /// </summary>
    public const string PublicGuestSession = "PublicGuestSession";

    /// <summary>
    /// The guest cancelling a booking (<c>POST api/public/guest/bookings/{code}/cancel</c> from the guest area and
    /// <c>POST api/public/bookings/{id}/cancel?token=</c> with the host's link, DB-08a): it moves money and frees the dates, so it is
    /// tight (10 per 10 minutes per client IP) and paired with a limit per booking (<see cref="GuestBookingCancelRateLimiter"/>, 5 per
    /// 15 minutes). The cancel link used <see cref="GuestCheckIn"/> before, which partitions by a <c>{token}</c> route value that the
    /// cancel route does not have (its token is in the query), so it counted by IP alone. The previews use <see cref="PublicBookingLookup"/>.
    /// </summary>
    public const string GuestBookingCancel = "GuestBookingCancel";

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
    /// The search of a booking site and its calendar (<c>GET api/public/orgs/{slug}/stays</c> and <c>…/calendar</c>, DB-07a): the heaviest
    /// anonymous reads, each prices every house of the site for the dates (the answer is kept a few seconds), so they have a limit of their own,
    /// tighter than <see cref="PublicRead"/>.
    /// </summary>
    public const string PublicStaySearch = "PublicStaySearch";

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
    /// The guest's review of a stay (<c>POST api/public/bookings/{id}/review</c>, DB-12a): the only anonymous call that publishes text
    /// on a public page, so it is tight (10 per 10 minutes per client IP). Opening the review link uses <see cref="PublicBookingLookup"/>.
    /// </summary>
    public const string PublicReviewSubmit = "PublicReviewSubmit";

    /// <summary>
    /// The guest writing to the host (<c>POST api/public/guest/bookings/{code}/messages</c>, DB-14a): free text that ends in an email to
    /// the host, so it is bounded per client IP (30 per 10 minutes) and, with <see cref="GuestMessageSessionRateLimiter"/>, per guest
    /// session and booking (20 per hour). Reading the thread uses <see cref="PublicBookingLookup"/>.
    /// </summary>
    public const string GuestMessageSend = "GuestMessageSend";

    /// <summary>
    /// The host answering a guest (<c>POST api/bookings/{id}/messages</c>, DB-14a): partitioned by <b>person</b> (the signed-in account),
    /// since the answer ends in an email to the guest.
    /// </summary>
    public const string HostMessageSend = "HostMessageSend";

    /// <summary>
    /// A visitor of the booking site writing to the host (<c>POST api/public/orgs/{slug}/contact</c>, DB-03b): anonymous free text that ends
    /// in an email to somebody else, so it is the tightest of the public limits together with the bookings (5 per 10 minutes per client IP),
    /// paired with a limit per address and org (<see cref="HostContactEmailRateLimiter"/>, 3 per day).
    /// </summary>
    public const string PublicContact = "PublicContact";

    /// <summary>
    /// What an org invitation link is for (<c>POST api/org-invitations/lookup</c>, AM-02): anonymous, a token in the body.
    /// Tighter than <see cref="PublicRead"/>: a page asks once, and the token is 256 random bits, so nobody has a reason
    /// to ask often.
    /// </summary>
    public const string PublicInvitationLookup = "PublicInvitationLookup";

    /// <summary>
    /// A member's request for access to the administrators of its org (<c>POST api/orgs/me/access-requests</c>, AM-02b). The
    /// only policy partitioned by <b>person</b> (the signed-in account) instead of by client IP: the request sends an email to
    /// other people, so the bound that matters is how many one person can send, not how many one office network can.
    /// </summary>
    public const string OrgAccessRequest = "OrgAccessRequest";

    /// <summary>
    /// The global search of the palette (<c>GET api/search</c>, UI-13a). A policy of a signed-in endpoint, like
    /// <see cref="OrgAccessRequest"/>: it is partitioned by <b>person</b> (a hash of the subject), not by IP, because the palette
    /// asks at every pause in typing and the people of one office share an address. Each call runs up to seven short queries, so
    /// it has a limit of its own.
    /// </summary>
    public const string GlobalSearch = "GlobalSearch";

    /// <summary>
    /// The free slots of a supplier's service read by the host to ask for an intervention (<c>GET api/suppliers/{id}/services/{serviceId}/slots</c>,
    /// SR-08a). Each read runs the slot planner over up to 14 days, so it has a limit of its own. A policy of a signed-in endpoint, like
    /// <see cref="GlobalSearch"/>: partitioned by <b>person</b>, since the people of one office share an address.
    /// </summary>
    public const string HostSupplierSlots = "HostSupplierSlots";

    /// <summary>
    /// The copy of a tenant's personal data and the erasure request (<c>GET api/long-rent/tenants/{id}/export</c> and <c>POST
    /// …/erasure-request</c>, LR-09): partitioned by <b>person</b>, since the export puts every personal datum of somebody in one file and
    /// the erasure cannot be taken back. Nobody asks for more than a few in ten minutes.
    /// </summary>
    public const string TenantPrivacy = "TenantPrivacy";

    /// <summary>
    /// The autosave of the showcase editor (<c>PATCH api/supplier/showcase/content</c>, SP-13a). A policy of a signed-in endpoint, like
    /// <see cref="GlobalSearch"/>: it is partitioned by <b>person</b>, not by IP, because the editor saves at every pause in typing and the
    /// people of one office share an address. The body is small and bounded; the limit keeps a script from rewriting a draft in a loop.
    /// </summary>
    public const string SupplierShowcaseSave = "SupplierShowcaseSave";
}
