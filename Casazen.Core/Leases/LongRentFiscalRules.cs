using Casazen.Core.Entities.Enums;
using Casazen.Core.Options;
using Casazen.Core.Regulatory;
using Casazen.Core.Services;

namespace Casazen.Core.Leases;

/// <summary>
/// What the fiscal report reads of a lease (LR-11b): scalars only. The dates are the Europe/Rome calendar days of the stored
/// midnights UTC; <see cref="CurrentTermEndDate"/> is the end of the period the lease is in now (LR-14), the date every reader that
/// asks when a lease ends reads.
/// </summary>
public sealed record LongRentFiscalLease(
    Guid Id,
    Guid PropertyId,
    string PropertyName,
    string? TenantFirstName,
    string? TenantLastName,
    bool TenantAnonymized,
    FiscalRegime LegacyRegime,
    LeaseContractType ContractType,
    LeaseTaxRegime? TaxRegime,
    decimal MonthlyRent,
    DateOnly StartDate,
    DateOnly CurrentTermEndDate);

/// <summary>
/// The rules of the fiscal report of the long-term area (LR-11b), pure so that they are the same for the page and the file:
/// <list type="bullet">
/// <item><b>Accrual</b> (<c>competenza</c>): the months of the year in which the lease is in force, whole months (a month with a
/// single day of the lease counts as a month), times the monthly rent, up to <see cref="LongRentFiscalLease.CurrentTermEndDate"/>.
/// Part-months and changes of the rent are not modelled: the report says it counts whole months.</item>
/// <item><b>Cedolare secca</b>: the rate and its basis come from the tax advisory of the lease (<see cref="LeaseTaxAdvisory"/>, LT-08:
/// the same rule and the same configured parameters, so the report and the advisory of the lease can never give two rates), applied
/// to the accrual of the year.</item>
/// <item><b>Ordinary regime</b>: no tax figure. The IRPEF depends on the landlord's total income, which CasaZen does not hold
/// (decision LR-D21, reduced to this by the review of the perimeter); registration tax and stamp duty are not estimated either.
/// A lease with no recorded regime has none either: no guess.</item>
/// </list>
/// Money is rounded to the cent, half away from zero, like the advisory.
/// </summary>
public static class LongRentFiscalRules
{
    /// <summary>The first year a report may be asked for.</summary>
    public const int FirstYear = 2000;

    private const int MonthsPerYear = 12;

    // The notes of the advisory that qualify a cedolare figure: whether the reduced rate may apply (the comune) and what the
    // reliefs need. The others of the advisory are about the registration, the Questura or the annualization of a short term, which
    // this report does not do.
    private static readonly HashSet<string> CedolareNotes =
    [
        LeaseTaxAdvisoryNoteCodes.AtaUnverified,
        LeaseTaxAdvisoryNoteCodes.AtaNotListed,
        LeaseTaxAdvisoryNoteCodes.EmergencyComuniNotChecked,
        LeaseTaxAdvisoryNoteCodes.ConcordatoAttestationRequired,
        LeaseTaxAdvisoryNoteCodes.TransitorioReliefsToConfirm,
    ];

    /// <summary>
    /// Whether a lease ran at all: the last day of the period it is in now (<paramref name="currentTermEnd"/>) is after its first day.
    /// A lease ended on the day it starts, or before, never ran and is in no report (decision of the product owner, 2026-11-01);
    /// without this the whole-month rule would give it a month of rent and a tax estimate.
    /// </summary>
    public static bool Ran(DateOnly start, DateOnly currentTermEnd) => currentTermEnd > start;

    /// <summary>
    /// The months of <paramref name="year"/> with at least one day of the lease, from <paramref name="start"/> to
    /// <paramref name="end"/> (both included): 0 for a lease that does not touch the year or whose dates are in disorder.
    /// </summary>
    public static int MonthsInYear(DateOnly start, DateOnly end, int year)
    {
        if (end < start || start.Year > year || end.Year < year)
            return 0;

        var first = start.Year < year ? 1 : start.Month;
        var last = end.Year > year ? MonthsPerYear : end.Month;
        return last - first + 1;
    }

    /// <summary>
    /// Whether the rent register starts after the first month of the lease in <paramref name="year"/>: then the collected rent of the
    /// year is only part of what was paid, because the months before <paramref name="trackedFrom"/> were not tracked in CasaZen.
    /// </summary>
    public static bool IsCollectedPartial(DateOnly trackedFrom, DateOnly start, int year)
    {
        var firstMonthOfTheLease = start.Year < year ? 1 : start.Month;
        var firstTrackedMonth = ((trackedFrom.Year - year) * MonthsPerYear) + trackedFrom.Month;
        return firstTrackedMonth > firstMonthOfTheLease;
    }

    /// <summary>
    /// The row of a lease for <paramref name="year"/>. <paramref name="trackedFrom"/> is the first day of the first installment of the
    /// lease in the rent register (not cancelled), <c>null</c> when the register holds none; <paramref name="collected"/> the sum of the
    /// installments paid in the year, <c>null</c> or 0 when there are none. <paramref name="ata"/> is the high-tension status of the
    /// comune, read only for a canone concordato lease.
    /// </summary>
    public static LongRentFiscalRow Row(
        LongRentFiscalLease lease,
        int year,
        decimal? collected,
        DateOnly? trackedFrom,
        HighTensionAreaStatus ata,
        CedolareAdvisoryOptions options)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(options);

        var months = MonthsInYear(lease.StartDate, lease.CurrentTermEndDate, year);
        var accrual = Money(months * lease.MonthlyRent);
        var (tax, notes) = Tax(lease, year, accrual, ata, options);

        return new LongRentFiscalRow(
            lease.Id,
            lease.PropertyId,
            lease.PropertyName,
            lease.TenantFirstName,
            lease.TenantLastName,
            lease.TenantAnonymized,
            lease.ContractType,
            lease.TaxRegime,
            lease.StartDate,
            lease.CurrentTermEndDate,
            months,
            lease.MonthlyRent,
            accrual,
            trackedFrom is null ? null : Money(collected ?? 0m),
            trackedFrom,
            trackedFrom is { } from && IsCollectedPartial(from, lease.StartDate, year),
            tax,
            notes);
    }

    public static LongRentFiscalTotals Totals(IReadOnlyList<LongRentFiscalRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        return new LongRentFiscalTotals(
            rows.Count,
            rows.Sum(r => r.AccrualEur),
            rows.Sum(r => r.CollectedEur ?? 0m),
            rows.Sum(r => r.Tax.AmountEur ?? 0m),
            rows.Count(r => r.Tax.Status == LongRentFiscalTaxStatus.CedolareEstimated),
            rows.Count(r => r.Tax.Status != LongRentFiscalTaxStatus.CedolareEstimated));
    }

    private static (LongRentFiscalTax Tax, IReadOnlyList<string> Notes) Tax(
        LongRentFiscalLease lease, int year, decimal accrual, HighTensionAreaStatus ata, CedolareAdvisoryOptions options)
    {
        switch (lease.TaxRegime)
        {
            case LeaseTaxRegime.CedolareSecca:
                // The advisory of the lease, on this lease alone and without the data CasaZen does not hold: only its cedolare rate and
                // the notes about it are read. The amount is the accrual of the year, not the annual rent the advisory shows.
                var facts = new LeaseTaxFacts(
                    lease.LegacyRegime, lease.ContractType, lease.TaxRegime, lease.MonthlyRent, Term: null, ata, HasExtraEuTenant: false);
                var advisory = LeaseTaxAdvisory.Compute(facts, CedolareAdvisoryInput.None, options, year);
                var cedolare = advisory.Cedolare;
                return (
                    new LongRentFiscalTax(
                        LongRentFiscalTaxStatus.CedolareEstimated, cedolare.Rate, cedolare.RateBasis, Money(accrual * cedolare.Rate)),
                    advisory.Notes.Where(CedolareNotes.Contains).ToList());

            case LeaseTaxRegime.Ordinario:
                return (new LongRentFiscalTax(LongRentFiscalTaxStatus.OrdinaryNotEstimated, null, null, null), []);

            default:
                return (
                    new LongRentFiscalTax(LongRentFiscalTaxStatus.RegimeUnknown, null, null, null),
                    [LeaseTaxAdvisoryNoteCodes.TaxRegimeUnknown]);
        }
    }

    private static decimal Money(decimal value) => decimal.Round(value, 2, MidpointRounding.AwayFromZero);
}
