namespace Casazen.Core.Entities.Enums;

/// <summary>
/// How a short-stay cancellation refund is computed from the host's customization (PO 2026-10-08).
/// Stays of 28 nights or more ignore these overrides and keep the catalog policy.
/// </summary>
public enum HostCancellationRefundType
{
    /// <summary>Percent of the stay by the full/partial windows (catalog or host hours and percent).</summary>
    Percent = 0,

    /// <summary>No refund outside any catalog grace the host did not override; custom windows yield 0%.</summary>
    NonRefundable = 1,
}
