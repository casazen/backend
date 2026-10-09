using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.Email;

/// <summary>
/// Builds the links of the web app placed in emails and the public URLs of its pages (SEO canonical, sitemap), always
/// from <c>App:PublicSiteBaseUrl</c> (decision D3). When the value is missing or invalid it throws
/// <see cref="EmailConfigurationException"/>: no fallback domain.
/// </summary>
public sealed class PublicSiteLinks(IOptions<PublicSiteOptions> options)
{
    public bool IsConfigured => PublicSiteOptions.TryGetBaseUri(options.Value.PublicSiteBaseUrl, out _);

    /// <summary>Throws <see cref="EmailConfigurationException"/> when links cannot be built.</summary>
    public void EnsureConfigured() => _ = BaseUrl();

    /// <summary>Supplier console inbox.</summary>
    public string SupplierInbox() => Build("/app/supplier/inbox");

    /// <summary>Guest self check-in page for a raw session token.</summary>
    public string GuestCheckIn(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        return Build($"/checkin/{Uri.EscapeDataString(token)}");
    }

    /// <summary>
    /// Supplier registration page of the web app for an invite (SU-01). Only the token is in the link: the page reads
    /// email and comune from the API, so no personal data ends up in URLs and logs.
    /// </summary>
    public string SupplierInviteSignup(string inviteToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inviteToken);
        return Build($"/register?inviteToken={Uri.EscapeDataString(inviteToken)}");
    }

    /// <summary>
    /// Page of the web app where a person accepts an invitation to an org (AM-02, route <c>/invite/accept</c>, built by
    /// AM-04). The link carries the secret token of the invitation and nothing else (no email, no names): the page reads
    /// what it shows from <c>POST /api/org-invitations/lookup</c>, which takes the token in its body, and keeps the token
    /// out of the address bar as soon as it has read it.
    /// </summary>
    public string OrgInvitationAccept(string inviteToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inviteToken);
        return Build($"/invite/accept?token={Uri.EscapeDataString(inviteToken)}");
    }

    /// <summary>The people page of the account (AM-02, route <c>/app/account/people</c>, built by AM-04): who works in the org and its invitations.</summary>
    public string AccountPeople() => Build("/app/account/people");

    /// <summary>
    /// Absolute URL of a public page of the web app (<paramref name="path"/> starts with <c>/</c>), e.g. a sitemap
    /// entry. Throws <see cref="EmailConfigurationException"/> when the public URL is not configured.
    /// </summary>
    public string PublicPage(string path) => Build(EnsureAbsolutePath(path));

    /// <summary>
    /// Like <see cref="PublicPage"/>, but <c>null</c> when the public URL is not configured (only possible in
    /// Development/Testing: elsewhere the startup fails). Used for the canonical URL of a page served anyway.
    /// </summary>
    public string? TryPublicPage(string path)
    {
        var absolutePath = EnsureAbsolutePath(path);
        return PublicSiteOptions.TryGetBaseUri(options.Value.PublicSiteBaseUrl, out var baseUri)
            ? baseUri.AbsoluteUri.TrimEnd('/') + absolutePath
            : null;
    }

    private static string EnsureAbsolutePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return path.StartsWith('/')
            ? path
            : throw new ArgumentException("The path of a public page must start with '/'.", nameof(path));
    }

    /// <summary>
    /// Page of the booking site where the guest confirms the email of a "pay at the property" request (BK-06). Only the
    /// booking id and the random token are in the link, no personal data.
    /// </summary>
    public string OnSiteRequestConfirmation(string orgSlug, Guid bookingId, string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(orgSlug);
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        return Build(
            $"/book/{Uri.EscapeDataString(orgSlug)}/requests/{bookingId:D}/confirm?token={Uri.EscapeDataString(token)}");
    }

    /// <summary>Host console: the "pay at the property" requests to accept or decline (BK-06).</summary>
    public string HostBookingRequests() => Build("/app/short-rent/bookings?view=requests");

    /// <summary>
    /// Payments page of the host console (route <c>/app/short-rent/settings/payments</c> of the web app), where the Stripe
    /// Connect onboarding starts and ends (BK-09, A3-42).
    /// </summary>
    public const string ConnectPaymentsPagePath = "/app/short-rent/settings/payments";

    /// <summary><c>return_url</c> of the Connect Account Link: the payments page with <c>?stripe_return=1</c> (BK-09).</summary>
    public string ConnectOnboardingReturn() => Build(ConnectPaymentsPagePath + "?stripe_return=1");

    /// <summary>
    /// <c>refresh_url</c> of the Connect Account Link (expired or already used link): the payments page with
    /// <c>?stripe_refresh=1</c>, from which the host starts a new link (BK-09).
    /// </summary>
    public string ConnectOnboardingRefresh() => Build(ConnectPaymentsPagePath + "?stripe_refresh=1");

    /// <summary>
    /// Guest self-cancellation page of the booking site (BK-02, BK-07, PO 2026-10-08): the guest confirms the
    /// cancellation there; the page then calls <c>POST /api/public/bookings/{id}/cancel?token=…</c>.
    /// Only the booking id and the signed token are in the link, no personal data.
    /// </summary>
    public string GuestBookingCancel(string orgSlug, Guid bookingId, string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(orgSlug);
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        return Build(
            $"/book/{Uri.EscapeDataString(orgSlug)}/bookings/{bookingId:D}/cancel?token={Uri.EscapeDataString(token)}");
    }

    /// <summary>
    /// Settings page of the supplier console (route <c>/app/supplier/settings</c> of the web app), where the supplier's Stripe
    /// Connect onboarding starts and ends (SP-14). The supplier has no plan or billing page, so it is not one of
    /// <see cref="BillingReturnPagePaths"/> (those are the pages of the Stripe Checkout and billing portal of the plans).
    /// </summary>
    public const string SupplierSettingsPagePath = "/app/supplier/settings";

    /// <summary>
    /// <c>return_url</c> of the supplier's Connect Account Link: the settings page with <c>?stripe_return=1</c> (SP-14), where
    /// the web app reads the account state again. Built by the server, like the host's, never taken from the client.
    /// </summary>
    public string SupplierConnectOnboardingReturn() => Build(SupplierSettingsPagePath + "?stripe_return=1");

    /// <summary>
    /// <c>refresh_url</c> of the supplier's Connect Account Link (expired or already used link): the settings page with
    /// <c>?stripe_refresh=1</c>, from which the supplier starts a new link (SP-14).
    /// </summary>
    public string SupplierConnectOnboardingRefresh() => Build(SupplierSettingsPagePath + "?stripe_refresh=1");

    /// <summary>
    /// "Le mie prenotazioni" of the org's booking site (BK-10, BK-11): the guest finds a booking there with its code and
    /// email. The checkout outcome page (BK-07) needs the checkout token, which only the guest's browser has, so emails
    /// link here. With <paramref name="bookingCode"/> the page opens with the code filled in (<c>?code=</c>) and still asks
    /// for the email before showing anything.
    /// </summary>
    public string GuestBookings(string orgSlug, string? bookingCode = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(orgSlug);
        var path = $"/book/{Uri.EscapeDataString(orgSlug)}/my-bookings";
        return string.IsNullOrWhiteSpace(bookingCode)
            ? Build(path)
            : Build($"{path}?code={Uri.EscapeDataString(bookingCode)}");
    }

    /// <summary>
    /// The public page of a supplier's showcase (route <c>/fornitori/:slug</c>, SU-13, SP-10): where a customer whose request
    /// was refused, cancelled or lapsed goes to book again.
    /// </summary>
    public string SupplierShowcase(string supplierSlug)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(supplierSlug);
        return Build($"/fornitori/{Uri.EscapeDataString(supplierSlug)}");
    }

    /// <summary>
    /// Page of the supplier's showcase where the customer checks the e-mail of a booking (route
    /// <c>/fornitori/:slug/conferma</c>, SP-10): the page reads the booking id and the token of the link and sends them to
    /// <c>POST api/public/suppliers/{slug}/bookings/{id}/confirm-email</c>. Only the id and the random token (single use,
    /// valid for the minutes of the hold) are in the link, no personal data.
    /// </summary>
    public string SupplierBookingConfirmation(string supplierSlug, Guid holdId, string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(supplierSlug);
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        return Build(
            $"/fornitori/{Uri.EscapeDataString(supplierSlug)}/conferma?hold={holdId:D}&token={Uri.EscapeDataString(token)}");
    }

    /// <summary>
    /// "La tua richiesta" on the supplier's showcase (route <c>/fornitori/:slug/richiesta</c>, SP-10, managed with SP-11): the
    /// customer finds a request there with its code and e-mail. With <paramref name="publicCode"/> the page opens with the code
    /// filled in (<c>?code=</c>, as <see cref="GuestBookings"/> does) and still asks for the e-mail before showing anything.
    /// </summary>
    public string SupplierBookingRequest(string supplierSlug, string? publicCode = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(supplierSlug);
        var path = $"/fornitori/{Uri.EscapeDataString(supplierSlug)}/richiesta";
        return string.IsNullOrWhiteSpace(publicCode)
            ? Build(path)
            : Build($"{path}?code={Uri.EscapeDataString(Casazen.Core.Services.BookingCodes.Format(publicCode))}");
    }

    /// <summary>
    /// Checkout outcome page of the booking site (BK-07) with a checkout token: the guest of a failed deferred charge pays
    /// there (BK-08). Only the booking id and the random token are in the link, no personal data.
    /// </summary>
    public string CheckoutOutcome(string orgSlug, Guid bookingId, string checkoutToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(orgSlug);
        ArgumentException.ThrowIfNullOrWhiteSpace(checkoutToken);
        return Build(
            $"/book/{Uri.EscapeDataString(orgSlug)}/booking/{bookingId:D}?token={Uri.EscapeDataString(checkoutToken)}");
    }

    /// <summary>
    /// Public page where a tenant pays a rent installment online (route <c>/rent/pay/:installmentId</c>, LT-06). Only the
    /// installment id and the random token are in the link, no personal data.
    /// </summary>
    public string RentPayment(Guid installmentId, string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        return Build($"/rent/pay/{installmentId:D}?token={Uri.EscapeDataString(token)}");
    }

    /// <summary>
    /// Public page where the payer pays a service a supplier completed (route <c>/service/pay/:paymentId</c>, SP-15a). Only the
    /// payment id and the random token are in the link, no personal data. The page itself is a frontend task (SP-15b).
    /// </summary>
    public string ServicePayment(Guid paymentId, string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        return Build($"/service/pay/{paymentId:D}?token={Uri.EscapeDataString(token)}");
    }

    /// <summary>Host console: detail page of one lease (route <c>/app/long-rent/leases/:id</c>, LT-06).</summary>
    public string HostLease(Guid leaseId) => Build($"/app/long-rent/leases/{leaseId:D}");

    /// <summary>Host console: detail page of one booking (BK-10).</summary>
    public string HostBooking(Guid bookingId) => Build($"/app/short-rent/bookings/{bookingId:D}");

    /// <summary>
    /// Host console: the page of one property in the area of its rental mode (routes <c>/app/short-rent/properties/:id</c> for
    /// <see cref="Core.Entities.Enums.RentalMode.Short"/>, <c>/app/long-rent/properties/:id</c> for
    /// <see cref="Core.Entities.Enums.RentalMode.Long"/>, PM-02).
    /// </summary>
    public string HostProperty(Guid propertyId, Core.Entities.Enums.RentalMode mode) =>
        Build($"/app/{(mode == Core.Entities.Enums.RentalMode.Long ? "long-rent" : "short-rent")}/properties/{propertyId:D}");

    /// <summary>Host console: activation wizard of one property (route <c>/app/short-rent/properties/:id/activation</c>, CO-06).</summary>
    public string HostPropertyActivation(Guid propertyId) => Build($"/app/short-rent/properties/{propertyId:D}/activation");

    /// <summary>Host console: CIN compliance page (route <c>/app/short-rent/compliance/cin</c>, CO-20).</summary>
    public string HostCinCompliance() => Build("/app/short-rent/compliance/cin");

    /// <summary>
    /// Plan page of the host console (route <c>/app/short-rent/settings/plan</c> of the web app): the default page Stripe
    /// Checkout and the billing portal return to (PL-11, A1-31) when the client names no page.
    /// </summary>
    public const string BillingPagePath = "/app/short-rent/settings/plan";

    /// <summary>
    /// Allow-list of the pages Stripe may send the browser back to (PL-16, A1-36): the plan and billing pages of the web
    /// app in each rental context, so a long-term landlord comes back to its own shell. Exact paths, no query string.
    /// </summary>
    public static readonly IReadOnlySet<string> BillingReturnPagePaths = new HashSet<string>(StringComparer.Ordinal)
    {
        BillingPagePath,
        "/app/short-rent/settings/billing",
        "/app/long-rent/settings/plan",
        "/app/long-rent/settings/billing",
    };

    /// <summary>True when <paramref name="path"/> is exactly one of <see cref="BillingReturnPagePaths"/>.</summary>
    public static bool IsBillingReturnPagePath(string? path) =>
        path is not null && BillingReturnPagePaths.Contains(path);

    /// <summary>
    /// Return page of a paid Stripe Checkout: <paramref name="returnPath"/> (one of <see cref="BillingReturnPagePaths"/>)
    /// or the default plan page, with <c>?checkout=success</c>.
    /// </summary>
    public string BillingCheckoutSuccess(string? returnPath = null) => Build(BillingPage(returnPath) + "?checkout=success");

    /// <summary>Return page of an abandoned Stripe Checkout, like <see cref="BillingCheckoutSuccess"/> with <c>?checkout=cancel</c>.</summary>
    public string BillingCheckoutCancel(string? returnPath = null) => Build(BillingPage(returnPath) + "?checkout=cancel");

    /// <summary>Return page of the Stripe billing portal: <paramref name="returnPath"/> or the default plan page.</summary>
    public string BillingPortalReturn(string? returnPath = null) => Build(BillingPage(returnPath));

    /// <summary>
    /// True when <paramref name="url"/> is a page Stripe may return to: an absolute URL of the public web app
    /// (<see cref="IsOnPublicSite"/>) whose path is one of <see cref="BillingReturnPagePaths"/>; any query string is kept
    /// (e.g. <c>?checkout=success&amp;session_id={CHECKOUT_SESSION_ID}</c>).
    /// </summary>
    public bool IsBillingReturnUrl(string? url) =>
        IsOnPublicSite(url)
        && Uri.TryCreate(url!.Trim(), UriKind.Absolute, out var candidate)
        && IsBillingReturnPagePath(candidate.AbsolutePath);

    private static string BillingPage(string? returnPath)
    {
        if (string.IsNullOrWhiteSpace(returnPath))
            return BillingPagePath;

        return IsBillingReturnPagePath(returnPath)
            ? returnPath
            : throw new ArgumentException("The billing return page is not in the allow-list.", nameof(returnPath));
    }

    /// <summary>
    /// True when <paramref name="url"/> is an absolute URL of the public web app: same scheme, host and port as
    /// <c>App:PublicSiteBaseUrl</c>, without user info. The allow-list of the return URLs a client may send to Stripe
    /// (PL-11, A1-31): after the payment the browser never lands on another site. False when the public URL is not
    /// configured.
    /// </summary>
    public bool IsOnPublicSite(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)
            || !PublicSiteOptions.TryGetBaseUri(options.Value.PublicSiteBaseUrl, out var baseUri)
            || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var candidate))
            return false;

        return string.Equals(candidate.Scheme, baseUri.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(candidate.IdnHost, baseUri.IdnHost, StringComparison.OrdinalIgnoreCase)
            && candidate.Port == baseUri.Port
            && string.IsNullOrEmpty(candidate.UserInfo);
    }

    /// <summary>
    /// True when <paramref name="host"/> (a host name without scheme, port optional) is the host of
    /// <c>App:PublicSiteBaseUrl</c>: the web app's own domain, as opposed to an org subdomain or custom domain (BK-15).
    /// False when the public URL is not configured.
    /// </summary>
    public bool IsPublicSiteHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)
            || !PublicSiteOptions.TryGetBaseUri(options.Value.PublicSiteBaseUrl, out var baseUri))
            return false;

        return string.Equals(PublicSiteHosts.Normalize(host), PublicSiteHosts.Normalize(baseUri.IdnHost), StringComparison.Ordinal);
    }

    private string Build(string pathAndQuery) => BaseUrl() + pathAndQuery;

    private string BaseUrl()
    {
        if (!PublicSiteOptions.TryGetBaseUri(options.Value.PublicSiteBaseUrl, out var baseUri))
        {
            throw new EmailConfigurationException(
                "App:PublicSiteBaseUrl is missing or invalid: public links cannot be built.");
        }

        return baseUri.AbsoluteUri.TrimEnd('/');
    }
}
