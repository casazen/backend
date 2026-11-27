using System.Text.Json.Serialization;
using Casazen.Core.Services;

namespace Casazen.Web.DTOs.Verification;

public sealed class VerificationMetricsDto
{
    public DateOnly From { get; set; }

    public DateOnly To { get; set; }

    public int Ok { get; set; }

    public int Review { get; set; }

    public int NotOk { get; set; }

    public int OperatorApproved { get; set; }

    public int OperatorRejected { get; set; }

    public int OperatorDecisions { get; set; }

    public double OperatorShare { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IdentityVerificationMetricsDto? Identity { get; set; }
}

public sealed class IdentityVerificationMetricsDto
{
    public int Opened { get; set; }

    public IdentityOutcomesDto Outcomes { get; set; } = new();

    public IdentityRateDto AutomationRate { get; set; } = new();

    public IdentityRateDto ShadowApprovalRate { get; set; } = new();

    public IdentityShadowVsHumanDto ShadowVsHuman { get; set; } = new();

    public IdentityPercentilesDto LatencyMs { get; set; } = new();

    public IdentityPercentilesDto ReviewSeconds { get; set; } = new();

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

public sealed class IdentityRateDto
{
    public double? Value { get; set; }

    public double WilsonLow { get; set; }

    public double WilsonHigh { get; set; }

    public int N { get; set; }

    public static IdentityRateDto From(IdentityRate rate)
    {
        ArgumentNullException.ThrowIfNull(rate);

        return new IdentityRateDto { Value = rate.Value, WilsonLow = rate.WilsonLow, WilsonHigh = rate.WilsonHigh, N = rate.N };
    }
}

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

public sealed class IdentityCoverageDto
{
    public string FormatId { get; set; } = string.Empty;

    public string Channel { get; set; } = string.Empty;

    public int Opened { get; set; }

    public int AutoApproved { get; set; }

    public int ShadowApproved { get; set; }

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
