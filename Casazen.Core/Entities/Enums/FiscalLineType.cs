namespace Casazen.Core.Entities.Enums;

/// <summary>
/// The kind of a line of a fiscal document (D3 of the analysis of 2026-11-14): the tax rule gives a VAT rate (and, in Italy, a nature code) to each
/// type. Stored as a number: a value never changes, a new one takes the next free number.
/// </summary>
public enum FiscalLineType
{
    Accommodation = 1,

    Cleaning = 2,

    ExtraService = 3,

    /// <summary>The tourist tax collected for the comune or the canton: outside the VAT.</summary>
    TouristTax = 4,

    Commission = 5,

    Recharge = 6,

    /// <summary>An expense advanced in the name and on behalf of the owner (art. 15): outside the VAT.</summary>
    AdvancedExpense = 7,

    Other = 99,
}
