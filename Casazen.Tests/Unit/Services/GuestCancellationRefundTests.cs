using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// BK-02, BK-07 (PO 2026-10-08), DB-08h, DB-08a: the refund a guest gets when they cancel themselves, from the one function that
/// calculates it (<see cref="CancellationRefundPolicy.EvaluateGuestRefund"/>).
/// <list type="bullet">
/// <item>Refund = percent of the property's cancellation policy only — no CasaZen floor (FreeRefundDeadline ignored).</item>
/// <item>Refund base = BasePrice (lodging and cleaning); the tourist tax comes back in full besides (DB-D15 (1a)); never more than
/// what is still refundable.</item>
/// </list>
/// What the cancellation does with it (host 100%, guest the policy's refund) is in <c>BookingCancellationServiceTests</c>
/// and <c>GuestBookingCancellationServiceTests</c>.
/// </summary>
public class GuestCancellationRefundTests
{
    private static readonly DateTime CheckIn = new(2026, 11, 10, 0, 0, 0, DateTimeKind.Utc);

    private static CancellationPolicy Catalog(string slug) => CatalogPolicies.Get(slug);

    [Fact]
    public void NewToken_IsUrlSafe()
    {
        var token = GuestCancellationTokens.NewToken();

        Assert.False(string.IsNullOrWhiteSpace(token));
        Assert.DoesNotContain("+", token);
        Assert.DoesNotContain("/", token);
        Assert.DoesNotContain("=", token);
    }

    [Fact]
    public void TokenMatches_RoundTrip_ReturnsTrue()
    {
        var token = GuestCancellationTokens.NewToken();
        var hash = GuestCancellationTokens.HashToken(token);

        Assert.True(GuestCancellationTokens.TokenMatches(hash, token));
    }

    [Fact]
    public void TokenMatches_WrongToken_ReturnsFalse()
    {
        var hash = GuestCancellationTokens.HashToken("correct-token");

        Assert.False(GuestCancellationTokens.TokenMatches(hash, "wrong-token"));
    }

    [Fact]
    public void TokenMatches_NullHash_ReturnsFalse()
    {
        Assert.False(GuestCancellationTokens.TokenMatches(null, "any-token"));
    }

    [Theory]
    [InlineData(CatalogPolicies.Ampia, 48, 100, 400)]
    [InlineData(CatalogPolicies.Ampia, 24, 100, 400)]
    [InlineData(CatalogPolicies.Ampia, 23, 0, 0)]
    [InlineData(CatalogPolicies.Intermedia, 130, 100, 400)]
    [InlineData(CatalogPolicies.Intermedia, 120, 100, 400)]
    [InlineData(CatalogPolicies.Intermedia, 50, 50, 200)]
    [InlineData(CatalogPolicies.Intermedia, 0, 50, 200)]
    [InlineData(CatalogPolicies.Intermedia, -1, 0, 0)]
    [InlineData(CatalogPolicies.Contenuta, 400, 100, 400)]
    [InlineData(CatalogPolicies.Contenuta, 336, 100, 400)]
    [InlineData(CatalogPolicies.Contenuta, 200, 50, 200)]
    [InlineData(CatalogPolicies.Contenuta, 168, 50, 200)]
    [InlineData(CatalogPolicies.Contenuta, 167, 0, 0)]
    [InlineData(CatalogPolicies.Anticipata, 800, 100, 400)]
    [InlineData(CatalogPolicies.Anticipata, 400, 50, 200)]
    [InlineData(CatalogPolicies.Anticipata, 100, 0, 0)]
    [InlineData(CatalogPolicies.NonRimborsabile, 10, 0, 0)]
    [InlineData(CatalogPolicies.NonRimborsabile, 5000, 0, 0)]
    [InlineData(CatalogPolicies.NonRimborsabile, -1, 0, 0)]
    public void EvaluateGuestRefund_CatalogPolicy_RefundsPolicyPercentOfBasePriceAndTheWholeTouristTax(
        string slug, double hoursBeforeCheckIn, int expectedPercent, int expectedStayRefund)
    {
        var now = NowHoursBeforeCheckIn(hoursBeforeCheckIn);
        var withTax = new Booking { CheckInDate = CheckIn, BasePrice = 400m, TouristTax = 20m, TotalPrice = 420m };
        var withoutTax = new Booking { CheckInDate = CheckIn, BasePrice = 400m, TouristTax = 0m, TotalPrice = 400m };

        var paidWithTax = CancellationRefundPolicy.EvaluateGuestRefund(withTax, Catalog(slug), now, refundableAmount: 420m);
        var paidWithoutTax = CancellationRefundPolicy.EvaluateGuestRefund(withoutTax, Catalog(slug), now, refundableAmount: 400m);

        Assert.Equal(expectedPercent, paidWithTax.Percent);
        Assert.Equal(expectedStayRefund + 20, paidWithTax.Amount);
        Assert.Equal(expectedPercent, paidWithoutTax.Percent);
        Assert.Equal(expectedStayRefund, paidWithoutTax.Amount);
    }

    [Fact]
    public void EvaluateGuestRefund_FullRefundWindowEdge_IsInclusiveToTheMinute()
    {
        var booking = new Booking { CheckInDate = CheckIn, BasePrice = 400m };
        var atTheEdge = NowHoursBeforeCheckIn(24);

        Assert.Equal(100m, CancellationRefundPolicy.EvaluateGuestRefund(booking, Catalog(CatalogPolicies.Ampia), atTheEdge, 400m).Percent);
        Assert.Equal(0m, CancellationRefundPolicy.EvaluateGuestRefund(booking, Catalog(CatalogPolicies.Ampia), atTheEdge.AddMinutes(1), 400m).Percent);
    }

    [Fact]
    public void EvaluateGuestRefund_BaseIsBasePrice_NotTotalPrice_AndTheTaxComesBackInFull()
    {
        var booking = new Booking { CheckInDate = CheckIn, BasePrice = 400m, TouristTax = 50m, TotalPrice = 450m };

        var decision = CancellationRefundPolicy.EvaluateGuestRefund(booking, Catalog(CatalogPolicies.Intermedia), NowHoursBeforeCheckIn(50), 450m);

        Assert.Equal(250m, decision.Amount);
    }

    [Fact]
    public void EvaluateGuestRefund_DueMoreThanRefundable_IsCappedAtRefundable()
    {
        var booking = new Booking { CheckInDate = CheckIn, BasePrice = 400m };

        var decision = CancellationRefundPolicy.EvaluateGuestRefund(booking, Catalog(CatalogPolicies.Contenuta), NowHoursBeforeCheckIn(500), 300m);

        Assert.Equal(100m, decision.Percent);
        Assert.Equal(300m, decision.Amount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-10)]
    public void EvaluateGuestRefund_NothingRefundable_RefundsNothingButStillReportsThePercent(int refundable)
    {
        var booking = new Booking { CheckInDate = CheckIn, BasePrice = 400m };

        var decision = CancellationRefundPolicy.EvaluateGuestRefund(booking, Catalog(CatalogPolicies.Ampia), NowHoursBeforeCheckIn(100), refundable);

        Assert.Equal(100m, decision.Percent);
        Assert.Equal(0m, decision.Amount);
    }

    [Fact]
    public void EvaluateGuestRefund_NoPolicy_RefundsNothing()
    {
        var booking = new Booking { CheckInDate = CheckIn, BasePrice = 400m };

        var decision = CancellationRefundPolicy.EvaluateGuestRefund(booking, policy: null, NowHoursBeforeCheckIn(1000), 400m);

        Assert.Equal(0m, decision.Percent);
        Assert.Equal(0m, decision.Amount);
    }

    [Theory]
    [InlineData("333.33", "166.67")]
    [InlineData("100.01", "50.01")]
    [InlineData("0", "0")]
    public void EvaluateGuestRefund_Rounding_ToTwoDecimalsAwayFromZero(string basePrice, string expected)
    {
        var booking = new Booking { CheckInDate = CheckIn, BasePrice = decimal.Parse(basePrice, System.Globalization.CultureInfo.InvariantCulture) };

        var decision = CancellationRefundPolicy.EvaluateGuestRefund(booking, Catalog(CatalogPolicies.Intermedia), NowHoursBeforeCheckIn(50), 1000m);

        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), decision.Amount);
    }

    [Fact]
    public void EvaluateGuestRefund_FreeCancellationDeadlineStillOpen_DoesNotRaiseTheRefund()
    {
        var booking = new Booking { CheckInDate = CheckIn, FreeRefundDeadline = CheckIn, BasePrice = 400m };
        var now = NowHoursBeforeCheckIn(10);

        var guest = CancellationRefundPolicy.EvaluateGuestRefund(booking, Catalog(CatalogPolicies.Anticipata), now, 400m);
        var hostFloor = CancellationRefundPolicy.Evaluate(booking, Catalog(CatalogPolicies.Anticipata), now);

        Assert.Equal(0m, guest.Amount);
        Assert.Equal(CancellationRefundRule.FreeCancellationDeadline, hostFloor.Rule);
        Assert.Equal(100m, hostFloor.Percent);
    }

    [Fact]
    public void EvaluateGuestRefund_HostMarkedStayNonRefundable_RefundsNothing()
    {
        var property = new Property
        {
            CancellationPolicy = Catalog(CatalogPolicies.Ampia),
            CancellationRefundType = HostCancellationRefundType.NonRefundable,
        };
        var booking = new Booking { CheckInDate = CheckIn, BasePrice = 400m };

        var policy = CancellationRefundPolicy.EffectivePolicy(property, nights: 3);
        var decision = CancellationRefundPolicy.EvaluateGuestRefund(booking, policy, NowHoursBeforeCheckIn(1000), 400m);

        Assert.Equal(0m, decision.Amount);
    }

    [Fact]
    public void EvaluateGuestRefund_HostOverrideOfTheWindow_UsesTheEffectivePolicy()
    {
        var property = new Property
        {
            CancellationPolicy = Catalog(CatalogPolicies.Ampia),
            CancellationFullRefundHours = 240,
        };
        var booking = new Booking { CheckInDate = CheckIn, BasePrice = 400m };
        var policy = CancellationRefundPolicy.EffectivePolicy(property, nights: 3);

        Assert.Equal(400m, CancellationRefundPolicy.EvaluateGuestRefund(booking, policy, NowHoursBeforeCheckIn(241), 400m).Amount);
        Assert.Equal(0m, CancellationRefundPolicy.EvaluateGuestRefund(booking, policy, NowHoursBeforeCheckIn(100), 400m).Amount);
    }

    [Fact]
    public void EvaluateGuestRefund_Percent_MatchesTheFloorOfTheHostRule()
    {
        var booking = new Booking { CheckInDate = CheckIn, BasePrice = 400m };
        foreach (var hours in new double[] { 800, 400, 200, 100, 10, -5 })
        {
            var now = NowHoursBeforeCheckIn(hours);
            foreach (var slug in new[] { CatalogPolicies.Ampia, CatalogPolicies.Intermedia, CatalogPolicies.Contenuta, CatalogPolicies.Anticipata, CatalogPolicies.NonRimborsabile })
            {
                var policy = Catalog(slug);
                Assert.Equal(
                    CancellationRefundPolicy.Evaluate(booking, policy, now).Percent,
                    CancellationRefundPolicy.EvaluateGuestRefund(booking, policy, now, 400m).Percent);
            }
        }
    }

    [Theory]
    [InlineData(150, 100)]
    [InlineData(-5, 0)]
    public void EvaluateGuestRefund_PartialPercentOutOfRange_IsClampedToZeroAndOneHundred(int configured, int expectedPercent)
    {
        var policy = new CancellationPolicy { FullRefundHours = 100, PartialRefundHours = 0, PartialRefundPercent = configured };
        var booking = new Booking { CheckInDate = CheckIn, BasePrice = 400m };

        var decision = CancellationRefundPolicy.EvaluateGuestRefund(booking, policy, NowHoursBeforeCheckIn(50), 1000m);

        Assert.Equal(expectedPercent, decision.Percent);
        Assert.Equal(expectedPercent * 4m, decision.Amount);
    }

    [Fact]
    public void EvaluateGuestRefund_ForTheBooking_ResolvesThePolicyOfTheStayWithTheHostsOverrides()
    {
        var property = new Property { CancellationPolicy = Catalog(CatalogPolicies.Ampia), CancellationFullRefundHours = 240 };
        var booking = new Booking { Property = property, CheckInDate = CheckIn, CheckOutDate = CheckIn.AddDays(3), BasePrice = 400m };

        Assert.Equal(0m, CancellationRefundPolicy.EvaluateGuestRefund(booking, NowHoursBeforeCheckIn(100), 400m).Amount);
        Assert.Equal(400m, CancellationRefundPolicy.EvaluateGuestRefund(booking, NowHoursBeforeCheckIn(241), 400m).Amount);
    }

    [Fact]
    public void EvaluateGuestRefund_ForALongStay_KeepsTheCatalogPolicyWhateverTheHostChose()
    {
        var property = new Property
        {
            CancellationPolicy = Catalog(CatalogPolicies.Ampia),
            CancellationRefundType = HostCancellationRefundType.NonRefundable,
        };
        var booking = new Booking { Property = property, CheckInDate = CheckIn, CheckOutDate = CheckIn.AddDays(28), BasePrice = 4000m };

        Assert.Equal(4000m, CancellationRefundPolicy.EvaluateGuestRefund(booking, NowHoursBeforeCheckIn(100), 4000m).Amount);
    }

    [Fact]
    public void EvaluateGuestRefund_ForABookingWithoutPropertyPolicy_RefundsNothing()
    {
        var withoutPolicy = new Booking { Property = new Property(), CheckInDate = CheckIn, CheckOutDate = CheckIn.AddDays(3), BasePrice = 400m };
        var withoutProperty = new Booking { CheckInDate = CheckIn, CheckOutDate = CheckIn.AddDays(3), BasePrice = 400m };

        Assert.Equal(0m, CancellationRefundPolicy.EvaluateGuestRefund(withoutPolicy, NowHoursBeforeCheckIn(1000), 400m).Amount);
        Assert.Equal(0m, CancellationRefundPolicy.EvaluateGuestRefund(withoutProperty, NowHoursBeforeCheckIn(1000), 400m).Amount);
    }

    [Fact]
    public void EvaluateGuestRefund_NullBooking_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            CancellationRefundPolicy.EvaluateGuestRefund(null!, Catalog(CatalogPolicies.Ampia), DateTimeOffset.UtcNow, 100m));
        Assert.Throws<ArgumentNullException>(() =>
            CancellationRefundPolicy.EvaluateGuestRefund(null!, DateTimeOffset.UtcNow, 100m));
    }

    private static DateTimeOffset NowHoursBeforeCheckIn(double hours) =>
        new(RomeCalendar.StartOfDayUtc(CheckIn).AddHours(-hours), TimeSpan.Zero);
}
