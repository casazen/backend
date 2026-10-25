namespace Casazen.Core.Entities.Enums;

/// <summary>
/// Tax treatment of property management commission for owner settlement statements (ADR-012).
/// </summary>
public enum CommissionTreatment
{
    /// <summary>Commission is deductible from the owner's gross revenue (default for cedolare secca).</summary>
    Deductible = 0,

    /// <summary>Commission is not deductible; owner pays tax on full gross revenue.</summary>
    NotDeductible = 1,
}
