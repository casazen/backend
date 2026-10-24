using Casazen.Core.Entities.Enums;

namespace Casazen.Web.Dtos.ConcordatoFacile;

/// <summary>
/// Response from looking up a practice by practice ID (NF-06).
/// Used when returning from the ConcordatoFacile partner portal with a practice parameter.
/// </summary>
public class PracticeLookupResponse
{
    /// <summary>Lease ID associated with the practice, or null if not yet selected</summary>
    public string? LeaseId { get; set; }

    /// <summary>ConcordatoFacile practice identifier</summary>
    public required string PracticeId { get; set; }

    /// <summary>Current status of the practice, or null if leaseId is null</summary>
    public CfPracticeStatus? Status { get; set; }

    /// <summary>Whether the user needs to manually select which lease this practice belongs to</summary>
    public required bool ManualSelectionRequired { get; set; }
}
