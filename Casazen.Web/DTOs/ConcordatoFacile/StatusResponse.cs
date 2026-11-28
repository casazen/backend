using Casazen.Core.Entities.Enums;

namespace Casazen.Web.Dtos.ConcordatoFacile;

public class StatusResponse
{
    public required IReadOnlyList<PracticeResponse> Practices { get; set; }

    public required DateTime? LastCheckedAt { get; set; }

    public required DateTime NextUpdateAt { get; set; }

    public required bool Stale { get; set; }

    /// <summary>
    /// Whether the practices come from ConcordatoFacile: true with a successful read or a valid snapshot (stale or not); false when the
    /// status API cannot be read and there is no snapshot, with the key missing or with ConcordatoFacile failing. The answer is still a
    /// 200, with no practices, <c>stale: true</c> and <c>lastCheckedAt: null</c>: an unreadable status is never "no practice" (CF-BE).
    /// </summary>
    public required bool Available { get; set; }
}

public class PracticeResponse
{
    public required string PracticeId { get; set; }

    public required CfPracticeStatus Status { get; set; }

    public required DateTime StatusChangedAt { get; set; }

    public DateTime? SubmittedAt { get; set; }

    public string? Comune { get; set; }

    public bool ComuneDiffersFromLink { get; set; }

    public string? CloseReason { get; set; }
}
