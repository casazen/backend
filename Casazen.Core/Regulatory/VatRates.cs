using Casazen.Core.Services;

namespace Casazen.Core.Regulatory;

/// <summary>The kind of a VAT rate of a country (<see cref="VatRates"/>).</summary>
public enum VatRateKind
{
    /// <summary>The standard rate: what a cleaning company that applies VAT charges (Switzerland 8.1, Italy 22).</summary>
    Standard = 1,

    /// <summary>The reduced rate of Switzerland (2.6). Italy has no row of this kind in the table.</summary>
    Reduced = 2,

    /// <summary>The rate of the lodging: Switzerland the special rate for accommodation (3.8, by law until 31/12/2027), Italy the rate of the accommodation in a structure (10).</summary>
    Lodging = 3,
}

/// <summary>One row of <see cref="VatRates"/>: the rate of a kind in a country, in force from <paramref name="ValidFrom"/> to <paramref name="ValidTo"/>, both days included.</summary>
/// <param name="Country">The country (ISO 3166-1 alpha-2): <c>CH</c> or <c>IT</c>.</param>
/// <param name="Kind">The kind of the rate.</param>
/// <param name="Percent">The rate in percent.</param>
/// <param name="ValidFrom">The first day the table vouches for the rate (not the day the rate began: the table covers nothing before 2024).</param>
/// <param name="ValidTo">The last day the rate is in force by law; <c>null</c> = no end known.</param>
/// <param name="Source">Where the rate was read (research R4 of 20/11/2026).</param>
public sealed record VatRate(string Country, VatRateKind Kind, decimal Percent, DateOnly ValidFrom, DateOnly? ValidTo, string Source);

/// <summary>
/// The VAT rates of Switzerland and Italy with their validity (FIX/BE-CLEANING-VAT, decision 4 of <c>fatturazione/decisioni-fiscali-2026-11-20.md</c>, research
/// <c>fatturazione/ricerche/R4-iva-pulizie.md</c>): the one place a rate is written. The question of a cleaning recharged to the owner («La ditta di pulizie ha
/// applicato l'IVA su questa fattura?») reads the standard rate of the country of the cleaning on its day from here, never from a constant.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Never a silent default.</b> A day outside the validity of every row of a kind (before 2024, or the Swiss lodging rate after 31/12/2027) has no rate:
/// <see cref="Find"/> answers <c>null</c> and the caller says so, it never takes another row or a number of its own.</item>
/// <item><b>A new rate is a new row</b> from its first day, with the old row closed the day before: never an edit of a published row. The Swiss votation of
/// 29/11/2026 (8.5 and 4 from 2028 at the earliest) and the extension of the 3.8 (message of the Federal Council of 15/4/2026) are not decided: they are not in the
/// table.</item>
/// <item>A default of the product read on official sources on 20/11/2026, not tax advice: the rates from 2028 and the 3.8 of the final cleaning are for the fiduciary
/// and the accountant (runbook <c>owner-accounting.md</c>, «The VAT of the cleaning company»).</item>
/// </list>
/// </remarks>
public static class VatRates
{
    private const string Swiss = "ESTV, Schweizer Mehrwertsteuersätze (5/11/2026); LIVA/MWSTG SR 641.20 art. 25 (consolidated 1/1/2025)";
    private const string Italian = "DPR 633/72 art. 16 and Tab. A parte III n. 120 (Normattiva); from 1/1/2027 D.Lgs. 10/2026 (TU IVA)";

    /// <summary>The first day the table covers.</summary>
    public static readonly DateOnly FirstDay = new(2024, 1, 1);

    /// <summary>The rows, by country and kind; two rows of one country and kind never overlap.</summary>
    public static IReadOnlyList<VatRate> Table { get; } =
    [
        new(FiscalDocumentRules.Switzerland, VatRateKind.Standard, 8.1m, FirstDay, null, Swiss),
        new(FiscalDocumentRules.Switzerland, VatRateKind.Reduced, 2.6m, FirstDay, null, Swiss),
        new(FiscalDocumentRules.Switzerland, VatRateKind.Lodging, 3.8m, FirstDay, new DateOnly(2027, 12, 31), Swiss),
        new(FiscalDocumentRules.Italy, VatRateKind.Standard, 22m, FirstDay, null, Italian),
        new(FiscalDocumentRules.Italy, VatRateKind.Lodging, 10m, FirstDay, null, Italian),
    ];

    /// <summary>
    /// The rate of <paramref name="kind"/> in <paramref name="country"/> in force on <paramref name="day"/>, or <c>null</c> when the table has none for that day
    /// (a country it does not know, a day outside every validity): never a default.
    /// </summary>
    public static VatRate? Find(string? country, VatRateKind kind, DateOnly day)
    {
        var code = country?.Trim().ToUpperInvariant();
        return string.IsNullOrEmpty(code)
            ? null
            : Table.SingleOrDefault(rate => rate.Country == code && rate.Kind == kind && rate.ValidFrom <= day && (rate.ValidTo is not { } to || day <= to));
    }
}
