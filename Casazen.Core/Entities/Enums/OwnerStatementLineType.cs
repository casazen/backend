namespace Casazen.Core.Entities.Enums;

/// <summary>
/// Type of line item in an owner statement (ADR-017).
/// </summary>
public enum OwnerStatementLineType
{
    /// <summary>Revenue from a booking collected via the platform (Stripe).</summary>
    Booking,

    /// <summary>OTA revenue manually entered or imported from CSV (ADR-017-R6).</summary>
    OtaRevenue,

    /// <summary>Expense charged to the owner (cleaning, linen, utilities, etc.).</summary>
    Expense,

    /// <summary>Manual adjustment for a correction after publication (ADR-017-R5).</summary>
    Adjustment
}
