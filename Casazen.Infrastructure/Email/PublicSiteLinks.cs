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

    public void EnsureConfigured() => _ = BaseUrl();

    public string SupplierInbox() => Build("/app/supplier/inbox");

    public string SupplierRequest(Guid serviceRequestId) => Build($"/app/supplier/inbox/{serviceRequestId:D}");

    public string GuestCheckIn(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        return Build($"/checkin/{Uri.EscapeDataString(token)}");
    }

    public string DoorCheckIn(string doorToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(doorToken);
        return Build($"/checkin/porta/{Uri.EscapeDataString(doorToken)}");
    }

    /// <summary>
    /// Page of the web app where a guest stops the marketing e-mails of an org (route <c>/unsubscribe</c>, DEB-01; the page is a frontend
    /// task). <paramref name="signedQuery"/> (<c>orgId</c>, <c>emailHash</c>, <c>expiresAt</c>, <c>signature</c>, built by
    /// <see cref="MarketingUnsubscribeLinks"/>) travels in the <b>fragment</b>, which a browser never sends to a server, so it is in no
    /// access log: the page reads it and posts it unchanged as the query of <c>POST api/public/marketing/unsubscribe</c>. Never the address.
    /// </summary>
    public string MarketingUnsubscribe(string signedQuery)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(signedQuery);
        return Build($"/unsubscribe#{signedQuery}");
    }

    /// <summary>
    /// Page of the web app where a person confirms the subscription to the marketing e-mails of an org, the second step of the double opt-in
    /// (route <c>/marketing/confirm</c>, E07/F04/US01/T01; the page is a frontend task). <paramref name="signedQuery"/> (<c>contactId</c>,
    /// <c>expiresAt</c>, <c>signature</c>, built by <see cref="MarketingConfirmationLinks"/>) travels in the <b>fragment</b>, which a browser
    /// never sends to a server, so it is in no access log: the page reads it and posts it unchanged as the query of
    /// <c>POST api/public/marketing/confirm</c>. Never the address, never the org.
    /// </summary>
    public string MarketingConfirm(string signedQuery)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(signedQuery);
        return Build($"/marketing/confirm#{signedQuery}");
    }

    public string Welcome(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        return Build($"/benvenuto/{Uri.EscapeDataString(token)}");
    }

    public string GuestPortal(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        return Build(PublicSitePaths.HostGuestPortal(token));
    }

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

    /// <summary>
    /// Public page of the web app for a host invite (route <c>/invito-host/{token}</c>): it reads what it shows from
    /// <c>GET api/host-invites/lookup</c> and sends the person to the normal signup. Only the secret token is in the link: no
    /// email, no name.
    /// </summary>
    public string HostInviteSignup(string inviteToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inviteToken);
        return Build($"/invito-host/{Uri.EscapeDataString(inviteToken)}");
    }

    /// <summary>
    /// Page of the web app where a property owner accepts the invitation of its agency to the owner portal (route
    /// <c>/portale-proprietari/invito</c>, E10/F02/US02/T02, ADR-011-R6): the page keeps the token through the Auth0 sign-in and sends it once, in the body
    /// of <c>POST api/owner-invitations/accept</c>. Only the secret token is in the link: no name, no address. The address is the one public domain of
    /// the product (<c>App:PublicSiteBaseUrl</c>, decision D3), checked at startup to be an absolute https URL.
    /// </summary>
    public string OwnerInvitationAccept(string inviteToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inviteToken);
        return Build($"/portale-proprietari/invito?token={Uri.EscapeDataString(inviteToken)}");
    }

    /// <summary>
    /// Page of the web app where a contact an owner invited accepts the invitation (route <c>/portale-proprietari/contatto</c>, E07/F07/US01/T02): as the
    /// owner invitation, the page keeps the token through the Auth0 sign-in and sends it once, in the body of <c>POST api/owner-contacts/accept</c>. Only the
    /// secret token is in the link.
    /// </summary>
    public string OwnerContactAccept(string inviteToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inviteToken);
        return Build($"/portale-proprietari/contatto?token={Uri.EscapeDataString(inviteToken)}");
    }

    public string OwnerPortalConversation(Guid conversationId) => Build($"/portale-proprietari/messaggi/{conversationId:D}");

    public string OwnerPortalAccounting() => Build("/portale-proprietari/contabilita");

    public string AgencyOwnerConversation(Guid conversationId) => Build($"/app/owners/messages/{conversationId:D}");

    public string AccountPeople() => Build("/app/account/people");

    public string PublicPage(string path) => Build(EnsureAbsolutePath(path));

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

    public string OnSiteRequestConfirmation(string orgSlug, Guid bookingId, string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(orgSlug);
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        return Build(
            $"/book/{Uri.EscapeDataString(orgSlug)}/requests/{bookingId:D}/confirm?token={Uri.EscapeDataString(token)}");
    }

    public string HostBookingRequests() => Build("/app/short-rent/bookings?view=requests");

    public const string ConnectPaymentsPagePath = "/app/short-rent/settings/payments";

    public string ConnectOnboardingReturn() => Build(ConnectPaymentsPagePath + "?stripe_return=1");

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

    public string GuestReview(string orgSlug, Guid bookingId, string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(orgSlug);
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        return Build(
            $"/book/{Uri.EscapeDataString(orgSlug)}/booking/{bookingId:D}/review?token={Uri.EscapeDataString(token)}");
    }

    public string FiscalDocumentView(Guid documentId, string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        return Build($"/documents/view/{documentId:D}?token={Uri.EscapeDataString(token)}");
    }

    /// <summary>
    /// Settings page of the supplier console (route <c>/app/supplier/settings</c> of the web app), where the supplier's Stripe
    /// Connect onboarding starts and ends (SP-14). The supplier has no plan or billing page, so it is not one of
    /// <see cref="BillingReturnPagePaths"/> (those are the pages of the Stripe Checkout and billing portal of the plans).
    /// </summary>
    public const string SupplierSettingsPagePath = "/app/supplier/settings";

    public string SupplierConnectOnboardingReturn() => Build(SupplierSettingsPagePath + "?stripe_return=1");

    public string SupplierConnectOnboardingRefresh() => Build(SupplierSettingsPagePath + "?stripe_refresh=1");

    public string SupplierSettings() => Build(SupplierSettingsPagePath);

    public string GuestBookings(string orgSlug, string? bookingCode = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(orgSlug);
        var path = $"/book/{Uri.EscapeDataString(orgSlug)}/my-bookings";
        return string.IsNullOrWhiteSpace(bookingCode)
            ? Build(path)
            : Build($"{path}?code={Uri.EscapeDataString(bookingCode)}");
    }

    public string SupplierShowcase(string supplierSlug)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(supplierSlug);
        return Build($"/fornitori/{Uri.EscapeDataString(supplierSlug)}");
    }

    /// <summary>
    /// Page of the supplier's showcase where the customer checks the e-mail of a booking (route
    /// <c>/fornitori/:slug/conferma</c>, SP-10): the page reads <c>hold</c> from the query and the <c>token</c> from the <b>fragment</b>
    /// — which a browser never sends to a server, so the token is in no access log (SP-15c-a, FE-SP10-1: the link of the booking used to
    /// carry it in the query) — and sends them to <c>POST api/public/suppliers/{slug}/bookings/{id}/confirm-email</c>. Only the id and the
    /// random token (single use, valid for the minutes of the hold) are in the link, no personal data.
    /// </summary>
    public string SupplierBookingConfirmation(string supplierSlug, Guid holdId, string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(supplierSlug);
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        return Build(
            $"/fornitori/{Uri.EscapeDataString(supplierSlug)}/conferma?hold={holdId:D}#token={Uri.EscapeDataString(token)}");
    }

    /// <summary>
    /// Page of the supplier's showcase where the customer checks the e-mail of a request on measure (SP-10b; the same route as the booking's,
    /// <c>/fornitori/:slug/conferma</c>, told apart by the query key): the page reads <c>inquiry</c> from the query and <c>token</c> from the
    /// <b>fragment</b> — which a browser never sends to a server, so the token is in no access log — and sends them to
    /// <c>POST api/public/suppliers/{slug}/inquiries/{id}/confirm-email</c>. Only the id and the random token (single use, valid for the minutes
    /// of the inquiry) are in the link, no personal data.
    /// </summary>
    public string SupplierInquiryConfirmation(string supplierSlug, Guid inquiryId, string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(supplierSlug);
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        return Build(
            $"/fornitori/{Uri.EscapeDataString(supplierSlug)}/conferma?inquiry={inquiryId:D}#token={Uri.EscapeDataString(token)}");
    }

    public string SupplierBookingRequest(string supplierSlug, string? publicCode = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(supplierSlug);
        var path = $"/fornitori/{Uri.EscapeDataString(supplierSlug)}/richiesta";
        return string.IsNullOrWhiteSpace(publicCode)
            ? Build(path)
            : Build($"{path}?code={Uri.EscapeDataString(Casazen.Core.Services.BookingCodes.Format(publicCode))}");
    }

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

    public string ServicePayment(Guid paymentId, string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        return Build($"/service/pay/{paymentId:D}?token={Uri.EscapeDataString(token)}");
    }

    public string HostLease(Guid leaseId) => Build($"/app/long-rent/leases/{leaseId:D}");

    public string HostBooking(Guid bookingId) => Build($"/app/short-rent/bookings/{bookingId:D}");

    public string HostBookingMessages(Guid bookingId) => Build($"/app/short-rent/bookings/{bookingId:D}?tab=messages");

    public string HostMessageThread(string threadKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(threadKey);
        return Build($"/app/short-rent/messages/{threadKey}");
    }

    public string HostApprovalQueue() => Build("/app/short-rent/approval-queue");

    public string HostProperty(Guid propertyId, Core.Entities.Enums.RentalMode mode) =>
        Build($"/app/{(mode == Core.Entities.Enums.RentalMode.Long ? "long-rent" : "short-rent")}/properties/{propertyId:D}");

    public string HostPropertyActivation(Guid propertyId) => Build($"/app/short-rent/properties/{propertyId:D}/activation");

    /// <summary>Host console: CIN compliance page (route <c>/app/short-rent/compliance/cin</c>, CO-20).</summary>
    public string HostCinCompliance() => Build("/app/short-rent/compliance/cin");

    public string HostCompliance() => Build("/app/short-rent/compliance");

    public const string BillingPagePath = "/app/short-rent/settings/plan";

    public const string AccountBillingPagePath = "/app/account/billing";

    /// <summary>
    /// Allow-list of the pages Stripe may send the browser back to (PL-16, A1-36): the plan and billing pages of the web
    /// app in each rental context, so a long-term landlord comes back to its own shell, and the billing page of the
    /// account area (AM-08a-FU1). Exact paths, no query string.
    /// </summary>
    public static readonly IReadOnlySet<string> BillingReturnPagePaths = new HashSet<string>(StringComparer.Ordinal)
    {
        BillingPagePath,
        "/app/short-rent/settings/billing",
        "/app/long-rent/settings/plan",
        "/app/long-rent/settings/billing",
        AccountBillingPagePath,
        "/onboarding",
    };

    public static bool IsBillingReturnPagePath(string? path) =>
        path is not null && BillingReturnPagePaths.Contains(path);

    public string BillingCheckoutSuccess(string? returnPath = null) => Build(BillingPage(returnPath) + "?checkout=success");

    public string BillingCheckoutCancel(string? returnPath = null) => Build(BillingPage(returnPath) + "?checkout=cancel");

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
