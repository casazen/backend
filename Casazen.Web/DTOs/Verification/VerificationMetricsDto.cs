using System.Text.Json.Serialization;
using Casazen.Core.Services;

namespace Casazen.Web.DTOs.Verification;

/// <summary>
/// The figures of the identity verifications of a period (<c>GET /api/verification/metrics</c>, E03/F04/US02/T03, NF-51 monitoring) for the
/// bookings the caller reaches.
/// </summary>
public sealed class VerificationMetricsDto
{
    /// <summary>First day of the period (Europe/Rome).</summary>
    public DateOnly From { get; set; }

    /// <summary>Last day of the period (Europe/Rome), included.</summary>
    public DateOnly To { get; set; }

    /// <summary>Automatic outcomes: passed.</summary>
    public int Ok { get; set; }

    /// <summary>Automatic outcomes: sent to a person (between the thresholds, MRZ not consistent, document unreadable, technical problem).</summary>
    public int Review { get; set; }

    /// <summary>Automatic outcomes: not passed (a new attempt, or the operator after the last one).</summary>
    public int NotOk { get; set; }

    /// <summary>Decisions of the operators: approved.</summary>
    public int OperatorApproved { get; set; }

    /// <summary>Decisions of the operators: rejected.</summary>
    public int OperatorRejected { get; set; }

    /// <summary>Every decision of an operator.</summary>
    public int OperatorDecisions { get; set; }

    /// <summary>Operator decisions / (automatic ok + operator decisions): the share of the concluded verifications an operator concluded, 0 to 1.</summary>
    public double OperatorShare { get; set; }

    /// <summary>
    /// The counters of the self-hosted identity verification of the same period and the same scope (E73/F01/US03/T01, NF-196), only with
    /// <c>Features:CheckInIdentityVerification</c> and <c>Features:CheckInIdentitySelfHosted</c> on. With either off the member is not in the answer at all, so the answer is exactly
    /// the one it was before the self-hosted verification (a client reads a missing <c>identity</c> as none).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IdentityVerificationMetricsDto? Identity { get; set; }
}

/// <summary>
/// The counters of the identity steps opened in the period (<see cref="VerificationMetricsDto.Identity"/>, E73/F01/US03/T01, NF-196): the denominator is every step opened, the
/// categories add up to it, and every rate is a fraction from 0 to 1 that is not rounded (the page formats it).
/// </summary>
public sealed class IdentityVerificationMetricsDto
{
    /// <summary>The identity steps opened in the period: the denominator of every rate.</summary>
    public int Opened { get; set; }

    /// <summary>How they split by category; the counts add up to <see cref="Opened"/>.</summary>
    public IdentityOutcomesDto Outcomes { get; set; } = new();

    /// <summary>Closed by an automatic approval out of <see cref="Opened"/>.</summary>
    public IdentityRateDto AutomationRate { get; set; } = new();

    /// <summary>Attempts whose shadow outcome is an approval out of <see cref="Opened"/>.</summary>
    public IdentityRateDto ShadowApprovalRate { get; set; } = new();

    /// <summary>What the policy would have decided against what a person decided after it.</summary>
    public IdentityShadowVsHumanDto ShadowVsHuman { get; set; } = new();

    /// <summary>The time of the presence step, in milliseconds.</summary>
    public IdentityPercentilesDto LatencyMs { get; set; } = new();

    /// <summary>The time from a review handed to a person to its decision, in seconds.</summary>
    public IdentityPercentilesDto ReviewSeconds { get; set; } = new();

    /// <summary>The steps by format and channel, in ordinal order.</summary>
    public IReadOnlyList<IdentityCoverageDto> Coverage { get; set; } = [];

    public static IdentityVerificationMetricsDto From(IdentityVerificationMetrics metrics)
    {
        ArgumentNullException.ThrowIfNull(metrics);

        return new IdentityVerificationMetricsDto
        {
            Opened = metrics.Opened,
            Outcomes = IdentityOutcomesDto.From(metrics.Outcomes),
            AutomationRate = IdentityRateDto.From(metrics.AutomationRate),
            ShadowApprovalRate = IdentityRateDto.From(metrics.ShadowApprovalRate),
            ShadowVsHuman = IdentityShadowVsHumanDto.From(metrics.ShadowVsHuman),
            LatencyMs = IdentityPercentilesDto.From(metrics.LatencyMs),
            ReviewSeconds = IdentityPercentilesDto.From(metrics.ReviewSeconds),
            Coverage = metrics.Coverage.Select(IdentityCoverageDto.From).ToList(),
        };
    }
}

/// <summary>The opened identity steps by category: a refusal, an unsupported format, an error, an unavailable verification, a minor, an abandonment and one in progress have each their own.</summary>
public sealed class IdentityOutcomesDto
{
    public int AutoApproved { get; set; }

    public int Review { get; set; }

    public int Assisted { get; set; }

    public int ConsentRefused { get; set; }

    public int FormatUnsupported { get; set; }

    public int TechnicalError { get; set; }

    public int Unavailable { get; set; }

    public int MinorExcluded { get; set; }

    public int Abandoned { get; set; }

    public int InProgress { get; set; }

    public static IdentityOutcomesDto From(IdentityOutcomeCounts counts)
    {
        ArgumentNullException.ThrowIfNull(counts);

        return new IdentityOutcomesDto
        {
            AutoApproved = counts.AutoApproved,
            Review = counts.Review,
            Assisted = counts.Assisted,
            ConsentRefused = counts.ConsentRefused,
            FormatUnsupported = counts.FormatUnsupported,
            TechnicalError = counts.TechnicalError,
            Unavailable = counts.Unavailable,
            MinorExcluded = counts.MinorExcluded,
            Abandoned = counts.Abandoned,
            InProgress = counts.InProgress,
        };
    }
}

/// <summary>A fraction of <see cref="N"/> with its Wilson 95% interval; <see cref="Value"/> is null when <see cref="N"/> is 0 (nothing measured is not a rate of 0).</summary>
public sealed class IdentityRateDto
{
    /// <summary>The fraction, 0 to 1; null when nothing was measured.</summary>
    public double? Value { get; set; }

    public double WilsonLow { get; set; }

    public double WilsonHigh { get; set; }

    /// <summary>The number of trials (the opened steps).</summary>
    public int N { get; set; }

    public static IdentityRateDto From(IdentityRate rate)
    {
        ArgumentNullException.ThrowIfNull(rate);

        return new IdentityRateDto { Value = rate.Value, WilsonLow = rate.WilsonLow, WilsonHigh = rate.WilsonHigh, N = rate.N };
    }
}

/// <summary>The shadow decision of the policy against the decision of the person, for the verifications with both; <see cref="ApproveReject"/> is the critical cell (a would-be false approval).</summary>
public sealed class IdentityShadowVsHumanDto
{
    public int ApproveApprove { get; set; }

    public int ApproveReject { get; set; }

    public int ReviewApprove { get; set; }

    public int ReviewReject { get; set; }

    public static IdentityShadowVsHumanDto From(IdentityShadowVsHuman value)
    {
        ArgumentNullException.ThrowIfNull(value);

        return new IdentityShadowVsHumanDto
        {
            ApproveApprove = value.ApproveApprove,
            ApproveReject = value.ApproveReject,
            ReviewApprove = value.ReviewApprove,
            ReviewReject = value.ReviewReject,
        };
    }
}

/// <summary>The median and the 95th percentile (nearest rank) of <see cref="N"/> values; both are null when there is none.</summary>
public sealed class IdentityPercentilesDto
{
    public double? P50 { get; set; }

    public double? P95 { get; set; }

    public int N { get; set; }

    public static IdentityPercentilesDto From(IdentityPercentiles percentiles)
    {
        ArgumentNullException.ThrowIfNull(percentiles);

        return new IdentityPercentilesDto { P50 = percentiles.P50, P95 = percentiles.P95, N = percentiles.N };
    }
}

/// <summary>One cell of the coverage: a format of the registry (<c>unknown</c> when the document was not read as one) on a channel (the family of the browser).</summary>
public sealed class IdentityCoverageDto
{
    public string FormatId { get; set; } = string.Empty;

    public string Channel { get; set; } = string.Empty;

    /// <summary>The steps opened for this format and channel.</summary>
    public int Opened { get; set; }

    /// <summary>How many closed with an automatic approval.</summary>
    public int AutoApproved { get; set; }

    /// <summary>How many attempts the policy would have approved (the shadow outcome).</summary>
    public int ShadowApproved { get; set; }

    /// <summary>The automatic approvals out of the opened steps, 0 to 1.</summary>
    public double Rate { get; set; }

    public double WilsonLow { get; set; }

    public double WilsonHigh { get; set; }

    public static IdentityCoverageDto From(IdentityCoverageCell cell)
    {
        ArgumentNullException.ThrowIfNull(cell);

        return new IdentityCoverageDto
        {
            FormatId = cell.FormatId,
            Channel = cell.Channel,
            Opened = cell.Opened,
            AutoApproved = cell.AutoApproved,
            ShadowApproved = cell.ShadowApproved,
            Rate = cell.Rate,
            WilsonLow = cell.WilsonLow,
            WilsonHigh = cell.WilsonHigh,
        };
    }
}
