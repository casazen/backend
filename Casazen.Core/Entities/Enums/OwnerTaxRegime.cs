namespace Casazen.Core.Entities.Enums;

/// <summary>
/// Tax regime of a property owner (ADR-012): private natural person or VAT holder (business / professional).
/// </summary>
public enum OwnerTaxRegime
{
    /// <summary>Natural person with no VAT number: subject to withholding tax (ritenuta d'acconto).</summary>
    Private = 0,

    /// <summary>VAT holder (business or professional): invoices instead of withholding.</summary>
    VatHolder = 1,
}
