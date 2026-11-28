namespace Casazen.Core.Services;

public interface IComuneOfficialProfileService
{
    /// <summary>
    /// Retrieve or reuse the profile of this ISTAT code (property create / PATCH). Input is only the code.
    /// </summary>
    Task<ComuneOfficialEnsureResult> EnsureAsync(string istatCode, CancellationToken cancellationToken = default);

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
