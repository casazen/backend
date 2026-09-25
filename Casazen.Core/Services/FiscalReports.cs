using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

/// <summary>Period of a report: calendar dates in Europe/Rome, both included (CO-19).</summary>
public sealed record FiscalReportPeriod(DateOnly From, DateOnly To)
{
    /// <summary>Longest period of a report, in days (a leap year).</summary>
    public const int MaxDays = 366;

    public static FiscalReportPeriod WholeYear(int year) => new(new DateOnly(year, 1, 1), new DateOnly(year, 12, 31));

    /// <summary>From is not after To and the period lasts at most <see cref="MaxDays"/> days.</summary>
    public bool IsValid => From <= To && To.DayNumber - From.DayNumber < MaxDays;

    public bool IsWithinYear(int year) => From.Year == year && To.Year == year;
}

/// <summary>
/// Rates and legal sources the reports apply, from configuration (<c>ShortStayFiscal</c>, fiscale.md § CO-18), printed in
/// the notes of the PDF and shown by the frontend.
/// </summary>
public sealed record FiscalReportRules(
    decimal CedolareRate,
    decimal CedolareReducedRate,
    string CedolareSource,
    decimal OtaWithholdingRate,
    string OtaWithholdingSource,
    int MaxApartmentsPerTaxpayer,
    string ThresholdSource);

/// <summary>One property in the fiscal summary of a period (CO-19).</summary>
/// <param name="Regime">Regime assigned for the tax year, if any.</param>
/// <param name="GrossIncome">
/// Payments completed in the period (payment date in Europe/Rome), refunds deducted. Tourist tax included when the guest
/// paid it with the stay.
/// </param>
/// <param name="Withholding">OTA withholding recorded on those payments (a prepayment, fiscale.md C7).</param>
/// <param name="Net">GrossIncome - Withholding: what the host received.</param>
/// <param name="TouristTax">
/// Tourist tax included in <paramref name="GrossIncome"/>: the amount recorded on the stay by the tourist tax engine
/// (BK-03), shared among the stay's payments in proportion to their amount. Paid by the guest for the comune.
/// </param>
/// <param name="RentalIncome">GrossIncome - TouristTax: gross rent (canone lordo).</param>
/// <param name="Commissions">OTA commissions and payment fees are not recorded in CasaZen: always null ("n.d.").</param>
/// <param name="TaxableIncome">
/// Cedolare secca only: the gross rent, no deduction (fiscale.md: expenses are deductible only in a business regime or for a
/// sublessor). Null when not estimated (<paramref name="TaxNote"/> says why).
/// </param>
/// <param name="TaxRate">Cedolare rate from configuration (fraction); null when not estimated.</param>
/// <param name="EstimatedTax">TaxableIncome x TaxRate, rounded to the cent; null when not estimated.</param>
/// <param name="TaxNote">A <see cref="FiscalTaxNotes"/> code when the tax is not estimated.</param>
/// <param name="TaxpayerIndex">Index in <see cref="AnnualIncomeReport.Taxpayers"/>; null when the property has no taxpayer this year.</param>
public record AnnualIncomeLine(
    Guid PropertyId,
    string Name,
    StrFiscalRegime? Regime,
    decimal GrossIncome,
    decimal Withholding,
    decimal Net,
    decimal TouristTax,
    decimal RentalIncome,
    decimal? Commissions,
    decimal? TaxableIncome,
    decimal? TaxRate,
    decimal? EstimatedTax,
    string? TaxNote,
    int? TaxpayerIndex);

/// <param name="TaxableIncome">Sum of the lines with an estimate.</param>
/// <param name="EstimatedTax">Sum of the lines with an estimate.</param>
/// <param name="LinesWithoutEstimate">Lines with income but no estimate (IRPEF ordinaria, impresa, no regime, threshold).</param>
public record AnnualIncomeTotals(
    decimal GrossIncome,
    decimal Withholding,
    decimal Net,
    decimal TouristTax,
    decimal RentalIncome,
    decimal TaxableIncome,
    decimal EstimatedTax,
    int LinesWithoutEstimate);

/// <summary>Subtotal of one taxpayer (titolare fiscale) in the summary (fiscale.md: report per contribuente e per immobile).</summary>
public record AnnualTaxpayerTotals(
    int Index,
    string? FiscalCodeMasked,
    bool IsOrgTaxProfile,
    bool ThresholdExceeded,
    int Properties,
    decimal RentalIncome,
    decimal Withholding,
    decimal EstimatedTax);

public record AnnualIncomeReport(
    int TaxYear,
    string PackLabel,
    string Disclaimer,
    IReadOnlyList<AnnualIncomeLine> Properties,
    AnnualIncomeTotals Totals,
    FiscalReportPeriod Period,
    string OrgName,
    DateOnly GeneratedOn,
    IReadOnlyList<AnnualTaxpayerTotals> Taxpayers,
    FiscalReportRules Rules);

public record WithholdingOtaBucket(string Source, decimal Gross, decimal Withholding, decimal Net, int PayoutCount);

/// <param name="PaidAt">Payment instant (UTC).</param>
/// <param name="PaidOn">Payment date in Europe/Rome.</param>
/// <param name="BookingCode">Booking code shown to host and guest (XXXXX-XXXXX).</param>
public record WithholdingLine(
    Guid PaymentId,
    Guid PropertyId,
    string Source,
    DateTime PaidAt,
    decimal Gross,
    decimal Withholding,
    decimal Net,
    string PropertyName,
    string BookingCode,
    DateOnly PaidOn,
    WithholdingSource WithholdingSource);

public record WithholdingTotals(decimal Gross, decimal Withholding, decimal Net, int PayoutCount);

public record WithholdingReport(
    int TaxYear,
    string PackLabel,
    IReadOnlyList<WithholdingOtaBucket> ByOta,
    IReadOnlyList<WithholdingLine> Lines,
    FiscalReportPeriod Period,
    string Disclaimer,
    string OrgName,
    DateOnly GeneratedOn,
    WithholdingTotals Totals,
    FiscalReportRules Rules);

/// <summary>Stays of one comune whose check-in falls in one month of the period.</summary>
/// <param name="Comune">Comune of the property (its city, as typed by the host; grouped case- and accent-insensitive).</param>
/// <param name="Amount">Tourist tax recorded on the stays by the engine (BK-03).</param>
/// <param name="StaysWithoutAmount">Stays with no tourist tax recorded (rate not available, exempt guests, OTA booking...).</param>
public record TouristTaxPeriodRow(
    string Comune,
    int Year,
    int Month,
    int Stays,
    int Nights,
    int Guests,
    decimal Amount,
    int StaysWithoutAmount);

public record TouristTaxComuneTotals(string Comune, int Stays, int Nights, int Guests, decimal Amount, int StaysWithoutAmount);

public record TouristTaxStayLine(
    Guid BookingId,
    string BookingCode,
    Guid PropertyId,
    string PropertyName,
    string Comune,
    DateOnly CheckIn,
    DateOnly CheckOut,
    int Nights,
    int Guests,
    BookingSource Source,
    decimal Amount);

public record TouristTaxTotals(int Stays, int Nights, int Guests, decimal Amount, int StaysWithoutAmount);

/// <summary>
/// Tourist tax per comune and month (CO-19, part of A5-16): confirmed stays with check-in in the period and the amount the
/// tourist tax engine recorded on each (BK-03). A basis for the comune's statement, not the statement itself.
/// </summary>
public record TouristTaxReport(
    FiscalReportPeriod Period,
    string Disclaimer,
    string OrgName,
    DateOnly GeneratedOn,
    IReadOnlyList<TouristTaxPeriodRow> Rows,
    IReadOnlyList<TouristTaxComuneTotals> ByComune,
    IReadOnlyList<TouristTaxStayLine> Stays,
    TouristTaxTotals Totals);
