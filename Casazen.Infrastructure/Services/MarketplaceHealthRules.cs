using Casazen.Core.Services;

namespace Casazen.Infrastructure.Services;

/// <summary>What the status of a connection says about the activation that holds it (E09/F03/US01/T01).</summary>
internal enum MarketplaceConnectionHealth
{
    /// <summary>A word CasaZen has not met (or no word): nothing is concluded from it and the activation is left as it is.</summary>
    Unrecognised = 0,

    /// <summary>The connection works: the activation is Active.</summary>
    Healthy = 1,

    /// <summary>The connection is still being set up (importing, waiting for the access or for the mapping): the activation is Pending.</summary>
    Waiting = 2,

    /// <summary>The connection does not work: the activation is To fix with <see cref="MarketplaceConnectionVerdict.ErrorCode"/>.</summary>
    Broken = 3,
}

/// <summary>The verdict on the status of a connection, and, for a broken one, the code of the problem.</summary>
internal readonly record struct MarketplaceConnectionVerdict(MarketplaceConnectionHealth Health, string? ErrorCode);

/// <summary>
/// The rules of the health sync that do not touch a database (E09/F03/US01/T01): what a connection status means and when the quota warns. The vocabulary is the
/// one of the OpenAPI (<c>ENABLED</c>, <c>WAITING</c>, <c>DISABLED</c>); the sandbox has no channel connected, so no other word was ever seen. Every other word is
/// <see cref="MarketplaceConnectionHealth.Unrecognised"/>, which changes nothing: a healthy connection is never marked broken by a word nobody knew.
/// </summary>
internal static class MarketplaceHealthRules
{
    /// <summary>The <c>status</c> of a connection that is waiting for the access, the import or the mapping.</summary>
    internal const string WaitingStatus = "WAITING";

    /// <summary>The <c>status</c> of a connection that was switched off on the channel manager.</summary>
    internal const string DisabledStatus = "DISABLED";

    /// <summary>The verdict on a connection status, compared without regard to case or the spaces around it.</summary>
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

    /// <summary>
    /// The quota as the panel shows it, from the configuration Octorate reported: <c>quotaStatus</c> counts the units used and <c>quotaLimit</c> is what the quota
    /// allows. <c>null</c> when either is missing or the limit is not a positive number: a quota that cannot be read is not guessed.
    /// </summary>
    public static MarketplaceQuota? QuotaOf(OctorateConfiguration configuration, int warnPercent, DateTime readAt)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (configuration.QuotaLimit is not { } limit || configuration.QuotaStatus is not { } used || limit <= 0 || used < 0)
            return null;

        return new MarketplaceQuota(used, limit, IsWarning(used, limit, warnPercent), readAt);
    }

    /// <summary>True when <paramref name="used"/> reached <paramref name="warnPercent"/> percent of <paramref name="limit"/>.</summary>
    public static bool IsWarning(int used, int limit, int warnPercent) => limit > 0 && (long)used * 100 >= (long)limit * warnPercent;
}
