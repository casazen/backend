using Casazen.Core.Authorization;
using Casazen.Core.Identity;
using Casazen.Core.Identity.Statistics;

namespace Casazen.Core.Services;

/// <summary>
/// The counters of the self-hosted identity verification of the check-in (E73/F01/US03/T01, NF-196, decision D10): what the pilot of 500 consecutive check-ins and the
/// release criterion «Automazione» are read from. <b>The denominator is complete</b>: every identity step that was opened in the period counts once, and a refusal of the
/// consent, a format the registry does not hold, a technical error, an unavailable verification, a minor, an abandonment and a verification still in progress are categories of
/// their own (<see cref="IdentityOutcomeCategory"/>), so the categories always sum to <see cref="IdentityVerificationMetrics.Opened"/>. The rates carry the Wilson 95% interval of
/// <see cref="IdentityStatistics"/>, the latency and the review time its nearest-rank percentiles; nothing is rounded here (the page formats).
/// </summary>
/// <remarks>
/// <para><b>One scope, always named.</b> The caller hands over the <see cref="HostScope"/> of the endpoint (the org and the properties the caller reaches, the one rule of
/// <c>InScope</c>): a session or a row of another org, or of a property outside the reach, is never counted, whatever the tenant filter of the context says. The services never
/// check a role.</para>
/// <para><b>What is counted.</b> The sessions whose identity step was opened (<c>GuestCheckInSession.IdentityOpenedAt</c>) in <c>[fromUtc, toUtc)</c>, with the decision row of the
/// policy each one closed with and the first decision of a person after it (the pairing is in <c>IdentityVerificationLedger</c>, shared with the export of the pilot). The counters
/// hold ids, codes, counts and times only: never an image, a value read on a document, a name, an e-mail or a score of a person.</para>
/// </remarks>
public interface IIdentityVerificationMetricsService
{
    /// <summary>
    /// The counters of the identity steps opened between <paramref name="fromUtc"/> (included) and <paramref name="toUtc"/> (excluded) for the org and the properties of
    /// <paramref name="scope"/>. No step opened gives a result with zeros, null rates and the interval (0, 1): no measurement is not a rate of 0.
    /// </summary>
    Task<IdentityVerificationMetrics> GetAsync(HostScope scope, DateTime fromUtc, DateTime toUtc, CancellationToken cancellationToken = default);
}

/// <summary>
/// The category an opened identity step falls in (E73/F01/US03/T01): the terminal codes of <see cref="IdentityOutcomeCodes"/> plus the one a step has while it is open,
/// <see cref="InProgress"/>. A closed verification has the category of its code; an open one is <see cref="Abandoned"/> once it is older than
/// <c>IdentityVerification:Retention:AbandonedAfterHours</c>, <see cref="InProgress"/> before. Every opened step falls in exactly one.
/// </summary>
public enum IdentityOutcomeCategory
{
    /// <summary>The policy approved the guest in a qualified segment.</summary>
    AutoApproved,

    /// <summary>A person reviews the verification (also the category of a closed verification whose code is missing or unknown: a hand-off is always a person).</summary>
    Review,

    /// <summary>The guest chose the assisted path or asked for a person.</summary>
    Assisted,

    /// <summary>The guest refused the biometric consent.</summary>
    ConsentRefused,

    /// <summary>The document is of a format the registry does not hold.</summary>
    FormatUnsupported,

    /// <summary>A model failed or timed out: a person decides.</summary>
    TechnicalError,

    /// <summary>The automatic verification was not offered.</summary>
    Unavailable,

    /// <summary>The guest who must be verified is a minor: never processed.</summary>
    MinorExcluded,

    /// <summary>Closed as abandoned, or still open and older than the abandonment time of the retention.</summary>
    Abandoned,

    /// <summary>Open, not yet old enough to be abandoned.</summary>
    InProgress,
}

/// <summary>The codes of <see cref="IdentityOutcomeCategory"/> (the snake case ones of the export of the pilot) and how a stored terminal code maps to a category.</summary>
public static class IdentityOutcomeCategories
{
    /// <summary>The code of a verification that is open and not abandoned (it has no terminal code yet).</summary>
    public const string InProgressCode = "in_progress";

    /// <summary>Every category, in the order of the counters.</summary>
    public static IReadOnlyList<IdentityOutcomeCategory> All { get; } = Enum.GetValues<IdentityOutcomeCategory>();

    /// <summary>The code of a category: the one of <see cref="IdentityOutcomeCodes"/> for a terminal outcome, <see cref="InProgressCode"/> for an open one.</summary>
    public static string CodeOf(IdentityOutcomeCategory category)
    {
        return category switch
        {
            IdentityOutcomeCategory.AutoApproved => IdentityOutcomeCodes.AutoApproved,
            IdentityOutcomeCategory.Review => IdentityOutcomeCodes.Review,
            IdentityOutcomeCategory.Assisted => IdentityOutcomeCodes.Assisted,
            IdentityOutcomeCategory.ConsentRefused => IdentityOutcomeCodes.ConsentRefused,
            IdentityOutcomeCategory.FormatUnsupported => IdentityOutcomeCodes.FormatUnsupported,
            IdentityOutcomeCategory.TechnicalError => IdentityOutcomeCodes.TechnicalError,
            IdentityOutcomeCategory.Unavailable => IdentityOutcomeCodes.Unavailable,
            IdentityOutcomeCategory.MinorExcluded => IdentityOutcomeCodes.MinorExcluded,
            IdentityOutcomeCategory.Abandoned => IdentityOutcomeCodes.Abandoned,
            IdentityOutcomeCategory.InProgress => InProgressCode,
            _ => IdentityOutcomeCodes.Review,
        };
    }

    /// <summary>
    /// The category of a closed verification from its terminal code (<c>GuestCheckInSession.IdentityOutcomeCode</c>). A closed verification whose code is missing or is not one
    /// of <see cref="IdentityOutcomeCodes.All"/> is a person reviewing (<see cref="IdentityOutcomeCategory.Review"/>), like the flow itself reads a reason it does not know: a
    /// hand-off is always to a person, and the categories must still add up to the opened steps.
    /// </summary>
    public static IdentityOutcomeCategory OfTerminalCode(string? code)
    {
        return code switch
        {
            IdentityOutcomeCodes.AutoApproved => IdentityOutcomeCategory.AutoApproved,
            IdentityOutcomeCodes.Assisted => IdentityOutcomeCategory.Assisted,
            IdentityOutcomeCodes.ConsentRefused => IdentityOutcomeCategory.ConsentRefused,
            IdentityOutcomeCodes.FormatUnsupported => IdentityOutcomeCategory.FormatUnsupported,
            IdentityOutcomeCodes.TechnicalError => IdentityOutcomeCategory.TechnicalError,
            IdentityOutcomeCodes.Unavailable => IdentityOutcomeCategory.Unavailable,
            IdentityOutcomeCodes.MinorExcluded => IdentityOutcomeCategory.MinorExcluded,
            IdentityOutcomeCodes.Abandoned => IdentityOutcomeCategory.Abandoned,
            _ => IdentityOutcomeCategory.Review,
        };
    }
}

/// <summary>
/// The counters of the identity steps opened in a period (<see cref="IIdentityVerificationMetricsService.GetAsync"/>), the shape of the contract with the frontend. Counts are whole
/// numbers, rates are fractions from 0 to 1, times are milliseconds and seconds, none of them rounded.
/// </summary>
/// <param name="Opened">The steps opened in the period: the denominator of every rate.</param>
/// <param name="Outcomes">How they split by category; the counts add up to <paramref name="Opened"/>.</param>
/// <param name="AutomationRate">Closed by an automatic approval out of <paramref name="Opened"/>, with its Wilson 95% interval.</param>
/// <param name="ShadowApprovalRate">Attempts whose shadow outcome is an approval out of <paramref name="Opened"/>, with its Wilson 95% interval.</param>
/// <param name="ShadowVsHuman">What the policy would have done against what the person did, for the verifications with both.</param>
/// <param name="LatencyMs">The time of the presence step of the verifications the policy decided.</param>
/// <param name="ReviewSeconds">The time from the review the policy handed over to the decision of the person.</param>
/// <param name="Coverage">The steps by format and channel (the family of the browser), in ordinal order.</param>
public sealed record IdentityVerificationMetrics(
    int Opened,
    IdentityOutcomeCounts Outcomes,
    IdentityRate AutomationRate,
    IdentityRate ShadowApprovalRate,
    IdentityShadowVsHuman ShadowVsHuman,
    IdentityPercentiles LatencyMs,
    IdentityPercentiles ReviewSeconds,
    IReadOnlyList<IdentityCoverageCell> Coverage);

/// <summary>The opened steps by category (<see cref="IdentityOutcomeCategory"/>); <see cref="Total"/> is the denominator they add up to.</summary>
public sealed record IdentityOutcomeCounts(
    int AutoApproved,
    int Review,
    int Assisted,
    int ConsentRefused,
    int FormatUnsupported,
    int TechnicalError,
    int Unavailable,
    int MinorExcluded,
    int Abandoned,
    int InProgress)
{
    /// <summary>The sum of the categories.</summary>
    public int Total => AutoApproved + Review + Assisted + ConsentRefused + FormatUnsupported + TechnicalError + Unavailable + MinorExcluded + Abandoned + InProgress;

    /// <summary>The count of one category.</summary>
    public int Of(IdentityOutcomeCategory category)
    {
        return category switch
        {
            IdentityOutcomeCategory.AutoApproved => AutoApproved,
            IdentityOutcomeCategory.Assisted => Assisted,
            IdentityOutcomeCategory.ConsentRefused => ConsentRefused,
            IdentityOutcomeCategory.FormatUnsupported => FormatUnsupported,
            IdentityOutcomeCategory.TechnicalError => TechnicalError,
            IdentityOutcomeCategory.Unavailable => Unavailable,
            IdentityOutcomeCategory.MinorExcluded => MinorExcluded,
            IdentityOutcomeCategory.Abandoned => Abandoned,
            IdentityOutcomeCategory.InProgress => InProgress,
            _ => Review,
        };
    }

    /// <summary>The counts of <paramref name="categories"/>, one category per opened step.</summary>
    public static IdentityOutcomeCounts From(IEnumerable<IdentityOutcomeCategory> categories)
    {
        ArgumentNullException.ThrowIfNull(categories);

        var counts = new int[IdentityOutcomeCategories.All.Count];
        foreach (var category in categories)
            counts[(int)category]++;

        int At(IdentityOutcomeCategory category) => counts[(int)category];
        return new IdentityOutcomeCounts(
            At(IdentityOutcomeCategory.AutoApproved),
            At(IdentityOutcomeCategory.Review),
            At(IdentityOutcomeCategory.Assisted),
            At(IdentityOutcomeCategory.ConsentRefused),
            At(IdentityOutcomeCategory.FormatUnsupported),
            At(IdentityOutcomeCategory.TechnicalError),
            At(IdentityOutcomeCategory.Unavailable),
            At(IdentityOutcomeCategory.MinorExcluded),
            At(IdentityOutcomeCategory.Abandoned),
            At(IdentityOutcomeCategory.InProgress));
    }
}

/// <summary>
/// A count out of <paramref name="N"/> with its Wilson 95% interval (<see cref="IdentityStatistics.Wilson"/>). <paramref name="Value"/> is null when nothing was measured
/// (<paramref name="N"/> is 0): that is not a rate of 0, and the interval is then (0, 1), as the statistics say.
/// </summary>
public sealed record IdentityRate(double? Value, double WilsonLow, double WilsonHigh, int N)
{
    /// <summary><paramref name="count"/> out of <paramref name="n"/> with the interval of <see cref="IdentityStatistics.Wilson"/>.</summary>
    public static IdentityRate Of(int count, int n)
    {
        var (low, high) = IdentityStatistics.Wilson(count, n);
        return new IdentityRate(n == 0 ? null : (double)count / n, low, high, n);
    }
}

/// <summary>
/// The critical table of the pilot: what the policy would have decided (its shadow outcome) against what the person decided later, for the verifications with both. The
/// critical cell is <see cref="ApproveReject"/>: a would-be false approval.
/// </summary>
public sealed record IdentityShadowVsHuman(int ApproveApprove, int ApproveReject, int ReviewApprove, int ReviewReject);

/// <summary>The median and the 95th percentile (nearest rank, <see cref="IdentityStatistics.Percentile"/>) of <paramref name="N"/> values; both null when there is none.</summary>
public sealed record IdentityPercentiles(double? P50, double? P95, int N)
{
    /// <summary>The percentile of the median.</summary>
    public const double MedianPercent = 50;

    /// <summary>The percentile of the tail (p95).</summary>
    public const double TailPercent = 95;

    /// <summary>The percentiles of <paramref name="values"/>; the list is neither changed nor retained.</summary>
    public static IdentityPercentiles Of(IReadOnlyList<double> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return new IdentityPercentiles(IdentityStatistics.Percentile(values, MedianPercent), IdentityStatistics.Percentile(values, TailPercent), values.Count);
    }
}

/// <summary>
/// One cell of the coverage: the steps opened for a format of the registry (<see cref="UnknownFormat"/> when the document was not read as one) on a channel (a code of
/// <see cref="IdentityChannels"/>), how many closed with an automatic approval, how many the policy would have approved (the shadow outcome), and the automation rate of
/// the cell with its Wilson 95% interval. <paramref name="Opened"/> is at least 1: a cell with no step is not listed.
/// </summary>
public sealed record IdentityCoverageCell(
    string FormatId,
    string Channel,
    int Opened,
    int AutoApproved,
    int ShadowApproved,
    double Rate,
    double WilsonLow,
    double WilsonHigh)
{
    /// <summary>The format of a step whose document was not read as one of the registry (a refusal, an abandonment, a format the registry does not hold).</summary>
    public const string UnknownFormat = "unknown";
}
