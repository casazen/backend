namespace Casazen.Core.Features;

/// <summary>
/// Feature flags: one boolean per flag in the <c>Features</c> configuration section
/// (<c>Features:OtaPartnerApi</c>, environment variable <c>Features__OtaPartnerApi</c>).
/// A missing or non-boolean value means <b>off</b>: a flag is on only when explicitly set to <c>true</c>.
/// </summary>
/// <remarks>
/// Adding a flag (docs/runbooks/feature-flags.md): a constant here, listed in <see cref="All"/> (the frontend reads it
/// from <c>GET /api/public/features</c> as a camelCase key), <c>[FeatureGate(FeatureFlags.X)]</c> on the endpoints,
/// <see cref="IFeatureFlags"/> in background jobs and services.
/// </remarks>
public static class FeatureFlags
{
    public const string SectionName = "Features";

    /// <summary>
    /// D10: Airbnb / Booking.com partner API integrations (#31-35, in freeze): <c>api/ota</c>,
    /// <c>api/properties/{id}/ota-integrations</c>, <c>webhooks/ota/{platform}</c>, the OTA adapters and the
    /// <c>ota-sync-all</c> / <c>booking-pull-all</c> recurring jobs. iCal import/export is not behind this flag.
    /// </summary>
    public const string OtaPartnerApi = "OtaPartnerApi";

    /// <summary>
    /// D11: AI supplier discovery (US-014, in freeze): <c>POST api/service-requests/match-supplier</c>, the external
    /// web search and the LLM extraction of "nearby businesses" (<c>IAiSupplierDiscoveryService</c>) and the AI match
    /// reason. Off: the endpoint answers 404 and no call reaches the AI provider. The manual service request
    /// (<c>POST api/service-requests</c> with a chosen supplier) is not behind this flag.
    /// </summary>
    public const string AiSupplierDiscovery = "AiSupplierDiscovery";

    /// <summary>
    /// D15 / LT-01: RLI filing through an external provider (Openapi DocuEngine, docs/integrations/rli-esign.md), off
    /// until the legal opinion on the provider's filing professional and a real provider client exist. Off:
    /// <c>POST api/leases/{id}/registration</c> answers 404, the <c>lease-registration-status-poll</c> job is not
    /// registered and no call reaches the provider. The manual registration (the landlord files on the official
    /// channel and records number, date and receipt) is not behind this flag and is the default path.
    /// </summary>
    public const string RliProvider = "RliProvider";

    /// <summary>
    /// D15 / LT-02: contract signature through an external e-signature provider (docs/integrations/rli-esign.md §3), off
    /// until the budget and the legal opinion on the FEA exist and a real provider client is written. Off:
    /// <c>POST api/leases/{id}/signing</c> and <c>POST webhooks/esign</c> answer 404, the <c>lease-sign-status-poll</c>
    /// job is not registered and no call reaches a provider. The offline signature (download the contract, have it
    /// signed, upload the signed PDF with the stipula date) is not behind this flag and is the default path. On:
    /// <c>ESign:WebhookSecret</c> is required at startup.
    /// </summary>
    public const string ESignProvider = "ESignProvider";

    /// <summary>
    /// SP-02 (redesign wave, decision D34): booking from the public showcase of a supplier (<c>/fornitori/{slug}</c>), off
    /// until the product owner turns it on. It gates the public reads of the showcase (services, slots, estimate: SP-09) and
    /// the booking endpoints (hold and e-mail check: SP-10) and the customer's own area of a booking (find it again, cancel it,
    /// move it, answer a proposed time: SP-11, <c>api/public/supplier-bookings/*</c>), 404 when off. <b>SP-10 and SP-11 both have
    /// to be deployed before it is turned on</b>: a customer who can book and cannot manage the booking is a promise the e-mails
    /// make and the product does not keep. <b>On, the application needs
    /// <c>Suppliers:Showcase:PrivacyNoticeVersion</c> and, outside Development and Testing, <c>Suppliers:CustomerIndexKey</c></b>
    /// or it does not start (<see cref="Casazen.Core.Options.ShowcaseBookingOptionsValidator"/>). The upkeep jobs of the
    /// bookings are registered whatever the flag says. The service catalog (<c>api/supplier/services</c>) does not depend on
    /// it: every supplier has one.
    /// </summary>
    public const string SupplierShowcaseBooking = "SupplierShowcaseBooking";

    /// <summary>
    /// SP-02 (redesign wave, decision D2): payment of a supplier's work inside CasaZen (direct charge on the supplier's
    /// Stripe account with the platform commission), off until the product owner turns it on. SP-14 puts the supplier's Stripe
    /// Connect account behind it (<c>api/supplier/payments/*</c>: onboarding, state, Express Dashboard link; 404 while it is
    /// off); SP-15 will also gate the creation of the payment requests. The processing of the Stripe webhooks
    /// (<c>account.updated</c> and, later, the payments in flight) is not behind it. Without it the existing manual flow
    /// ("Segna pagato" by the host) stays the only one.
    /// </summary>
    public const string SupplierOnlinePayments = "SupplierOnlinePayments";

    /// <summary>
    /// SP-04 (redesign wave, decision D8): automatic cancellation of the service requests nobody answered. Every request now
    /// gets a deadline (<c>ResponseDueAt</c>, 120 minutes after its creation for a host's request); with the flag on, the
    /// recurring job <c>service-request-auto-cancel</c> (every 10 minutes) moves the new requests past their deadline to
    /// <c>Annullato</c> (reason <c>NoResponse</c>) and tells the host and the supplier. <b>Off by default</b>: it changes what
    /// happens to the requests that exist, so the product owner turns it on when the console and the apps show the new
    /// status. Off: the job is not scheduled (an earlier schedule is removed) and nothing is ever cancelled by time.
    /// </summary>
    public const string SupplierRequestAutoCancel = "SupplierRequestAutoCancel";

    /// <summary>
    /// BL-01 (redesign wave, decision 01-D8): gradual rollout of the new interface (UI v2: design tokens, single shell, navigation
    /// by area, in-app guide). Off by default, the product owner turns it on. The backend only introduces and exposes it: no
    /// endpoint, service or job reads it. The frontend reads <c>uiRedesign</c> from <c>GET /api/public/features</c> and switches
    /// the new look on or off (<c>html[data-ui='v2']</c>, UI-01).
    /// </summary>
    public const string UiRedesign = "UiRedesign";

    /// <summary>
    /// AM-01 (wave decision "team members behind a flag"): the org team, i.e. several people in one org with a role each
    /// (owner, administrator, property manager, collaborator, accountant). Off by default. The model, the roles, the
    /// backfill and the authorization guards of AM-01 work whatever its value; what it controls is what the clients can
    /// see and use of it: with it off <c>GET /api/me/contexts</c> does not list the <c>account</c> context (the web app of
    /// today does not know it and its workspace switcher fails on an unknown context), and the invitation endpoints of
    /// AM-02 are gated by it (404 while off). Turn it on only together with the account screens of AM-04.
    /// </summary>
    public const string OrgTeam = "OrgTeam";

    /// <summary>
    /// PM-02 / D16: the scheduled change of rental mode of a property (short stays to long-term leases and back). Off:
    /// <c>GET api/properties/{id}/mode</c>, <c>GET …/mode/preview</c>, <c>POST …/mode/change</c> and
    /// <c>DELETE …/mode/change/{changeId}</c> answer 404 before anything is read, and the hourly <c>property-mode-change</c>
    /// job is not registered (removed with <c>RemoveIfExists</c>): a change already programmed waits, untouched, until the
    /// flag is turned on again. The mode itself (PM-01) and everything that reads it are not behind this flag.
    /// </summary>
    public const string PropertyModeChange = "PropertyModeChange";

    /// <summary>Every flag, in the order exposed to the frontend.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        OtaPartnerApi,
        AiSupplierDiscovery,
        RliProvider,
        ESignProvider,
        SupplierShowcaseBooking,
        SupplierOnlinePayments,
        SupplierRequestAutoCancel,
        UiRedesign,
        OrgTeam,
        PropertyModeChange,
    ];
}
