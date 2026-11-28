using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Leases;
using Xunit;

namespace Casazen.Tests.Unit.Leases;

/// <summary>
/// LR-OPS-1c: the ISTAT update of a rent. The new rent is the rent plus the share of the variation of the FOI index the contract recognizes,
/// the increase rounded once to the cent (half away from zero); the update never lowers the rent; the cedolare secca suspends it; it is not
/// retroactive. "Today" is 10 October 2026.
/// </summary>
public class LeaseIstatRulesTests
{
    private static readonly DateOnly Today = new(2026, 10, 10);

    private static DateTime Utc(int year, int month, int day) => new(year, month, day, 0, 0, 0, DateTimeKind.Utc);

    private static LeaseContract Lease(LeaseTaxRegime? regime = LeaseTaxRegime.Ordinario, decimal rent = 650m) => new()
    {
        Status = LeaseStatus.Registered,
        ContractType = LeaseContractType.Libero,
        TaxRegime = regime,
        MonthlyRent = rent,
        StartDate = Utc(2025, 11, 1),
        EndDate = Utc(2029, 10, 31),
    };

    [Fact]
    public void Calculate_TheShareOfTheVariation_IsAppliedToTheRent_AndTheNewRentHasTheCents()
    {
        var seventyFive = LeaseIstatRules.Calculate(650m, 3.2m, 75m);

        Assert.Equal(2.4m, seventyFive.AppliedPercent);
        Assert.Equal(15.60m, seventyFive.Increase);
        Assert.Equal(665.60m, seventyFive.NewRent);

        var whole = LeaseIstatRules.Calculate(800m, 1.5m, 100m);

        Assert.Equal(1.5m, whole.AppliedPercent);
        Assert.Equal(12.00m, whole.Increase);
        Assert.Equal(812.00m, whole.NewRent);
    }

    [Fact]
    public void Calculate_RoundsTheIncreaseOnceToTheCent_HalfAwayFromZero()
    {
        var half = LeaseIstatRules.Calculate(10.50m, 1.00m, 100m);

        Assert.Equal(0.11m, half.Increase);
        Assert.Equal(10.61m, half.NewRent);

        var longer = LeaseIstatRules.Calculate(733.33m, 1.7m, 75m);

        Assert.Equal(1.275m, longer.AppliedPercent);
        Assert.Equal(9.35m, longer.Increase);
        Assert.Equal(742.68m, longer.NewRent);
    }

    [Theory]
    [InlineData(0, 75)]
    [InlineData(-1.5, 75)]
    [InlineData(3.2, 0)]
    [InlineData(3.2, 100.5)]
    [InlineData(3.215, 75)]
    [InlineData(100.5, 75)]
    public void Check_APercentageThatIsNotAboveZeroOrIsTooBigOrTooPrecise_CannotBeCalculated(double foi, double share)
    {
        Assert.Equal(LeaseIstatProblem.PercentInvalid, LeaseIstatRules.Check((decimal)foi, (decimal)share));
        Assert.Throws<ArgumentOutOfRangeException>(() => LeaseIstatRules.Calculate(650m, (decimal)foi, (decimal)share));
    }

    [Fact]
    public void Facts_TheCedolareSeccaSuspendsTheUpdate_AnOrdinaryLeaseCanAskForIt()
    {
        var ordinary = LeaseIstatRules.Facts(Lease(LeaseTaxRegime.Ordinario), Today);
        var cedolare = LeaseIstatRules.Facts(Lease(LeaseTaxRegime.CedolareSecca), Today);
        var unknown = LeaseIstatRules.Facts(Lease(regime: null), Today);

        Assert.True(ordinary.Available);
        Assert.Equal(650m, ordinary.CurrentRent);
        Assert.Equal(LeaseIstatRules.DefaultSharePercent, ordinary.DefaultSharePercent);
        Assert.False(cedolare.Available);
        Assert.True(unknown.Available);
    }

    [Fact]
    public void Facts_ALeaseThatIsNotInForce_EndedEarly_WithoutARent_OrOver_HasNothingToUpdate()
    {
        var draft = Lease();
        draft.Status = LeaseStatus.Draft;
        var ended = Lease();
        ended.TerminatedOn = Utc(2026, 8, 31);
        var noRent = Lease(rent: 0m);
        var over = Lease();
        over.EndDate = Utc(2026, 6, 30);

        Assert.False(LeaseIstatRules.Facts(draft, Today).Available);
        Assert.False(LeaseIstatRules.Facts(ended, Today).Available);
        Assert.False(LeaseIstatRules.Facts(noRent, Today).Available);
        Assert.False(LeaseIstatRules.Facts(over, Today).Available);
    }

    [Fact]
    public void Facts_TheUpdateIsNotRetroactive_ItStartsFromTodayAtTheEarliestAndEndsWithThePeriod()
    {
        var facts = LeaseIstatRules.Facts(Lease(), Today);

        Assert.Equal(Today, facts.MinEffectiveOn);
        Assert.Equal(new DateOnly(2029, 10, 31), facts.MaxEffectiveOn);

        var future = Lease();
        future.StartDate = Utc(2026, 12, 1);
        future.EndDate = Utc(2030, 11, 30);
        Assert.Equal(new DateOnly(2026, 12, 1), LeaseIstatRules.Facts(future, Today).MinEffectiveOn);
    }

    [Fact]
    public void CedolareNotice_IsForALeaseInForceUnderTheCedolareSecca_AndOnlyThen()
    {
        var cedolare = LeaseOperationRules.CedolareNotice(Lease(LeaseTaxRegime.CedolareSecca), Today);
        var ordinary = LeaseOperationRules.CedolareNotice(Lease(LeaseTaxRegime.Ordinario), Today);
        var ended = Lease(LeaseTaxRegime.CedolareSecca);
        ended.TerminatedOn = Utc(2026, 8, 31);

        Assert.True(cedolare.Available);
        Assert.Equal(new DateOnly(2025, 11, 1), cedolare.MinEffectiveOn);
        Assert.Equal(new DateOnly(2029, 10, 31), cedolare.MaxEffectiveOn);
        Assert.False(ordinary.Available);
        Assert.False(LeaseOperationRules.CedolareNotice(ended, Today).Available);
    }
}
