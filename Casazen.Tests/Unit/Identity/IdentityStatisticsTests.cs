using Casazen.Core.Identity.Statistics;
using Xunit;

namespace Casazen.Tests.Unit.Identity;

/// <summary>
/// E74/F01/US01/T01: the statistics of the release criteria (<see cref="IdentityStatistics"/>) against the figures of the specification and against values
/// computed independently of the implementation. Where a table holds numbers, they come from this recipe, run once outside the code under test with the
/// Python standard library only (exact integers and 70-digit decimals, no library of statistics):
/// the Wilson interval is <c>(p + z^2/2n -+ z * sqrt(p(1-p)/n + z^2/4n^2)) / (1 + z^2/n)</c> with z = <c>statistics.NormalDist().inv_cdf(1 - (1 - c) / 2)</c>;
/// the Clopper-Pearson upper bound U is the root of <c>sum_{i=0..k} C(n,i) U^i (1-U)^(n-i) = 1 - c</c>, found by bisection on the exact sum
/// (equivalently <c>U = BetaInverse(c; k + 1, n - k)</c>); the percentiles are the nearest-rank definition of the textbook.
/// </summary>
public class IdentityStatisticsTests
{
    private const double Tight = 1e-12;

    [Fact]
    public void Wilson_TheSpecificationCase_425Of500_HasALowerBoundAboveEightyPercent()
    {
        var (low, high) = IdentityStatistics.Wilson(425, 500);

        Assert.InRange(low, 0.8160 - 1e-4, 0.8160 + 1e-4);
        Assert.InRange(high, 0.8786 - 1e-4, 0.8786 + 1e-4);
        Assert.True(low > 0.80, "the criterion of the specification: the lower Wilson bound of 425 of 500 is above 80%");
    }

    // (successes, n, low, high) at 95%.
    [Theory]
    [InlineData(425, 500, 0.8160389193683016, 0.8786240423086118)]
    [InlineData(0, 10, 0.0, 0.27753279986288915)]
    [InlineData(10, 10, 0.7224672001371109, 1.0)]
    [InlineData(1, 10, 0.017876213095072913, 0.40415002679523837)]
    [InlineData(5, 100, 0.021543679154367976, 0.11175046923191911)]
    [InlineData(50, 100, 0.4038315303659957, 0.5961684696340044)]
    [InlineData(0, 300, 0.0, 0.01264297122454603)]
    [InlineData(1, 300, 0.0005886577218511582, 0.01863669369453123)]
    [InlineData(3, 3, 0.4385029682449546, 1.0)]
    [InlineData(0, 1, 0.0, 0.7934506856227626)]
    [InlineData(1, 1, 0.2065493143772375, 1.0)]
    [InlineData(950, 1000, 0.9346861797557492, 0.9618697376072511)]
    public void Wilson_AtNinetyFivePercent_MatchesTheIndependentReferenceValues(int successes, int n, double low, double high)
    {
        var actual = IdentityStatistics.Wilson(successes, n);

        Assert.InRange(actual.Low, low - Tight, low + Tight);
        Assert.InRange(actual.High, high - Tight, high + Tight);
    }

    // (confidence, low, high) for 425 of 500.
    [Theory]
    [InlineData(0.99, 0.8042976539556046, 0.8865351377611248)]
    [InlineData(0.90, 0.821853243835376, 0.8743793809237347)]
    public void Wilson_AtOtherConfidences_MatchesTheIndependentReferenceValues(double confidence, double low, double high)
    {
        var actual = IdentityStatistics.Wilson(425, 500, confidence);

        Assert.InRange(actual.Low, low - Tight, low + Tight);
        Assert.InRange(actual.High, high - Tight, high + Tight);
    }

    [Fact]
    public void Wilson_NoTrials_IsTheWholeInterval()
    {
        Assert.Equal((0.0, 1.0), IdentityStatistics.Wilson(0, 0));
        Assert.Equal((0.0, 1.0), IdentityStatistics.Wilson(0, 0, 0.99));
    }

    [Fact]
    public void Wilson_NoSuccessesAndAllSuccesses_PinTheEndsExactly()
    {
        Assert.Equal(0.0, IdentityStatistics.Wilson(0, 50).Low);
        Assert.Equal(1.0, IdentityStatistics.Wilson(50, 50).High);
    }

    [Fact]
    public void Wilson_IsSymmetric_ContainsTheObservedRate_AndWidensWithTheConfidence()
    {
        foreach (var n in new[] { 1, 2, 7, 50, 333 })
        {
            for (var successes = 0; successes <= n; successes++)
            {
                var interval = IdentityStatistics.Wilson(successes, n);
                var mirrored = IdentityStatistics.Wilson(n - successes, n);
                var wider = IdentityStatistics.Wilson(successes, n, 0.99);
                var rate = (double)successes / n;

                Assert.InRange(interval.Low, 0.0, rate);
                Assert.InRange(interval.High, rate, 1.0);
                Assert.InRange(interval.Low, 1.0 - mirrored.High - Tight, 1.0 - mirrored.High + Tight);
                Assert.InRange(interval.High, 1.0 - mirrored.Low - Tight, 1.0 - mirrored.Low + Tight);
                Assert.True(wider.Low <= interval.Low && wider.High >= interval.High);
            }
        }
    }

    // (confidence, z): the two-sided standard normal quantile, statistics.NormalDist().inv_cdf(1 - (1 - c) / 2).
    [Theory]
    [InlineData(0.5, 0.6744897501960817)]
    [InlineData(0.8, 1.2815515655446008)]
    [InlineData(0.9, 1.6448536269514715)]
    [InlineData(0.95, 1.959963984540054)]
    [InlineData(0.99, 2.5758293035489)]
    [InlineData(0.999, 3.2905267314919255)]
    [InlineData(0.9999, 3.89059188641312)]
    public void TwoSidedZ_MatchesTheNormalQuantiles(double confidence, double z)
    {
        Assert.InRange(IdentityStatistics.TwoSidedZ(confidence), z - Tight, z + Tight);
    }

    // (z, P(Z > z)) = 0.5 * math.erfc(z / sqrt(2)).
    [Theory]
    [InlineData(-1.0, 0.8413447460685428)]
    [InlineData(0.0, 0.5)]
    [InlineData(0.5, 0.30853753872598694)]
    [InlineData(1.0, 0.15865525393145707)]
    [InlineData(1.959963984540054, 0.025)]
    [InlineData(3.0, 0.0013498980316300959)]
    [InlineData(5.0, 2.8665157187919455e-07)]
    public void NormalUpperTail_MatchesTheComplementaryErrorFunction(double z, double tail)
    {
        Assert.InRange(IdentityStatistics.NormalUpperTail(z), tail * (1.0 - 1e-12) - 1e-15, tail * (1.0 + 1e-12) + 1e-15);
    }

    // (x, math.lgamma(x)).
    [Theory]
    [InlineData(0.5, 0.5723649429247004)]
    [InlineData(1.0, 0.0)]
    [InlineData(2.0, 0.0)]
    [InlineData(10.0, 12.801827480081467)]
    [InlineData(101.0, 363.73937555556347)]
    [InlineData(30001.0, 279274.6532369701)]
    public void LogGamma_MatchesTheKnownValues(double x, double expected)
    {
        Assert.InRange(IdentityStatistics.LogGamma(x), expected - 1e-9, expected + 1e-9);
    }

    [Fact]
    public void ClopperPearsonUpper_NoErrors_IsTheClosedForm_OfTheSpecificationFigures()
    {
        // 1 - 0.05^(1/n): the spec's "about 30,000 trials for 0.01%" and "about 300 trials per class for 1%".
        var impostors = IdentityStatistics.ClopperPearsonUpper(0, 29_956);
        var attacks = IdentityStatistics.ClopperPearsonUpper(0, 300);

        Assert.InRange(impostors, 0.0001 - 1e-6, 0.0001 + 1e-6);
        Assert.InRange(attacks, 0.00994 - 1e-5, 0.00994 + 1e-5);
        Assert.InRange(impostors, 9.999941531978724e-05 - 1e-15, 9.999941531978724e-05 + 1e-15);
        Assert.InRange(attacks, 0.009936081944457713 - 1e-13, 0.009936081944457713 + 1e-13);
    }

    // (errors, n, upper bound at 95%).
    [Theory]
    [InlineData(1, 10, 0.39416330243650477)]
    [InlineData(2, 50, 0.12061415542204414)]
    [InlineData(3, 100, 0.07571079374983006)]
    [InlineData(1, 300, 0.015714554891583955)]
    [InlineData(5, 1000, 0.010484076911415653)]
    [InlineData(10, 300, 0.055884740514558565)]
    [InlineData(9, 10, 0.9948838031081763)]
    [InlineData(150, 300, 0.5489912452912065)]
    [InlineData(1, 30000, 0.00015811895074140285)]
    [InlineData(5, 30000, 0.00035040229845741317)]
    [InlineData(0, 1, 0.95)]
    [InlineData(0, 10, 0.2588655508930523)]
    public void ClopperPearsonUpper_AtNinetyFivePercent_MatchesTheIndependentReferenceValues(int errors, int n, double expected)
    {
        var actual = IdentityStatistics.ClopperPearsonUpper(errors, n);

        Assert.InRange(actual, expected * (1.0 - 1e-9), expected * (1.0 + 1e-9));
    }

    // 3 errors in 100 trials, one-sided, at other confidences.
    [Theory]
    [InlineData(0.99, 0.09697104526534447)]
    [InlineData(0.90, 0.06558575150289181)]
    public void ClopperPearsonUpper_AtOtherConfidences_MatchesTheIndependentReferenceValues(double confidence, double expected)
    {
        Assert.InRange(IdentityStatistics.ClopperPearsonUpper(3, 100, confidence), expected * (1.0 - 1e-9), expected * (1.0 + 1e-9));
    }

    [Theory]
    [InlineData(1, 10, 0.95)]
    [InlineData(2, 50, 0.95)]
    [InlineData(3, 100, 0.99)]
    [InlineData(10, 300, 0.90)]
    [InlineData(150, 300, 0.95)]
    [InlineData(9, 10, 0.95)]
    [InlineData(5, 1000, 0.95)]
    public void ClopperPearsonUpper_IsWhereTheChanceOfThatFewErrorsFallsToOneMinusConfidence(int errors, int n, double confidence)
    {
        var bound = IdentityStatistics.ClopperPearsonUpper(errors, n, confidence);

        // A plain sum of the binomial terms, written here and not shared with the implementation.
        Assert.InRange(IndependentBinomialCdf(errors, n, bound), 1.0 - confidence - 1e-9, 1.0 - confidence + 1e-9);
    }

    [Theory]
    [InlineData(5, 30000, 0.00035)]
    [InlineData(3, 100, 0.0757)]
    [InlineData(20, 100, 0.1)]
    [InlineData(20, 100, 0.35)]
    [InlineData(1, 10, 0.9)]
    [InlineData(0, 300, 0.01)]
    [InlineData(299, 300, 0.99)]
    public void BinomialCdf_MatchesAnIndependentSum(int k, int n, double p)
    {
        var expected = IndependentBinomialCdf(k, n, p);

        Assert.InRange(IdentityStatistics.BinomialCdf(k, n, p), expected * (1.0 - 1e-9) - 1e-300, expected * (1.0 + 1e-9) + 1e-300);
    }

    [Fact]
    public void ClopperPearsonUpper_NoTrialsOrEveryTrialAnError_IsOne()
    {
        Assert.Equal(1.0, IdentityStatistics.ClopperPearsonUpper(0, 0));
        Assert.Equal(1.0, IdentityStatistics.ClopperPearsonUpper(7, 7));
        Assert.Equal(1.0, IdentityStatistics.ClopperPearsonUpper(1, 1, 0.99));
    }

    [Fact]
    public void ClopperPearsonUpper_GrowsWithTheErrors_ShrinksWithTheTrials_AndGrowsWithTheConfidence()
    {
        var previous = 0.0;
        for (var errors = 0; errors <= 20; errors++)
        {
            var bound = IdentityStatistics.ClopperPearsonUpper(errors, 200);
            Assert.True(bound > previous, "more errors in the same trials give a higher bound");
            previous = bound;
        }

        previous = 1.0;
        foreach (var n in new[] { 50, 100, 400, 1600, 6400 })
        {
            var bound = IdentityStatistics.ClopperPearsonUpper(2, n);
            Assert.True(bound < previous, "the same errors in more trials give a lower bound");
            previous = bound;
        }

        Assert.True(IdentityStatistics.ClopperPearsonUpper(3, 100, 0.99) > IdentityStatistics.ClopperPearsonUpper(3, 100, 0.90));
    }

    [Fact]
    public void RuleOfThree_IsThreeOverN_AndNeverAboveOne()
    {
        Assert.Equal(0.01, IdentityStatistics.RuleOfThree(300));
        Assert.Equal(0.0001, IdentityStatistics.RuleOfThree(30_000));
        Assert.Equal(0.75, IdentityStatistics.RuleOfThree(4));
        Assert.Equal(1.0, IdentityStatistics.RuleOfThree(3));
        Assert.Equal(1.0, IdentityStatistics.RuleOfThree(1));
        Assert.Equal(1.0, IdentityStatistics.RuleOfThree(0));
    }

    [Fact]
    public void RuleOfThree_ApproximatesTheExactBoundAfterNoErrors_WithinOnePercent()
    {
        foreach (var n in new[] { 300, 3_000, 30_000, 300_000 })
        {
            var exact = IdentityStatistics.ClopperPearsonUpper(0, n);

            Assert.InRange(IdentityStatistics.RuleOfThree(n) / exact, 0.99, 1.01);
        }
    }

    [Fact]
    public void Fmr_CountsTheImpostorScoresAtOrAboveTheThreshold()
    {
        var fmr = IdentityStatistics.Fmr([0.10, 0.20, 0.50, 0.50, 0.90], 0.50);

        Assert.Equal(new CountedRate(3, 5), fmr);
        Assert.Equal(0.6, fmr.Rate);
        Assert.Equal(new CountedRate(0, 5), IdentityStatistics.Fmr([0.10, 0.20, 0.50, 0.50, 0.90], 0.91));
        Assert.Equal(new CountedRate(5, 5), IdentityStatistics.Fmr([0.10, 0.20, 0.50, 0.50, 0.90], 0.10));
    }

    [Fact]
    public void Fnmr_CountsTheGenuineScoresBelowTheThreshold()
    {
        var fnmr = IdentityStatistics.Fnmr([0.40, 0.50, 0.60, 0.90], 0.50);

        Assert.Equal(new CountedRate(1, 4), fnmr);
        Assert.Equal(0.25, fnmr.Rate);
        Assert.Equal(new CountedRate(0, 4), IdentityStatistics.Fnmr([0.40, 0.50, 0.60, 0.90], 0.40));
        Assert.Equal(new CountedRate(4, 4), IdentityStatistics.Fnmr([0.40, 0.50, 0.60, 0.90], 0.91));
    }

    [Fact]
    public void FmrAndFnmr_AtTheSameThreshold_ReadTheSameScoresTheOtherWay()
    {
        // One threshold: an impostor at the threshold is a false match, a genuine one at the threshold is a match, never both errors.
        double[] scores = [0.30, 0.55, 0.55, 0.80];

        Assert.Equal(3, IdentityStatistics.Fmr(scores, 0.55).Count);
        Assert.Equal(1, IdentityStatistics.Fnmr(scores, 0.55).Count);
    }

    [Fact]
    public void FmrFnmrBpcer_NoMeasurement_HasNoRate()
    {
        Assert.Equal(new CountedRate(0, 0), IdentityStatistics.Fmr([], 0.5));
        Assert.Null(IdentityStatistics.Fmr([], 0.5).Rate);
        Assert.Null(IdentityStatistics.Fnmr([], 0.5).Rate);
        Assert.Null(IdentityStatistics.Bpcer([]).Rate);
    }

    [Fact]
    public void Apcer_GivesEveryClass_AndTheMaximumOverTheClasses()
    {
        var outcomes = new Dictionary<string, List<bool>>
        {
            ["screen_replay"] = [false, false, false, false, false, false, false, false, false, false],
            ["video_replay"] = [true, true, false],
            ["photo_print"] = [false, false, false, true],
        };

        var apcer = IdentityStatistics.Apcer(outcomes);

        Assert.Equal(
            [
                new NamedRate("photo_print", new CountedRate(1, 4)),
                new NamedRate("screen_replay", new CountedRate(0, 10)),
                new NamedRate("video_replay", new CountedRate(2, 3)),
            ],
            apcer.ByClass);
        Assert.Equal(new NamedRate("video_replay", new CountedRate(2, 3)), apcer.Worst);
        Assert.Equal(2.0 / 3.0, apcer.Max);
    }

    [Fact]
    public void Apcer_AClassWithoutOutcomes_IsListedWithoutARate_AndNoClassHasNoWorst()
    {
        var some = IdentityStatistics.Apcer(new Dictionary<string, bool[]> { ["mask"] = [], ["synthetic"] = [false, false] });
        var none = IdentityStatistics.Apcer(new Dictionary<string, bool[]> { ["mask"] = [] });
        var nothing = IdentityStatistics.Apcer(new Dictionary<string, bool[]>());

        Assert.Equal(new NamedRate("mask", new CountedRate(0, 0)), some.ByClass[0]);
        Assert.Null(some.ByClass[0].Rate.Rate);
        Assert.Equal("synthetic", some.Worst?.Name);
        Assert.Equal(0.0, some.Max);
        Assert.Null(none.Worst);
        Assert.Null(none.Max);
        Assert.Empty(nothing.ByClass);
        Assert.Null(nothing.Worst);
    }

    [Fact]
    public void Apcer_ATieForTheWorst_GoesToTheFirstClassInOrdinalOrder()
    {
        var apcer = IdentityStatistics.Apcer(new Dictionary<string, IReadOnlyList<bool>>
        {
            ["video_replay"] = new[] { true, false },
            ["mask"] = new[] { false, true },
            ["synthetic"] = new[] { false, false },
        });

        Assert.Equal("mask", apcer.Worst?.Name);
        Assert.Equal(0.5, apcer.Max);
    }

    [Fact]
    public void Bpcer_CountsTheBonaFidePresentationsThatWereNotAccepted()
    {
        var bpcer = IdentityStatistics.Bpcer([true, true, false, true]);

        Assert.Equal(new CountedRate(1, 4), bpcer);
        Assert.Equal(0.25, bpcer.Rate);
        Assert.Equal(new CountedRate(0, 3), IdentityStatistics.Bpcer([true, true, true]));
    }

    [Fact]
    public void FieldExactness_CountsTheExactMatchesPerField_AfterTheNormalizationOfTheField()
    {
        // The normalization is the caller's, per field: names compare without case, spaces and accents, the number without separators.
        static string? Normalize(string field, string? value)
        {
            if (value is null)
                return null;

            return field == "documentNumber" ? value.Replace("-", string.Empty, StringComparison.Ordinal).ToUpperInvariant() : value.Trim().ToUpperInvariant();
        }

        (string Field, string? Expected, string? Read)[] pairs =
        [
            ("surname", "Rossi", " ROSSI "),
            ("surname", "Bianchi", "Bianchi"),
            ("surname", "Verdi", "Verde"),
            ("documentNumber", "CA-00000-AA", "ca00000aa"),
            ("documentNumber", "CA-00000-BB", "CA00000BB"),
            ("birthDate", "1980-01-01", "1980-01-02"),
        ];

        var exactness = IdentityStatistics.FieldExactness(pairs, Normalize);

        Assert.Equal(
            [
                new NamedRate("birthDate", new CountedRate(0, 1)),
                new NamedRate("documentNumber", new CountedRate(2, 2)),
                new NamedRate("surname", new CountedRate(2, 3)),
            ],
            exactness);
    }

    [Fact]
    public void FieldExactness_AMissingValueAndAnEmptyOneAreTheSame_ButOneSidedAbsenceIsAMiss()
    {
        (string Field, string? Expected, string? Read)[] pairs =
        [
            ("middleName", null, string.Empty),
            ("middleName", string.Empty, null),
            ("middleName", null, null),
            ("middleName", "Maria", null),
            ("middleName", null, "Maria"),
        ];

        var exactness = Assert.Single(IdentityStatistics.FieldExactness(pairs, (_, value) => value));

        Assert.Equal(new NamedRate("middleName", new CountedRate(3, 5)), exactness);
    }

    [Fact]
    public void FieldExactness_ReturnsNamesAndCountsOnly_NeverAValueOfAPair()
    {
        (string Field, string? Expected, string? Read)[] pairs =
        [
            ("surname", "GROUND-TRUTH-SENTINEL", "READ-SENTINEL"),
            ("documentNumber", "NUMBER-SENTINEL", "NUMBER-SENTINEL"),
        ];

        var exactness = IdentityStatistics.FieldExactness(pairs, (_, value) => value);
        var printed = string.Join(" ", exactness.Select(entry => entry.ToString()));

        Assert.DoesNotContain("SENTINEL", printed, StringComparison.Ordinal);
        Assert.Equal(["documentNumber", "surname"], exactness.Select(entry => entry.Name));
    }

    [Fact]
    public void FieldExactness_NoPairs_GivesNoField()
    {
        Assert.Empty(IdentityStatistics.FieldExactness([], (_, value) => value));
    }

    [Fact]
    public void FieldExactness_TheLowerWilsonBoundIsWhatTheCriterionReads()
    {
        // 295 of 300 exact is 98.3% observed, but its lower Wilson bound is under the 98% of the criterion; 299 of 300 passes it.
        var fiveMisses = ExactnessOfThreeHundredWithMisses(5);
        var oneMiss = ExactnessOfThreeHundredWithMisses(1);

        Assert.Equal(new CountedRate(295, 300), fiveMisses.Rate);
        Assert.True(IdentityStatistics.Wilson(fiveMisses.Rate.Count, fiveMisses.Rate.Total).Low < 0.98);
        Assert.True(IdentityStatistics.Wilson(oneMiss.Rate.Count, oneMiss.Rate.Total).Low > 0.98);
    }

    [Fact]
    public void Percentile_NearestRank_OnTheTextbookArray()
    {
        // The textbook example of the nearest-rank method: {15, 20, 35, 40, 50}.
        double[] values = [40, 15, 50, 35, 20];

        Assert.Equal(15.0, IdentityStatistics.Percentile(values, 5));
        Assert.Equal(20.0, IdentityStatistics.Percentile(values, 30));
        Assert.Equal(20.0, IdentityStatistics.Percentile(values, 40));
        Assert.Equal(35.0, IdentityStatistics.Percentile(values, 50));
        Assert.Equal(50.0, IdentityStatistics.Percentile(values, 100));
        Assert.Equal(15.0, IdentityStatistics.Percentile(values, 0));
    }

    [Fact]
    public void Percentile_EveryRank_MatchesIntegerArithmetic()
    {
        // The values 1..n, in descending order: the percentile is its own rank, ceil(percent * n / 100) at least 1, worked out here in integers.
        foreach (var n in Enumerable.Range(1, 200))
        {
            var values = Enumerable.Range(1, n).Reverse().Select(i => (double)i).ToList();
            foreach (var percent in new[] { 0, 1, 5, 10, 25, 33, 50, 66, 75, 90, 95, 99, 100 })
            {
                var rank = Math.Max(1, (percent * n + 99) / 100);

                Assert.Equal((double)rank, IdentityStatistics.Percentile(values, percent));
            }
        }
    }

    [Fact]
    public void Percentile_P95OfTwentyValues_IsTheNineteenth_NotTheTwentieth()
    {
        // 0.95 * 20 is 19.000000000000004 in floating point: the rank must still be 19.
        var values = Enumerable.Range(1, 20).Select(i => (double)i).ToList();

        Assert.Equal(19.0, IdentityStatistics.Percentile(values, 95));
        Assert.Equal(999.0, IdentityStatistics.Percentile(Enumerable.Range(1, 1000).Select(i => (double)i).ToList(), 99.9));
    }

    [Fact]
    public void Percentile_LargeUnsortedInputWithDuplicates_IsTheNearestRank_AndTheInputIsNotChanged()
    {
        var values = Enumerable.Range(0, 10_000).Select(i => (double)((i * 7919) % 1000) - 500.5).ToList();
        var before = values.ToArray();
        var sorted = values.Order().ToArray();

        Assert.Equal(sorted[9_500 - 1], IdentityStatistics.Percentile(values, 95));
        Assert.Equal(sorted[5_000 - 1], IdentityStatistics.Percentile(values, 50));
        Assert.Equal(sorted[0], IdentityStatistics.Percentile(values, 0));
        Assert.Equal(sorted[^1], IdentityStatistics.Percentile(values, 100));
        Assert.Equal(before, values);
    }

    [Fact]
    public void Percentile_OneValueOrNone_IsThatValueOrNull()
    {
        Assert.Equal(7.5, IdentityStatistics.Percentile([7.5], 95));
        Assert.Equal(7.5, IdentityStatistics.Percentile([7.5], 0));
        Assert.Null(IdentityStatistics.Percentile([], 95));
    }

    [Fact]
    public void InvalidArguments_ThrowArgumentOutOfRange()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => IdentityStatistics.Wilson(-1, 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => IdentityStatistics.Wilson(11, 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => IdentityStatistics.Wilson(0, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => IdentityStatistics.Wilson(1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => IdentityStatistics.Wilson(5, 10, 0.0));
        Assert.Throws<ArgumentOutOfRangeException>(() => IdentityStatistics.Wilson(5, 10, 1.0));
        Assert.Throws<ArgumentOutOfRangeException>(() => IdentityStatistics.Wilson(5, 10, -0.5));
        Assert.Throws<ArgumentOutOfRangeException>(() => IdentityStatistics.Wilson(5, 10, double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => IdentityStatistics.ClopperPearsonUpper(-1, 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => IdentityStatistics.ClopperPearsonUpper(11, 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => IdentityStatistics.ClopperPearsonUpper(0, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => IdentityStatistics.ClopperPearsonUpper(1, 10, 1.0));
        Assert.Throws<ArgumentOutOfRangeException>(() => IdentityStatistics.ClopperPearsonUpper(0, 10, 0.0));
        Assert.Throws<ArgumentOutOfRangeException>(() => IdentityStatistics.RuleOfThree(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => IdentityStatistics.Percentile([1.0], -0.1));
        Assert.Throws<ArgumentOutOfRangeException>(() => IdentityStatistics.Percentile([1.0], 100.1));
        Assert.Throws<ArgumentOutOfRangeException>(() => IdentityStatistics.Percentile([1.0], double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => IdentityStatistics.Percentile([1.0, double.NaN], 50));
        Assert.Throws<ArgumentOutOfRangeException>(() => IdentityStatistics.Fmr([0.1, double.NaN], 0.5));
        Assert.Throws<ArgumentOutOfRangeException>(() => IdentityStatistics.Fnmr([0.1, double.NaN], 0.5));
        Assert.Throws<ArgumentOutOfRangeException>(() => IdentityStatistics.Fmr([0.1], double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => IdentityStatistics.Fnmr([0.1], double.PositiveInfinity));
        Assert.Throws<ArgumentOutOfRangeException>(() => IdentityStatistics.Apcer(new Dictionary<string, bool[]> { [" "] = [true] }));
        Assert.Throws<ArgumentOutOfRangeException>(() => IdentityStatistics.FieldExactness([(" ", "a", "a")], (_, value) => value));
    }

    [Fact]
    public void NullCollections_ThrowArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() => IdentityStatistics.Fmr(null!, 0.5));
        Assert.Throws<ArgumentNullException>(() => IdentityStatistics.Fnmr(null!, 0.5));
        Assert.Throws<ArgumentNullException>(() => IdentityStatistics.Bpcer(null!));
        Assert.Throws<ArgumentNullException>(() => IdentityStatistics.Percentile(null!, 50));
        Assert.Throws<ArgumentNullException>(() => IdentityStatistics.Apcer<bool[]>(null!));
        Assert.Throws<ArgumentNullException>(() => IdentityStatistics.FieldExactness(null!, (_, value) => value));
        Assert.Throws<ArgumentNullException>(() => IdentityStatistics.FieldExactness([], null!));
    }

    [Fact]
    public void Errors_NeverCarryAScoreOrAField()
    {
        // A score of a person must not reach a message: the exception names the parameter and the rule, nothing else.
        var scoreException = Assert.Throws<ArgumentOutOfRangeException>(() => IdentityStatistics.Fmr([0.123456, double.NaN, 0.654321], 0.5));
        var thresholdException = Assert.Throws<ArgumentOutOfRangeException>(() => IdentityStatistics.Fnmr([0.123456], double.NaN));
        var fieldException = Assert.Throws<ArgumentOutOfRangeException>(() => IdentityStatistics.FieldExactness([(" ", "VALUE-SENTINEL", "VALUE-SENTINEL")], (_, value) => value));

        foreach (var exception in new[] { scoreException, thresholdException, fieldException })
        {
            Assert.DoesNotContain("0.123456", exception.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("0.654321", exception.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("SENTINEL", exception.Message, StringComparison.Ordinal);
            Assert.Null(exception.ActualValue);
        }
    }

    private static NamedRate ExactnessOfThreeHundredWithMisses(int misses)
    {
        var pairs = new List<(string Field, string? Expected, string? Read)>();
        for (var i = 0; i < 300; i++)
            pairs.Add(("surname", "A", i < misses ? "B" : "A"));

        return Assert.Single(IdentityStatistics.FieldExactness(pairs, (_, value) => value));
    }

    // sum_{i=0..k} C(n,i) p^i (1-p)^(n-i), the textbook way: the coefficient built term by term, the powers by Math.Pow.
    private static double IndependentBinomialCdf(int k, int n, double p)
    {
        var sum = 0.0;
        var coefficient = 1.0;
        for (var i = 0; i <= k; i++)
        {
            sum += coefficient * Math.Pow(p, i) * Math.Pow(1.0 - p, n - i);
            coefficient = coefficient * (n - i) / (i + 1);
        }

        return sum;
    }
}
