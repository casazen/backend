namespace Casazen.Core.Entities.Enums;

/// <summary>
/// The kind of a line of a fiscal document (D3 of the analysis of 2026-11-14): the tax rule gives a VAT rate (and, in Italy, a nature code) to each
/// type. Stored as a number: a value never changes, a new one takes the next free number.
/// </summary>
public enum FiscalLineType
{
    /// <summary>The stay itself (lodging).</summary>
    Accommodation = 1,

    /// <summary>The final cleaning: it follows the stay or is a service, as the profile of the issuer says.</summary>
    Cleaning = 2,

    /// <summary>A service sold to the guest besides the stay.</summary>
    ExtraService = 3,

    /// <summary>The tourist tax collected for the comune or the canton: outside the VAT.</summary>
    TouristTax = 4,

    /// <summary>The commission of the property manager (owner invoices).</summary>
    Commission = 5,

    /// <summary>A cost charged on to the owner with VAT (owner invoices).</summary>
    Recharge = 6,

    /// <summary>An expense advanced in the name and on behalf of the owner (art. 15): outside the VAT.</summary>
    AdvancedExpense = 7,

    /// <summary>Anything else.</summary>
    Other = 99,
}
