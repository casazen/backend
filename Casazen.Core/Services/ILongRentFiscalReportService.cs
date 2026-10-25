using Casazen.Core.Authorization;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

/// <summary>How the tax of a lease is estimated in the fiscal report of the long-term area (LR-11b).</summary>
public enum LongRentFiscalTaxStatus
{
    /// <summary>Cedolare secca: the rate of the tax advisory of the lease (LT-08) on the rent accrued in the year.</summary>
    CedolareEstimated = 0,

    /// <summary>
    /// Ordinary regime: no figure. The IRPEF depends on the landlord's total income, which CasaZen does not hold, so the report says
    /// so instead of giving an amount (decision LR-D21: no IRPEF in this version).
    /// </summary>
    OrdinaryNotEstimated = 1,

    /// <summary>The lease has no recorded tax regime (an older canone concordato lease): no figure, rather than a guess.</summary>
    RegimeUnknown = 2,
}

/// <summary>
/// The tax estimated for one lease. <c>Rate</c>, <c>RateBasis</c> and <c>AmountEur</c> only for
/// <see cref="LongRentFiscalTaxStatus.CedolareEstimated"/>: the rate and its basis are the ones of the tax advisory of the lease, the
/// amount is the rent accrued in the year (<see cref="LongRentFiscalRow.AccrualEur"/>) times that rate.
/// </summary>
public sealed record LongRentFiscalTax(
    LongRentFiscalTaxStatus Status,
    decimal? Rate,
    CedolareRateBasis? RateBasis,
    decimal? AmountEur);

/// <summary>
/// One lease of the fiscal report. The tenant is the first one, by name only (D29), both names null once anonymized.
/// <c>AccrualEur</c> is by <b>accrual</b>: the months of the year the lease is in force, whole months, times the monthly rent, up to
/// the end of the period the lease is in now (LR-14). <c>CollectedEur</c> is by <b>cash</b>: the installments of the rent register paid
/// in the year, so only what CasaZen tracked; <c>null</c> when the register holds nothing of the lease. <c>TrackedFrom</c> is the first
/// day of the first tracked installment; <c>CollectedIsPartial</c>: it starts after the first month of the lease in the year, so the
/// collected rent is only part of what was paid.
/// </summary>
public sealed record LongRentFiscalRow(
    Guid LeaseId,
    Guid PropertyId,
    string PropertyName,
    string? TenantFirstName,
    string? TenantLastName,
    bool TenantAnonymized,
    LeaseContractType ContractType,
    LeaseTaxRegime? TaxRegime,
    DateOnly StartDate,
    DateOnly CurrentTermEndDate,
    int MonthsInYear,
    decimal MonthlyRentEur,
    decimal AccrualEur,
    decimal? CollectedEur,
    DateOnly? TrackedFrom,
    bool CollectedIsPartial,
    LongRentFiscalTax Tax,
    IReadOnlyList<string> Notes);

/// <summary>
/// The sums of the rows. Each is the sum of the amounts the rows show (rounded to the cent), so the total always adds up. The
/// <c>CollectedEur</c> is the sum of the leases the register holds something of.
/// </summary>
/// <param name="CedolareLeases">Leases with a cedolare secca estimate.</param>
/// <param name="NotEstimatedLeases">Leases with no estimate (ordinary regime, or regime not recorded).</param>
public sealed record LongRentFiscalTotals(
    int Leases,
    decimal AccrualEur,
    decimal CollectedEur,
    decimal CedolareEur,
    int CedolareLeases,
    int NotEstimatedLeases);

/// <summary>The fiscal report of a year, for the leases the caller reaches. <c>GeneratedOn</c> is today's Europe/Rome day.</summary>
public sealed record LongRentFiscalReport(
    int Year,
    DateOnly GeneratedOn,
    IReadOnlyList<LongRentFiscalRow> Rows,
    LongRentFiscalTotals Totals);

/// <summary>
/// The fiscal report of the long-term area, per lease, for the accountant (LR-11b): read side only, for the leases the caller reaches
/// (<see cref="HostScope"/>, TN-3). It is an <b>estimate</b> and says so; every number declares its base (accrual or cash).
/// </summary>
public interface ILongRentFiscalReportService
{
    /// <summary>
    /// The leases signed by every party that are in force in some month of <paramref name="year"/> (judged on the end of the period they
    /// are in now, LR-14), with the rent accrued, the rent collected in the year according to the rent register, the regime and, for the
    /// cedolare secca, the estimated tax. Nothing is written and nothing is logged.
    /// </summary>
    Task<LongRentFiscalReport> GetAsync(HostScope scope, int year, CancellationToken cancellationToken = default);
}

/// <summary>Stable codes of the fiscal report (LR-11b): answered as 400.</summary>
public static class LongRentFiscalErrorCodes
{
    /// <summary>The year is not between <see cref="Leases.LongRentFiscalRules.FirstYear"/> and the current year.</summary>
    public const string YearInvalid = "long_rent_fiscal_year_invalid";

    /// <summary>The export was asked in a format that is not <c>csv</c>.</summary>
    public const string FormatInvalid = "long_rent_fiscal_format_invalid";
}
