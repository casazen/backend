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

    /// <summary>Every flag, in the order exposed to the frontend.</summary>
    public static IReadOnlyList<string> All { get; } = [OtaPartnerApi, AiSupplierDiscovery, RliProvider];
}
