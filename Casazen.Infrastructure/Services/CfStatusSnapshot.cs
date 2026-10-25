using Casazen.Infrastructure.External;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// In-memory cache snapshot of ConcordatoFacile practice status (ADR-008-R7, ADR-008-R8). Since CF-BE it is also the memory of an outcome
/// "not available" (<see cref="IsAvailable"/> false): the status could not be read and there was no snapshot with data to fall back on,
/// so ConcordatoFacile is not asked again before <see cref="ExpiresAt"/>.
/// </summary>
internal sealed record CfStatusSnapshot
{
    public required Guid OrgId { get; init; }
    public required List<PracticeDto> Practices { get; init; }

    /// <summary>When the practices were last read successfully; null for an outcome "not available", which never was.</summary>
    public required DateTime? FetchedAt { get; init; }
    public required DateTime ExpiresAt { get; init; }
    public required bool IsStale { get; init; }
    public required string? LastError { get; init; }
    public required DateTime? LastAttemptAt { get; init; }

    /// <summary>True when <see cref="Practices"/> were read from ConcordatoFacile (even long ago); false for the memory of a failed read.</summary>
    public bool IsAvailable { get; init; } = true;

    /// <summary>
    /// The latest statusChangedAt among all practices in the snapshot (ADR-008-R14).
    /// </summary>
    public DateTime? LatestStatusChangedAt =>
        Practices.Count > 0 ? Practices.Max(p => p.StatusChangedAt) : null;
}
