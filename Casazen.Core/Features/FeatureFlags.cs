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

    /// <summary>Every flag, in the order exposed to the frontend.</summary>
    public static IReadOnlyList<string> All { get; } = [OtaPartnerApi];
}
