namespace Casazen.Core.Options;

/// <summary>
/// The <c>Marketplace:Health</c> section (E09/F03/US01/T01, NF-57): how the health panel of the active integrations warns about the quota of the
/// channel manager's API. Nothing is required and the default is in code, so no entry is committed in <c>appsettings.json</c>; the one setting is
/// <c>Marketplace:Health:QuotaWarnPercent</c> (<c>Marketplace__Health__QuotaWarnPercent</c> on Railway), read at every sync.
/// </summary>
public sealed class MarketplaceHealthOptions
{
    public const string SectionName = "Marketplace:Health";

    /// <summary>The share of the quota, in percent, from which the panel warns when nothing is configured.</summary>
    public const int DefaultQuotaWarnPercent = 80;

    /// <summary>
    /// The share of <c>quotaLimit</c> that <c>quotaStatus</c> (the units of the quota used in the window) must reach for the warning
    /// (<c>Marketplace:Health:QuotaWarnPercent</c>, default 80). A value outside 1 to 100 is read as the default: a mistyped setting never turns the
    /// warning off, and never makes it fire at once.
    /// </summary>
    public int QuotaWarnPercent { get; set; } = DefaultQuotaWarnPercent;

    /// <summary><see cref="QuotaWarnPercent"/> as the sync uses it: itself when it is from 1 to 100, otherwise <see cref="DefaultQuotaWarnPercent"/>.</summary>
    public int EffectiveQuotaWarnPercent => QuotaWarnPercent is >= 1 and <= 100 ? QuotaWarnPercent : DefaultQuotaWarnPercent;
}
