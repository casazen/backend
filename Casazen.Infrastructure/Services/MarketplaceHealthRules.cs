using Casazen.Core.Services;

namespace Casazen.Infrastructure.Services;

internal enum MarketplaceConnectionHealth
{
    Unrecognised = 0,

    Healthy = 1,

    Waiting = 2,

    Broken = 3,
}

internal readonly record struct MarketplaceConnectionVerdict(MarketplaceConnectionHealth Health, string? ErrorCode);

/// <summary>
/// The rules of the health sync that do not touch a database (E09/F03/US01/T01): what a connection status means and when the quota warns. The vocabulary is the
/// one of the OpenAPI (<c>ENABLED</c>, <c>WAITING</c>, <c>DISABLED</c>); the sandbox has no channel connected, so no other word was ever seen. Every other word is
/// <see cref="MarketplaceConnectionHealth.Unrecognised"/>, which changes nothing: a healthy connection is never marked broken by a word nobody knew.
/// </summary>
internal static class MarketplaceHealthRules
{
    internal const string WaitingStatus = "WAITING";

    internal const string DisabledStatus = "DISABLED";

    public static MarketplaceConnectionVerdict Classify(string? status)
    {
        var word = status?.Trim();
        if (string.Equals(word, OctorateConnectionInfo.EnabledStatus, StringComparison.OrdinalIgnoreCase))
            return new MarketplaceConnectionVerdict(MarketplaceConnectionHealth.Healthy, null);

        if (string.Equals(word, WaitingStatus, StringComparison.OrdinalIgnoreCase))
            return new MarketplaceConnectionVerdict(MarketplaceConnectionHealth.Waiting, null);

        if (string.Equals(word, DisabledStatus, StringComparison.OrdinalIgnoreCase))
            return new MarketplaceConnectionVerdict(MarketplaceConnectionHealth.Broken, MarketplaceHealthErrorCodes.ConnectionDisabled);

        return new MarketplaceConnectionVerdict(MarketplaceConnectionHealth.Unrecognised, null);
    }

    public static MarketplaceQuota? QuotaOf(OctorateConfiguration configuration, int warnPercent, DateTime readAt)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (configuration.QuotaLimit is not { } limit || configuration.QuotaStatus is not { } used || limit <= 0 || used < 0)
            return null;

        return new MarketplaceQuota(used, limit, IsWarning(used, limit, warnPercent), readAt);
    }

    public static bool IsWarning(int used, int limit, int warnPercent) => limit > 0 && (long)used * 100 >= (long)limit * warnPercent;
}
