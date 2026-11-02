namespace Casazen.Core.Entities.Enums;

/// <summary>
/// Owner statement lifecycle status (ADR-017).
/// Draft = generated, still editable; Approved = locked for review; Published = sent to owner.
/// </summary>
public enum StatementStatus
{
    /// <summary>Generated but not yet locked. May receive manual adjustments (OTA CSV import).</summary>
    Draft,

    /// <summary>Locked for review. No longer editable. Host or agency must review before publishing.</summary>
    Approved,

    /// <summary>Sent to the owner. No changes allowed. Corrections via adjustment rows in next month (ADR-017-R5).</summary>
    Published
}
