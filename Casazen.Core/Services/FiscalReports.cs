using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

public sealed record FiscalReportPeriod(DateOnly From, DateOnly To)
{
    public const int MaxDays = 366;

    public static FiscalReportPeriod WholeYear(int year) => new(new DateOnly(year, 1, 1), new DateOnly(year, 12, 31));

    public bool IsValid => From <= To && To.DayNumber - From.DayNumber < MaxDays;

    public bool IsWithinYear(int year) => From.Year == year && To.Year == year;
}

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

public record AnnualIncomeTotals(
    decimal GrossIncome,
    decimal Withholding,
    decimal Net,
    decimal TouristTax,
    decimal RentalIncome,
    decimal TaxableIncome,
    decimal EstimatedTax,
    int LinesWithoutEstimate);

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

public record TouristTaxRemittanceNote(string Comune, string Text);

public record TouristTaxReport(
    FiscalReportPeriod Period,
    string Disclaimer,
    string OrgName,
    DateOnly GeneratedOn,
    IReadOnlyList<TouristTaxPeriodRow> Rows,
    IReadOnlyList<TouristTaxComuneTotals> ByComune,
    IReadOnlyList<TouristTaxStayLine> Stays,
    TouristTaxTotals Totals,
    IReadOnlyList<TouristTaxRemittanceNote>? RemittanceNotes = null);
