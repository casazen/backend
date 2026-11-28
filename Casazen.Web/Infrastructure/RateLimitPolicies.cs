namespace Casazen.Web.Infrastructure;

public static class RateLimitPolicies
{
    public const string StorefrontChatRead = "storefront-chat-read";
    public const string StorefrontChatWrite = "storefront-chat-write";
    public const string PublicRead = "PublicRead";

    public const string PublicBookingCreate = "PublicBookingCreate";

    public const string PublicBookingLookup = "PublicBookingLookup";

    public const string PublicGuestBookingLookup = "PublicGuestBookingLookup";

    public const string PublicGuestSession = "PublicGuestSession";

    public const string GuestBookingCancel = "GuestBookingCancel";

    public const string GuestCheckIn = "GuestCheckIn";

    public const string GuestCheckInSubmit = "GuestCheckInSubmit";

    /// <summary>
    /// The opening of the self-hosted identity step and its presence challenge (<c>POST api/public/checkin/{token}/identity/open</c> and
    /// <c>…/presence-challenge</c>, E70-E74, NF-188, NF-192): 30 a minute for each client IP and token, partitioned like <see cref="GuestCheckIn"/> (the partition
    /// holds a hash of the <c>{token}</c>, never the token). A budget of its own, so a reload, the guided retry and a network resume of the identity step never
    /// spend the 10 a minute of the reads of the form.
    /// </summary>
    public const string GuestIdentity = "GuestIdentity";

    public const string GuestIdentityUpload = "GuestIdentityUpload";

    public const string PublicDoorCheckIn = "PublicDoorCheckIn";

    public const string PublicTouristTaxCalc = "PublicTouristTaxCalc";

    public const string PublicSeoEvents = "PublicSeoEvents";

    public const string PublicResolveHost = "PublicResolveHost";

    public const string PublicIcal = "PublicIcal";

    public const string PublicRegistration = "PublicRegistration";

    public const string PublicComuni = "PublicComuni";

    public const string PublicStaySearch = "PublicStaySearch";

    public const string PublicSupplierSlots = "PublicSupplierSlots";

    public const string PublicSupplierQuote = "PublicSupplierQuote";

    public const string PublicSupplierBookingCreate = "PublicSupplierBookingCreate";

    public const string PublicReviewSubmit = "PublicReviewSubmit";

    public const string GuestMessageSend = "GuestMessageSend";

    /// <summary>
    /// The host answering a guest (<c>POST api/bookings/{id}/messages</c>, DB-14a): partitioned by <b>person</b> (the signed-in account),
    /// since the answer ends in an email to the guest.
    /// </summary>
    public const string HostMessageSend = "HostMessageSend";

    public const string PublicContact = "PublicContact";

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
    /// The import of a bank statement on the rents (<c>POST api/long-rent/rents/bank-import/preview</c> and <c>…/confirm</c>): partitioned by
    /// <b>person</b>, since each call reads a file of up to 8 MB and the confirmation declares payments. A landlord imports a statement a
    /// month and tries a few files in a sitting: thirty calls in ten minutes leave room for that and no more than a script could waste.
    /// </summary>
    public const string RentBankImport = "RentBankImport";

    /// <summary>
    /// The import of the data that changes per property from a CSV or an Excel file (<c>POST api/onboarding/bulk/import</c>, E10/F02/US01/T02):
    /// partitioned by <b>person</b>, since each call unzips and parses a file of up to 2 MB (20 MB once unzipped) and, without <c>dryRun</c>, writes
    /// up to a thousand rows. An agency checks a file, corrects it and imports it: thirty calls in ten minutes leave room for that and no more than a
    /// script could waste.
    /// </summary>
    public const string OnboardingBulkImport = "OnboardingBulkImport";

    /// <summary>
    /// The autosave of the showcase editor (<c>PATCH api/supplier/showcase/content</c>, SP-13a). A policy of a signed-in endpoint, like
    /// <see cref="GlobalSearch"/>: it is partitioned by <b>person</b>, not by IP, because the editor saves at every pause in typing and the
    /// people of one office share an address. The body is small and bounded; the limit keeps a script from rewriting a draft in a loop.
    /// </summary>
    public const string SupplierShowcaseSave = "SupplierShowcaseSave";

    public const string SupplierShowcaseMedia = "SupplierShowcaseMedia";

    public const string GuestAccountRead = "GuestAccountRead";

    public const string GuestAccountSession = "GuestAccountSession";

    /// <summary>
    /// The deletion of the guest's own account (<c>DELETE api/guest/account</c>): irreversible, so tight and partitioned by <b>person</b>; a
    /// few tries leave room for the retry after an answer 502 of Auth0.
    /// </summary>
    public const string GuestAccountDelete = "GuestAccountDelete";

    /// <summary>
    /// The anonymous calls that give, confirm or take back a marketing consent (E07/F04/US01): the page of the double opt-in link
    /// (<c>POST api/public/marketing/confirm</c>), the welcome page of a property (<c>GET api/public/welcome/{token}</c> and <c>POST …/consent</c>), and the
    /// choices on the guest page (<c>PUT api/public/guest/bookings/{code}/marketing</c> and <c>POST …/marketing/resend-confirmation</c>). Each
    /// can queue an e-mail to an address somebody typed, so they are bound per client IP: 60 in ten minutes, enough for the guests of a
    /// building that share one network scanning the code in the same hour, and no more than a script could waste. Reading the choices on the
    /// guest page uses <see cref="PublicBookingLookup"/>.
    /// </summary>
    public const string PublicMarketingConsent = "PublicMarketingConsent";

    public const string AssistantSimulate = "AssistantSimulate";

    public const string OwnerPortalRead = "OwnerPortalRead";

    public const string OwnerPortalInvite = "OwnerPortalInvite";

    public const string OwnerPortalWrite = "OwnerPortalWrite";

    public const string OwnerPortalDownload = "OwnerPortalDownload";

    /// <summary>
    /// The public page of the document of a stay that the guest got by e-mail (<c>POST api/public/fiscal-documents/{id}</c>, the summary, and <c>…/pdf</c>, the file, E12/F01/US02/T01): the
    /// token is in the body, never in the address, and every link that does not open answers the same 404. Per client IP, like the other anonymous policies; a page asks once and a guest
    /// downloads the file a few times, and the PDF is drawn only for a token that opens, so 20 a minute leave room for that and no more than a script could waste.
    /// </summary>
    public const string PublicFiscalDocumentDownload = "PublicFiscalDocumentDownload";

    public const string PublicAccountantPackage = "PublicAccountantPackage";

    /// <summary>
    /// «Send a test» of the package to the accountant (<c>POST api/accountant-package/send</c>, E15/F01/US02/T02): each call makes a package, keeps it in the private storage and sends an e-mail to a third party, so
    /// it is tight and partitioned by <b>person</b>: five in ten minutes, enough for a test and its retry.
    /// </summary>
    public const string AccountantPackageSend = "AccountantPackageSend";

    /// <summary>
    /// The guest portal (<c>api/public/guest-portal/{token}/…</c>, E50/F01/US01/T02, NF-152): the default of every endpoint of the portal
    /// (<c>GuestPortalControllerBase</c>), the overview and the reads of the sections. Partitioned by client IP <b>and</b> a hash of the <c>{token}</c>
    /// of the route, like <see cref="GuestCheckIn"/> (the partition never holds the token): 120 a minute, room for a page that reads several sections. A client
    /// that changes the token at every request does not meet it twice, so a limit per client IP over every token (<see cref="GuestPortalIpRateLimiter"/>, 600 a
    /// minute) is chained after it.
    /// </summary>
    public const string GuestPortal = "GuestPortal";

    public const string GuestPortalWrite = "GuestPortalWrite";

    /// <summary>
    /// The agency's «retry» of the owner sign-in on its own domain (<c>POST api/orgs/{orgId}/domain/owner-login</c>, E40/F01/US02/T01, NF-141): with <c>Auth0HostSync</c> on each call
    /// that finds the state not ready reads, and may write, the SPA application of the <b>shared</b> Auth0 tenant (the same Management API quota as the roles of every host) while it
    /// holds a database connection under the advisory lock. Partitioned by <b>person</b>: five in ten minutes, enough for a retry after CasaZen fixed a scope and a second try.
    /// </summary>
    public const string OwnerLoginRetry = "OwnerLoginRetry";

    /// <summary>
    /// A billing administrator confirms the connection of a site (<c>POST api/orgs/{orgId}/site-connections/authorize</c>, S022, site integration plan §10.4):
    /// each call writes a one-time code. Partitioned by <b>person</b>: ten in ten minutes, enough for a connection and its retries.
    /// </summary>
    public const string SiteConnectAuthorize = "SiteConnectAuthorize";

    public const string SiteConnectToken = "SiteConnectToken";

    /// <summary>
    /// The calls of a connected plugin (<c>api/site/v1/*</c>, S022 and S023): partitioned by <b>credential</b> (the connection of the scheme <c>SiteToken</c>),
    /// so the sites behind one hosting address never share a budget: 120 a minute. A call without a valid credential is refused before the limiter.
    /// </summary>
    public const string SiteApi = "SiteApi";

    public const string SiteDomainVerify = "SiteDomainVerify";
}
