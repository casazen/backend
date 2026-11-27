using System.Buffers;

namespace Casazen.Core.Identity.Statistics;

// E74/F01/US01/T01 (NF-198, NF-196, D12): the statistics of the release criteria of the self-hosted identity verification, in ONE static class, so that a
// number on the dashboard (E73/F01/US03/T01) and the same number in the report of the qualification toolkit (E74/F01/US02/T01) are computed by the same code.
// Pure functions: no I/O, no logging, no clock, no culture (nothing is parsed or formatted here, strings are compared ordinally). The functions that take
// scores, outcomes or document fields only count and compare them: they return counts, names of attack classes or fields, and numbers, never one of the
// inputs, and the errors they throw never carry a score, an outcome or a field value.

/// <summary>
/// A count out of a total: the base of every rate of the criteria. The rate is null when nothing was measured (<see cref="Total"/> is 0): no measurement is
/// not a rate of 0, and the bounds of <see cref="IdentityStatistics"/> say the same (Wilson (0, 1), upper bound 1).
/// </summary>
public readonly record struct CountedRate(long Count, long Total)
{
    /// <summary><see cref="Count"/> / <see cref="Total"/>, or null when <see cref="Total"/> is 0.</summary>
    public double? Rate => Total == 0 ? null : (double)Count / Total;
}

/// <summary>The <see cref="CountedRate"/> of one attack class (APCER) or of one document field (exactness).</summary>
public readonly record struct NamedRate(string Name, CountedRate Rate);

/// <summary>The APCER of every attack class, in ordinal order of the class name, and the worst of them.</summary>
public sealed record ApcerResult(IReadOnlyList<NamedRate> ByClass, NamedRate? Worst)
{
    /// <summary>The maximum of the APCER over the classes, or null when no class has an outcome.</summary>
    public double? Max => Worst?.Rate.Rate;
}

/// <summary>
/// Wilson and Clopper-Pearson bounds, the rule of three, FMR / FNMR, APCER / BPCER, the exactness of the fields read on a document and nearest-rank
/// percentiles. Conventions, the same everywhere: a count is a <c>long</c>; <c>confidence</c> is strictly between 0 and 1 (0.95 by default); a score is a
/// similarity, higher is closer, and a comparison matches when <c>score &gt;= threshold</c> (the comparison of the decision); an outcome is <c>true</c> when
/// the presentation was accepted as bona fide. Invalid arguments throw <see cref="ArgumentOutOfRangeException"/> (a null collection throws
/// <see cref="ArgumentNullException"/>); no sample is ever "all of them": n = 0 is a valid, empty measurement.
/// </summary>
public static class IdentityStatistics
{
    // The numeric core (internal methods are visible to Casazen.Tests): a convergence criterion is not a decision threshold, the thresholds of the criteria
    // live in the targets of the qualification and in the versioned policy.
    private const double BisectionTolerance = 1e-12;
    private const int MaxBisectionSteps = 200;
    private const double NewtonTolerance = 1e-13;
    private const int MaxNewtonSteps = 50;
    private const double TermEpsilon = 1e-17;
    private const double GammaEpsilon = 1e-15;
    private const double GammaTiny = 1e-300;
    private const int MaxGammaSteps = 1000;
    private const double RankSlack = 1e-12;
    private const double LanczosG = 7.0;

    private static readonly double HalfLogTwoPi = 0.5 * Math.Log(2.0 * Math.PI);
    private static readonly double HalfLogPi = 0.5 * Math.Log(Math.PI);
    private static readonly double InverseSqrtTwoPi = 1.0 / Math.Sqrt(2.0 * Math.PI);

    // Lanczos approximation of the gamma function (g = 7, nine coefficients).
    private static readonly double[] LanczosCoefficients =
    [
        0.99999999999980993, 676.5203681218851, -1259.1392167224028, 771.32342877765313, -176.61502916214059, 12.507343278686905, -0.13857109526572012,
        9.9843695780195716e-6, 1.5056327351493116e-7,
    ];

    /// <summary>
    /// The Wilson score interval of a proportion at the two-sided <paramref name="confidence"/> (z = 1.959963984540054 at 95%): <paramref name="successes"/>
    /// out of <paramref name="n"/> trials. The interval of the automation rate (NF-196: 425 of 500 gives about 0.8160 to 0.8786) and the bound of the exactness of a
    /// field. n = 0 gives (0, 1); 0 successes give a lower bound of exactly 0, n successes an upper bound of exactly 1.
    /// </summary>
    public static (double Low, double High) Wilson(long successes, long n, double confidence = 0.95)
    {
        CheckCounts(successes, n, nameof(successes), nameof(n));
        CheckConfidence(confidence);
        if (n == 0)
            return (0.0, 1.0);

        var z = TwoSidedZ(confidence);
        var trials = (double)n;
        var proportion = successes / trials;
        var zSquared = z * z;
        var denominator = 1.0 + zSquared / trials;
        var centre = (proportion + zSquared / (2.0 * trials)) / denominator;
        var margin = z * Math.Sqrt(proportion * (1.0 - proportion) / trials + zSquared / (4.0 * trials * trials)) / denominator;

        var low = successes == 0 ? 0.0 : Math.Max(0.0, centre - margin);
        var high = successes == n ? 1.0 : Math.Min(1.0, centre + margin);
        return (low, high);
    }

    /// <summary>
    /// The one-sided exact (Clopper-Pearson) upper bound of an error rate at <paramref name="confidence"/>: the proportion U for which the chance of
    /// <paramref name="errors"/> or fewer errors in <paramref name="n"/> independent trials is <c>1 - confidence</c>. With no error it is the closed form
    /// <c>1 - (1 - confidence)^(1/n)</c> (about 0.0001 for 29,956 trials and 0.00994 for 300); otherwise a bisection on the binomial distribution, summed in
    /// the log domain around its largest term, converging well below 1e-10. n = 0 and n errors out of n give 1.
    /// </summary>
    public static double ClopperPearsonUpper(long errors, long n, double confidence = 0.95)
    {
        CheckCounts(errors, n, nameof(errors), nameof(n));
        CheckConfidence(confidence);
        if (n == 0 || errors == n)
            return 1.0;

        var alpha = 1.0 - confidence;
        if (errors == 0)
            return 1.0 - Math.Exp(Math.Log(alpha) / n);

        // The chance of "errors or fewer" falls from 1 (at 0) to 0 (at 1) as the proportion grows: the bound is where it crosses alpha.
        var low = 0.0;
        var high = 1.0;
        for (var step = 0; step < MaxBisectionSteps && high - low > BisectionTolerance * high; step++)
        {
            var middle = low + (high - low) / 2.0;
            if (BinomialCdf(errors, n, middle) > alpha)
                low = middle;
            else
                high = middle;
        }

        return low + (high - low) / 2.0;
    }

    /// <summary>
    /// The rule of three: 3 / n, the approximation of the 95% upper bound after no error in n independent trials (about 300 trials per class for a bound near
    /// 1%, about 30,000 impostor comparisons for a bound near 0.01%). n = 0 gives 1, and so does any n below 3 (a bound is never above 1).
    /// </summary>
    public static double RuleOfThree(long n)
    {
        if (n < 0)
            throw OutOfRange(nameof(n), "The number of trials cannot be negative.");

        return n == 0 ? 1.0 : Math.Min(1.0, 3.0 / n);
    }

    /// <summary>
    /// The false match rate at <paramref name="threshold"/>: the impostor comparisons (two different persons) whose score is at or above the threshold, out
    /// of all of them. The count is of the matches that must not happen.
    /// </summary>
    public static CountedRate Fmr(IReadOnlyList<double> impostorScores, double threshold)
    {
        ArgumentNullException.ThrowIfNull(impostorScores);
        CheckThreshold(threshold);

        long matches = 0;
        for (var i = 0; i < impostorScores.Count; i++)
        {
            var score = impostorScores[i];
            CheckScore(score, nameof(impostorScores));
            if (score >= threshold)
                matches++;
        }

        return new CountedRate(matches, impostorScores.Count);
    }

    /// <summary>
    /// The false non-match rate at <paramref name="threshold"/>: the genuine comparisons (the same person) whose score is below the threshold, out of all of
    /// them. The count is of the guests who are turned away.
    /// </summary>
    public static CountedRate Fnmr(IReadOnlyList<double> genuineScores, double threshold)
    {
        ArgumentNullException.ThrowIfNull(genuineScores);
        CheckThreshold(threshold);

        long nonMatches = 0;
        for (var i = 0; i < genuineScores.Count; i++)
        {
            var score = genuineScores[i];
            CheckScore(score, nameof(genuineScores));
            if (score < threshold)
                nonMatches++;
        }

        return new CountedRate(nonMatches, genuineScores.Count);
    }

    /// <summary>
    /// The attack presentation classification error rate (APCER) of every attack class, and the maximum over the classes (the figure the criterion is
    /// stated on). Each outcome of a class is true when that attack was accepted as bona fide, the error. A class with no outcome is listed with a null
    /// rate and cannot be the worst; a tie goes to the class that comes first in ordinal order.
    /// </summary>
    public static ApcerResult Apcer<TOutcomes>(IReadOnlyDictionary<string, TOutcomes> attackOutcomesByClass)
        where TOutcomes : IReadOnlyList<bool>
    {
        ArgumentNullException.ThrowIfNull(attackOutcomesByClass);

        var byClass = new List<NamedRate>(attackOutcomesByClass.Count);
        foreach (var (attackClass, outcomes) in attackOutcomesByClass)
        {
            if (string.IsNullOrWhiteSpace(attackClass) || outcomes is null)
                throw OutOfRange(nameof(attackOutcomesByClass), "An attack class has no name or no outcomes.");

            long accepted = 0;
            for (var i = 0; i < outcomes.Count; i++)
            {
                if (outcomes[i])
                    accepted++;
            }

            byClass.Add(new NamedRate(attackClass, new CountedRate(accepted, outcomes.Count)));
        }

        byClass.Sort(static (left, right) => string.CompareOrdinal(left.Name, right.Name));
        return new ApcerResult(byClass, Worst(byClass));
    }

    /// <summary>
    /// The bona fide presentation classification error rate (BPCER): the bona fide presentations (a real person present) that were NOT accepted, out of all
    /// of them. Each outcome is true when the presentation was accepted as bona fide, the right answer.
    /// </summary>
    public static CountedRate Bpcer(IReadOnlyList<bool> bonaFideOutcomes)
    {
        ArgumentNullException.ThrowIfNull(bonaFideOutcomes);

        long rejected = 0;
        for (var i = 0; i < bonaFideOutcomes.Count; i++)
        {
            if (!bonaFideOutcomes[i])
                rejected++;
        }

        return new CountedRate(rejected, bonaFideOutcomes.Count);
    }

    /// <summary>
    /// The exactness of every field read on a document: how many pairs (the value on the ground truth, the value that was read) agree EXACTLY once both go
    /// through <paramref name="normalize"/> (the field name and the raw value in, the normalized value out: the normalization of the coherence checker,
    /// handed in so that Core does not depend on Infrastructure). A missing value and an empty one are the same value. The count is of the exact matches, the
    /// number to hand to <see cref="Wilson"/>; fields come in ordinal order of the field name. The values are compared and forgotten: only the field names
    /// and the counts are returned.
    /// </summary>
    public static IReadOnlyList<NamedRate> FieldExactness(IEnumerable<(string Field, string? Expected, string? Read)> pairs, Func<string, string?, string?> normalize)
    {
        ArgumentNullException.ThrowIfNull(pairs);
        ArgumentNullException.ThrowIfNull(normalize);

        var byField = new Dictionary<string, (long Exact, long Total)>(StringComparer.Ordinal);
        foreach (var (field, expected, read) in pairs)
        {
            if (string.IsNullOrWhiteSpace(field))
                throw OutOfRange(nameof(pairs), "A pair has no field name.");

            var exact = string.Equals(normalize(field, expected) ?? string.Empty, normalize(field, read) ?? string.Empty, StringComparison.Ordinal);
            byField.TryGetValue(field, out var seen);
            byField[field] = (seen.Exact + (exact ? 1 : 0), seen.Total + 1);
        }

        var result = new List<NamedRate>(byField.Count);
        foreach (var (field, seen) in byField)
            result.Add(new NamedRate(field, new CountedRate(seen.Exact, seen.Total)));

        result.Sort(static (left, right) => string.CompareOrdinal(left.Name, right.Name));
        return result;
    }

    /// <summary>
    /// The nearest-rank percentile: the value at rank <c>ceil(percent / 100 * N)</c> of the values in ascending order (rank 1 for 0), so the result is always
    /// one of the values. <paramref name="percent"/> is a percentage from 0 to 100 (95 for the p95 of a latency, not 0.95). No value gives null. The input
    /// is not changed and not retained.
    /// </summary>
    public static double? Percentile(IReadOnlyList<double> values, double percent)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (!(percent >= 0.0 && percent <= 100.0))
            throw OutOfRange(nameof(percent), "The percentile must be between 0 and 100.");

        var count = values.Count;
        if (count == 0)
            return null;

        var rented = ArrayPool<double>.Shared.Rent(count);
        try
        {
            var sorted = rented.AsSpan(0, count);
            for (var i = 0; i < count; i++)
            {
                var value = values[i];
                CheckScore(value, nameof(values));
                sorted[i] = value;
            }

            sorted.Sort();

            // The slack absorbs the noise of a product such as 0.95 * 20, which must be rank 19 and not 20.
            var rank = (long)Math.Ceiling(percent * count / 100.0 * (1.0 - RankSlack));
            return sorted[(int)Math.Clamp(rank, 1, count) - 1];
        }
        finally
        {
            ArrayPool<double>.Shared.Return(rented);
        }
    }

    /// <summary>The z of a two-sided interval: the point with a chance of <c>(1 - confidence) / 2</c> above it (1.959963984540054 at 0.95).</summary>
    internal static double TwoSidedZ(double confidence)
    {
        return UpperNormalQuantile((1.0 - confidence) / 2.0);
    }

    /// <summary>
    /// The z with <c>P(Z &gt; z) = tail</c> for a standard normal Z, for 0 &lt; tail &lt;= 0.5: Newton steps on the exact tail, from the rational approximation
    /// 26.2.23 of Abramowitz and Stegun (error below 5e-4), to full double precision.
    /// </summary>
    internal static double UpperNormalQuantile(double tail)
    {
        if (tail >= 0.5)
            return 0.0;

        var t = Math.Sqrt(-2.0 * Math.Log(tail));
        var z = t - (2.515517 + t * (0.802853 + t * 0.010328)) / (1.0 + t * (1.432788 + t * (0.189269 + t * 0.001308)));
        for (var step = 0; step < MaxNewtonSteps; step++)
        {
            var correction = (NormalUpperTail(z) - tail) / (InverseSqrtTwoPi * Math.Exp(-0.5 * z * z));
            z += correction;
            if (Math.Abs(correction) <= NewtonTolerance * Math.Max(1.0, Math.Abs(z)))
                break;
        }

        return z;
    }

    /// <summary><c>P(Z &gt; z)</c> for a standard normal Z.</summary>
    internal static double NormalUpperTail(double z)
    {
        return z < 0.0 ? 1.0 - NormalUpperTail(-z) : 0.5 * RegularizedGammaQ(z * z / 2.0);
    }

    /// <summary>The natural logarithm of the gamma function for x &gt;= 0.5 (Lanczos, relative error of the gamma about 1e-15).</summary>
    internal static double LogGamma(double x)
    {
        var z = x - 1.0;
        var series = LanczosCoefficients[0];
        for (var i = 1; i < LanczosCoefficients.Length; i++)
            series += LanczosCoefficients[i] / (z + i);

        var t = z + LanczosG + 0.5;
        return HalfLogTwoPi + (z + 0.5) * Math.Log(t) - t + Math.Log(series);
    }

    /// <summary>
    /// <c>P(X &lt;= k)</c> for X binomial with n trials and proportion p (0 &lt; p &lt; 1, 0 &lt;= k &lt; n). The term at the mode (or at k when the mode is
    /// beyond it) is computed from the log-gamma function and the others from it by the ratio of two neighbours, outwards: no term underflows before the
    /// ones that count, and each side stops when what is left cannot change the sum.
    /// </summary>
    internal static double BinomialCdf(long k, long n, double p)
    {
        var q = 1.0 - p;
        var start = Math.Min(k, (long)Math.Floor((n + 1) * p));
        var logTerm = LogGamma(n + 1.0) - LogGamma(start + 1.0) - LogGamma(n - start + 1.0) + start * Math.Log(p) + (n - start) * Math.Log(q);
        var peak = Math.Exp(logTerm);

        var sum = peak;
        var term = peak;
        var down = q / p;
        for (var i = start; i > 0; i--)
        {
            term *= (double)i / (n - i + 1) * down;
            sum += term;
            if (term <= sum * TermEpsilon)
                break;
        }

        term = peak;
        var up = p / q;
        for (var i = start; i < k; i++)
        {
            term *= (double)(n - i) / (i + 1) * up;
            sum += term;
            if (term <= sum * TermEpsilon)
                break;
        }

        return Math.Min(1.0, sum);
    }

    // Q(1/2, x), the upper regularized incomplete gamma function, which is erfc(sqrt(x)): the series below x = 1.5, the continued fraction (modified Lentz)
    // above it.
    private static double RegularizedGammaQ(double x)
    {
        const double a = 0.5;
        if (x <= 0.0)
            return 1.0;

        var prefix = Math.Exp(-x + a * Math.Log(x) - HalfLogPi);
        if (x < a + 1.0)
        {
            var term = 1.0 / a;
            var sum = term;
            var denominator = a;
            for (var i = 0; i < MaxGammaSteps; i++)
            {
                denominator += 1.0;
                term *= x / denominator;
                sum += term;
                if (Math.Abs(term) < Math.Abs(sum) * TermEpsilon)
                    break;
            }

            return 1.0 - sum * prefix;
        }

        var b = x + 1.0 - a;
        var c = 1.0 / GammaTiny;
        var d = 1.0 / b;
        var fraction = d;
        for (var i = 1; i < MaxGammaSteps; i++)
        {
            var an = -i * (i - a);
            b += 2.0;
            d = an * d + b;
            if (Math.Abs(d) < GammaTiny)
                d = GammaTiny;

            c = b + an / c;
            if (Math.Abs(c) < GammaTiny)
                c = GammaTiny;

            d = 1.0 / d;
            var delta = d * c;
            fraction *= delta;
            if (Math.Abs(delta - 1.0) < GammaEpsilon)
                break;
        }

        return prefix * fraction;
    }

    private static NamedRate? Worst(List<NamedRate> byClass)
    {
        NamedRate? worst = null;
        foreach (var entry in byClass)
        {
            if (entry.Rate.Rate is { } rate && (worst is null || rate > worst.Value.Rate.Rate))
                worst = entry;
        }

        return worst;
    }

    private static void CheckCounts(long count, long n, string countName, string trialsName)
    {
        if (n < 0)
            throw OutOfRange(trialsName, "The number of trials cannot be negative.");

        if (count < 0)
            throw OutOfRange(countName, "A count cannot be negative.");

        if (count > n)
            throw OutOfRange(countName, "A count cannot be larger than the number of trials.");
    }

    private static void CheckConfidence(double confidence)
    {
        if (!(confidence > 0.0 && confidence < 1.0))
            throw OutOfRange(nameof(confidence), "The confidence must be strictly between 0 and 1.");
    }

    private static void CheckThreshold(double threshold)
    {
        if (!double.IsFinite(threshold))
            throw OutOfRange(nameof(threshold), "The threshold must be a finite number.");
    }

    private static void CheckScore(double score, string parameter)
    {
        if (double.IsNaN(score))
            throw OutOfRange(parameter, "A value is not a number.");
    }

    private static ArgumentOutOfRangeException OutOfRange(string parameter, string message)
    {
        return new ArgumentOutOfRangeException(parameter, message);
    }
}
