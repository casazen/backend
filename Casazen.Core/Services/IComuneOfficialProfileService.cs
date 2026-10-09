namespace Casazen.Core.Services;

/// <summary>
/// Ensures the official MEF tourist-tax profile of a comune exists and is current, then projects extracted
/// amounts onto versioned <c>TouristTaxRates</c>. Never invents an amount.
/// </summary>
public interface IComuneOfficialProfileService
{
    /// <summary>
    /// Retrieve or reuse the profile of this ISTAT code (property create / PATCH). Input is only the code.
    /// </summary>
    Task<ComuneOfficialEnsureResult> EnsureAsync(string istatCode, CancellationToken cancellationToken = default);

    /// <summary>
    /// Monthly delta: already profiled comuni only. Same CSV SHA writes no new version; a new act SHA adds one.
    /// </summary>
    Task<ComuneOfficialRefreshResult> RefreshAllAsync(CancellationToken cancellationToken = default);
}

public sealed record ComuneOfficialEnsureResult(
    string IstatCode,
    string Status,
    bool Reused,
    bool RatesProjected,
    string? Detail);

public sealed record ComuneOfficialRefreshResult(
    string IndexStatus,
    int ProfilesVisited,
    int VersionsAdded,
    int Unchanged,
    string? Detail);
