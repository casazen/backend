using Casazen.Infrastructure.External;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// In-memory cache snapshot of ConcordatoFacile practice status (ADR-008-R7, ADR-008-R8).
/// </summary>
internal sealed class CfStatusSnapshot
{
    public required Guid OrgId { get; init; }
    public required List<PracticeDto> Practices { get; init; }
    public required DateTime FetchedAt { get; init; }
    public required DateTime ExpiresAt { get; init; }
    public bool IsStale { get; set; }
    public string? LastError { get; set; }
    public DateTime? LastAttemptAt { get; set; }

    /// <summary>
    /// The latest statusChangedAt among all practices in the snapshot (ADR-008-R14).
    /// </summary>
    public DateTime? LatestStatusChangedAt =>
        Practices.Count > 0 ? Practices.Max(p => p.StatusChangedAt) : null;
}
