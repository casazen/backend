using Casazen.Core.Entities.Enums;
using Casazen.Core.Leases;
using Casazen.Core.Regulatory;
using Casazen.Core.Services;
using Casazen.Tests.Unit.Configuration;
using Xunit;

namespace Casazen.Tests.Unit.Leases;

/// <summary>
/// LR-11b: the money of the fiscal report of the long-term area. The one proof for the rule that would cost a landlord the most if
/// it were wrong: the rent accrued in a year is judged on the end of the period the lease is in now (a lease that renewed by itself
/// is still there), the cedolare secca is that accrual at the rate the tax advisory of the lease gives (not a rate written in the
/// report), and a lease in the ordinary regime, or with no regime recorded, gets no tax figure at all.
/// </summary>
public class LongRentFiscalRulesTests
{
    [Fact]
    public void Row_ARenewedLease_AccruesOnTheCurrentEnd_TaxesTheAccrualAtTheAdvisoryRate_AndLeavesTheOrdinaryOnesWithoutAFigure()
    {
        var options = CedolareAdvisoryConfigurationTests.FromAppsettings();

        var renewed = Lease(LeaseContractType.Libero, LeaseTaxRegime.CedolareSecca, new DateOnly(2026, 3, 1), new DateOnly(2034, 2, 28));

        // 2031 is inside the renewed period only: twelve months, 9.600 €, and the cedolare of the advisory (21%) on them.
        var year = LongRentFiscalRules.Row(renewed, 2031, collected: 4_800m, new DateOnly(2031, 7, 1), HighTensionAreaStatus.NotListed, options);
        Assert.Equal(12, year.MonthsInYear);
        Assert.Equal(9_600.00m, year.AccrualEur);
        Assert.Equal(LongRentFiscalTaxStatus.CedolareEstimated, year.Tax.Status);
        Assert.Equal(0.21m, year.Tax.Rate);
        Assert.Equal(CedolareRateBasis.Standard, year.Tax.RateBasis);
        Assert.Equal(2_016.00m, year.Tax.AmountEur);
        Assert.Equal(4_800.00m, year.CollectedEur);
        Assert.True(year.CollectedIsPartial);

        var lastYear = LongRentFiscalRules.Row(renewed, 2034, collected: null, trackedFrom: null, HighTensionAreaStatus.NotListed, options);
        Assert.Equal(2, lastYear.MonthsInYear);
        Assert.Equal(1_600.00m, lastYear.AccrualEur);
        Assert.Equal(336.00m, lastYear.Tax.AmountEur);
        Assert.Null(lastYear.CollectedEur);
        Assert.Equal(0, LongRentFiscalRules.Row(renewed, 2035, collected: null, trackedFrom: null, HighTensionAreaStatus.NotListed, options).MonthsInYear);

        var concordato = Lease(LeaseContractType.Concordato, LeaseTaxRegime.CedolareSecca, new DateOnly(2026, 1, 1), new DateOnly(2028, 12, 31));
        var reduced = LongRentFiscalRules.Row(concordato, 2027, collected: 9_600m, new DateOnly(2026, 1, 1), HighTensionAreaStatus.Verified, options);
        Assert.Equal(0.10m, reduced.Tax.Rate);
        Assert.Equal(CedolareRateBasis.ConcordatoAta, reduced.Tax.RateBasis);
        Assert.Equal(960.00m, reduced.Tax.AmountEur);
        Assert.False(reduced.CollectedIsPartial);

        var ordinary = LongRentFiscalRules.Row(
            Lease(LeaseContractType.Libero, LeaseTaxRegime.Ordinario, new DateOnly(2026, 3, 1), new DateOnly(2034, 2, 28)),
            2031, collected: 4_800m, new DateOnly(2031, 7, 1), HighTensionAreaStatus.NotListed, options);
        Assert.Equal(9_600.00m, ordinary.AccrualEur);
        Assert.Equal(LongRentFiscalTaxStatus.OrdinaryNotEstimated, ordinary.Tax.Status);
        Assert.Null(ordinary.Tax.Rate);
        Assert.Null(ordinary.Tax.AmountEur);

        var unknown = LongRentFiscalRules.Row(
            Lease(LeaseContractType.Concordato, null, new DateOnly(2026, 1, 1), new DateOnly(2028, 12, 31)),
            2027, collected: null, trackedFrom: null, HighTensionAreaStatus.Verified, options);
        Assert.Equal(LongRentFiscalTaxStatus.RegimeUnknown, unknown.Tax.Status);
        Assert.Null(unknown.Tax.AmountEur);
        Assert.Contains(LeaseTaxAdvisoryNoteCodes.TaxRegimeUnknown, unknown.Notes);

        var totals = LongRentFiscalRules.Totals([year, reduced, ordinary, unknown]);
        Assert.Equal(2_016.00m + 960.00m, totals.CedolareEur);
        Assert.Equal(2, totals.CedolareLeases);
        Assert.Equal(2, totals.NotEstimatedLeases);
    }

    [Fact]
    public void Ran_ALeaseEndedOnTheDayItStartsOrBefore_NeverRan_WhileOneThatLastsPastItsFirstDayDid()
    {
        Assert.False(LongRentFiscalRules.Ran(new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 1)));
        Assert.False(LongRentFiscalRules.Ran(new DateOnly(2026, 11, 1), new DateOnly(2026, 10, 31)));

        Assert.True(LongRentFiscalRules.Ran(new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 2)));
        Assert.True(LongRentFiscalRules.Ran(new DateOnly(2026, 11, 1), new DateOnly(2029, 10, 31)));
    }

    private static LongRentFiscalLease Lease(LeaseContractType type, LeaseTaxRegime? taxRegime, DateOnly start, DateOnly currentEnd) =>
        new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Bilocale Sparano",
            "Anna",
            "Rossi",
            TenantAnonymized: false,
            LeaseContractTerms.LegacyFiscalRegime(type, taxRegime),
            type,
            taxRegime,
            800m,
            start,
            currentEnd);
}
