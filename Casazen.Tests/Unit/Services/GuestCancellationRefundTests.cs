using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// BK-02, BK-07 (PO 2026-10-08), DB-08h: the refund a guest gets when they cancel themselves, from the one function that
/// calculates it (<see cref="CancellationRefundPolicy.EvaluateGuestRefund"/>).
/// <list type="bullet">
/// <item>Refund = percent of the property's cancellation policy only — no CasaZen floor (FreeRefundDeadline ignored).</item>
/// <item>Refund base = BasePrice (tourist tax excluded), never more than what is still refundable.</item>
/// </list>
/// What the cancellation does with it (host 100%, guest the policy's refund) is in <c>BookingCancellationServiceTests</c>
/// and <c>GuestBookingCancellationServiceTests</c>.
/// </summary>
public class GuestCancellationRefundTests
{
    private static readonly DateTime CheckIn = new(2026, 11, 10, 0, 0, 0, DateTimeKind.Utc);

    private static CancellationPolicy Catalog(string slug) => CatalogPolicies.Get(slug);

    // ── Token helpers ──────────────────────────────────────────────────────────

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

    // ── The five policies of the catalog: percent of BasePrice, tourist tax paid or not ──
    // Base 400, tourist tax 20: the guest paid 420 (or 400 without the tax). The tax stays out of the refund even at 100%
    // (PO 2026-10-08; whether it should be refunded is the open decision DB-D15).

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
    // DB-D2 (open): with FullRefundHours = 0 the formula gives 100% until the check-in day starts. The row pins today's
    // behaviour so that the decision, when it is taken (DB-08a), has to change this test with the catalog.
    [InlineData(CatalogPolicies.NonRimborsabile, 10, 100, 400)]
    [InlineData(CatalogPolicies.NonRimborsabile, -1, 0, 0)]
    public void EvaluateGuestRefund_CatalogPolicy_RefundsPolicyPercentOfBasePriceWhetherOrNotTaxWasPaid(
        string slug, double hoursBeforeCheckIn, int expectedPercent, int expectedRefund)
    {
        var now = NowHoursBeforeCheckIn(hoursBeforeCheckIn);
        var withTax = new Booking { CheckInDate = CheckIn, BasePrice = 400m, TouristTax = 20m, TotalPrice = 420m };
        var withoutTax = new Booking { CheckInDate = CheckIn, BasePrice = 400m, TouristTax = 0m, TotalPrice = 400m };

        var paidWithTax = CancellationRefundPolicy.EvaluateGuestRefund(withTax, Catalog(slug), now, refundableAmount: 420m);
        var paidWithoutTax = CancellationRefundPolicy.EvaluateGuestRefund(withoutTax, Catalog(slug), now, refundableAmount: 400m);

        Assert.Equal(expectedPercent, paidWithTax.Percent);
        Assert.Equal(expectedRefund, paidWithTax.Amount);
        Assert.Equal(expectedPercent, paidWithoutTax.Percent);
        Assert.Equal(expectedRefund, paidWithoutTax.Amount);
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
    public void EvaluateGuestRefund_BaseIsBasePrice_NotTotalPrice()
    {
        // 50% policy, BasePrice 400, tourist tax 50, TotalPrice 450: 50% of 400, not of 450.
        var booking = new Booking { CheckInDate = CheckIn, BasePrice = 400m, TouristTax = 50m, TotalPrice = 450m };

        var decision = CancellationRefundPolicy.EvaluateGuestRefund(booking, Catalog(CatalogPolicies.Intermedia), NowHoursBeforeCheckIn(50), 450m);

        Assert.Equal(200m, decision.Amount);
    }

    [Fact]
    public void EvaluateGuestRefund_DueMoreThanRefundable_IsCappedAtRefundable()
    {
        // Part of the payment is already refunded: only 300 of the 400 base can still go back.
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
        // Pay-at-deadline or pay-on-site: no money was collected through Stripe, so there is nothing to give back.
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
    [InlineData("333.33", "166.67")] // 166.665 rounds away from zero
    [InlineData("100.01", "50.01")]  // 50.005
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
        // The guest has "free cancellation until check-in" (FreeRefundDeadline) and a strict policy that already refunds
        // nothing: for the guest's own cancellation only the policy counts (PO 2026-10-08), unlike the host's floor.
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
        // The host moved the full-refund window of "Ampia" from 24 h to 10 days before check-in.
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
        // One reading of the policy: the percent the guest gets is the one the host's floor reports for the same moment.
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
        Assert.Equal(expectedPercent * 4m, decision.Amount); // percent of the 400 base price
    }

    [Fact]
    public void EvaluateGuestRefund_ForTheBooking_ResolvesThePolicyOfTheStayWithTheHostsOverrides()
    {
        // "Ampia" refunds in full until 24 h before check-in; the host moved that to 10 days.
        var property = new Property { CancellationPolicy = Catalog(CatalogPolicies.Ampia), CancellationFullRefundHours = 240 };
        var booking = new Booking { Property = property, CheckInDate = CheckIn, CheckOutDate = CheckIn.AddDays(3), BasePrice = 400m };

        Assert.Equal(0m, CancellationRefundPolicy.EvaluateGuestRefund(booking, NowHoursBeforeCheckIn(100), 400m).Amount);
        Assert.Equal(400m, CancellationRefundPolicy.EvaluateGuestRefund(booking, NowHoursBeforeCheckIn(241), 400m).Amount);
    }

    [Fact]
    public void EvaluateGuestRefund_ForALongStay_KeepsTheCatalogPolicyWhateverTheHostChose()
    {
        // 28 nights or more: the host's overrides are ignored (PO 2026-10-08), here "non-refundable".
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

    /// <summary>The instant that is <paramref name="hours"/> before the start of the check-in day in Europe/Rome.</summary>
    private static DateTimeOffset NowHoursBeforeCheckIn(double hours) =>
        new(RomeCalendar.StartOfDayUtc(CheckIn).AddHours(-hours), TimeSpan.Zero);
}
