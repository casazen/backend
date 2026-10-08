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
    /// until the product owner turns it on. It will gate the public booking endpoints of the showcase (404 when off, SP-09
    /// and SP-10); nothing consumes it yet, SP-02 only introduces and exposes it. The service catalog
    /// (<c>api/supplier/services</c>) does not depend on it: every supplier has one.
    /// </summary>
    public const string SupplierShowcaseBooking = "SupplierShowcaseBooking";

    /// <summary>
    /// SP-02 (redesign wave, decision D2): payment of a supplier's work inside CasaZen (direct charge on the supplier's
    /// Stripe account with the platform commission), off until the product owner turns it on. It will gate the creation of
    /// the payment requests (SP-15); nothing consumes it yet, SP-02 only introduces and exposes it. Without it the
    /// existing manual flow ("Segna pagato" by the host) stays the only one.
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
    ];
}
