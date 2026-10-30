namespace Casazen.Core.Entities.Enums;

/// <summary>
/// Wire transfer batch lifecycle status (ADR-017 wire transfers).
/// Generated = batch created; Downloaded = file retrieved; Paid = batch confirmed as executed.
/// </summary>
public enum WireBatchStatus
{
    /// <summary>Batch generated, file not yet downloaded.</summary>
    Generated,

    /// <summary>File downloaded by the agency, awaiting execution.</summary>
    Downloaded,

    /// <summary>Batch executed (wire transfers sent to the bank).</summary>
    Paid
}
