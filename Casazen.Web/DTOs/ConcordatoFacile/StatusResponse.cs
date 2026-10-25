using Casazen.Core.Entities.Enums;

namespace Casazen.Web.Dtos.ConcordatoFacile;

/// <summary>
/// Response from getting practice status for a lease (ADR-008-R15).
/// Includes practices and timing metadata for frontend polling.
/// </summary>
public class StatusResponse
{
    /// <summary>List of practices for the lease</summary>
    public required IReadOnlyList<PracticeResponse> Practices { get; set; }

    /// <summary>When the backend last checked the CF API (ISO8601)</summary>
    public required DateTime LastCheckedAt { get; set; }

    /// <summary>When the frontend should poll again (ISO8601)</summary>
    public required DateTime NextUpdateAt { get; set; }

    /// <summary>Whether the snapshot is stale due to CF API error</summary>
    public required bool Stale { get; set; }
}

/// <summary>
/// Practice detail in the status response.
/// </summary>
public class PracticeResponse
{
    /// <summary>CF practice identifier</summary>
    public required string PracticeId { get; set; }

    /// <summary>Current status of the practice</summary>
    public required CfPracticeStatus Status { get; set; }

    /// <summary>When the status last changed (ISO8601)</summary>
    public required DateTime StatusChangedAt { get; set; }

    /// <summary>When the practice was submitted to CF (ISO8601), if available</summary>
    public DateTime? SubmittedAt { get; set; }

    /// <summary>Municipality associated with the practice by the partner</summary>
    public string? Comune { get; set; }

    /// <summary>
    /// Whether the municipality differs from the lease property municipality. ConcordatoFacile also says "unknown" (null: the link named no
    /// comune); there is nothing to warn about then and it is sent as false, so the contract with the frontend stays a boolean.
    /// </summary>
    public bool ComuneDiffersFromLink { get; set; }

    /// <summary>Reason for closure, if the practice is closed (duplicate, withdrawn, not_compliant, other)</summary>
    public string? CloseReason { get; set; }
}
