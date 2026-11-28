using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Infrastructure;

public enum ReportFormat
{
    Json = 0,

    Csv = 1,

    Xlsx = 2,
}

public static class ReportRequestChecks
{
    public const string InvalidPeriodCode = "report_invalid_period";

    public const string PeriodTooLongCode = "report_period_too_long";

    public const string InvalidFormatCode = "report_invalid_format";

    public const string InvalidGroupByCode = "report_invalid_group_by";

    public const string InvalidCompareCode = "report_invalid_compare";

    public const string InvalidBucketCode = "report_invalid_bucket";

    public sealed record PerformanceRequest(
        DateOnly From,
        DateOnly To,
        HostPerformanceGroupBy GroupBy,
        bool CompareYear,
        HostTrendBucketUnit Bucket,
        ReportFormat Format);

    public static ObjectResult? CheckFormat(this ControllerBase controller, string? format, out ReportFormat reportFormat)
    {
        ArgumentNullException.ThrowIfNull(controller);

        if (string.Equals(format, "csv", StringComparison.OrdinalIgnoreCase))
        {
            reportFormat = ReportFormat.Csv;
            return null;
        }

        if (string.Equals(format, "xlsx", StringComparison.OrdinalIgnoreCase))
        {
            reportFormat = ReportFormat.Xlsx;
            return null;
        }

        reportFormat = ReportFormat.Json;
        return !string.IsNullOrWhiteSpace(format) && !string.Equals(format, "json", StringComparison.OrdinalIgnoreCase)
            ? controller.ApiProblem(StatusCodes.Status400BadRequest, InvalidFormatCode, "ReportInvalidFormat")
            : null;
    }

    public static string FileExtensionOf(ReportFormat format) => format == ReportFormat.Xlsx ? "xlsx" : "csv";

    /// <summary>
    /// The file of a report as the answer: the bytes with the content type of <paramref name="format"/> and <c>Cache-Control: no-store</c>, because it is
    /// money and is built on request, never stored.
    /// </summary>
    public static FileContentResult ReportFile(this ControllerBase controller, ReportFormat format, byte[] content, string fileName)
    {
        ArgumentNullException.ThrowIfNull(controller);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        controller.Response.Headers.CacheControl = "no-store";
        return controller.File(content, format == ReportFormat.Xlsx ? XlsxWriter.ContentType : "text/csv", fileName);
    }

    public static ObjectResult? CheckPeriod(
        this ControllerBase controller, DateOnly? fromDay, DateOnly? toDay, out DateOnly first, out DateOnly last)
    {
        ArgumentNullException.ThrowIfNull(controller);

        first = default;
        last = default;
        if (fromDay is not { } from || toDay is not { } to)
            return controller.ApiProblem(StatusCodes.Status400BadRequest, InvalidPeriodCode, "ReportInvalidPeriod");

        switch (HostReportRules.CheckPeriod(from, to))
        {
            case HostReportPeriodCheck.Invalid:
                return controller.ApiProblem(StatusCodes.Status400BadRequest, InvalidPeriodCode, "ReportInvalidPeriod");
            case HostReportPeriodCheck.TooLong:
                return controller.ApiProblem(
                    StatusCodes.Status422UnprocessableEntity, PeriodTooLongCode, "ReportPeriodTooLong", HostReportRules.MaxMonths);
        }

        first = from;
        last = to;
        return null;
    }

    public static ObjectResult? CheckPerformanceRequest(
        this ControllerBase controller,
        DateOnly? fromDay,
        DateOnly? toDay,
        string? groupBy,
        string? compare,
        string? bucket,
        string? format,
        bool allowChannel,
        out PerformanceRequest request)
    {
        ArgumentNullException.ThrowIfNull(controller);

        request = new PerformanceRequest(default, default, HostPerformanceGroupBy.None, false, HostTrendBucketUnit.Auto, ReportFormat.Json);
        if (controller.CheckFormat(format, out var reportFormat) is { } formatProblem)
            return formatProblem;

        var cut = Choose(groupBy, HostPerformanceGroupBy.None, ("none", HostPerformanceGroupBy.None), ("property", HostPerformanceGroupBy.Property));
        if (allowChannel && cut is null)
            cut = Choose(groupBy, HostPerformanceGroupBy.None, ("channel", HostPerformanceGroupBy.Channel));
        if (cut is not { } cutBy)
            return controller.ApiProblem(
                StatusCodes.Status400BadRequest, InvalidGroupByCode, allowChannel ? "ReportInvalidGroupBy" : "ReportInvalidGroupByOwner");

        var compared = Choose(compare, false, ("none", false), ("previousyear", true));
        if (compared is not { } compareYear)
            return controller.ApiProblem(StatusCodes.Status400BadRequest, InvalidCompareCode, "ReportInvalidCompare");

        var size = Choose(
            bucket,
            HostTrendBucketUnit.Auto,
            ("auto", HostTrendBucketUnit.Auto),
            ("week", HostTrendBucketUnit.Week),
            ("month", HostTrendBucketUnit.Month));
        if (size is not { } unit)
            return controller.ApiProblem(StatusCodes.Status400BadRequest, InvalidBucketCode, "ReportInvalidBucket");

        if (controller.CheckPeriod(fromDay, toDay, out var first, out var last) is { } periodProblem)
            return periodProblem;

        request = new PerformanceRequest(first, last, cutBy, compareYear, unit, reportFormat);
        return null;
    }

    private static T? Choose<T>(string? word, T empty, params (string Name, T Value)[] options)
        where T : struct
    {
        if (string.IsNullOrWhiteSpace(word))
            return empty;

        foreach (var (name, value) in options)
        {
            if (string.Equals(word.Trim(), name, StringComparison.OrdinalIgnoreCase))
                return value;
        }

        return null;
    }
}
