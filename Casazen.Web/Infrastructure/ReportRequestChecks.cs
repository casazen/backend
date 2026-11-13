using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Infrastructure;

/// <summary>What a report request asks for: the JSON of the screen, or a file to download (NF-101).</summary>
public enum ReportFormat
{
    /// <summary>The JSON the screens read: the default.</summary>
    Json = 0,

    /// <summary>The CSV of <see cref="CsvWriter"/>: UTF-8 with BOM, <c>;</c>, decimal comma, for a spreadsheet set to Italian.</summary>
    Csv = 1,

    /// <summary>The Excel file of <see cref="XlsxWriter"/>: the same columns and numbers as the CSV, numbers as numbers, days as dates, percentages as percentages.</summary>
    Xlsx = 2,
}

/// <summary>
/// The words of a report request, checked before anything is read (SR-10, NF-96): the format, how the report is cut, whether it
/// compares with the year before, the size of the buckets and the days. One place for the reports of the host (<c>api/reports</c>),
/// of the agency by owner (<c>api/reports/by-owner</c>) and of the owner portal (<c>api/owner-portal</c>), so they answer the same
/// codes for the same mistakes. A problem is the answer to give; null means the words are fine.
/// </summary>
public static class ReportRequestChecks
{
    /// <summary>Code of a <c>from</c> and <c>to</c> that are missing, out of order or out of the supported years.</summary>
    public const string InvalidPeriodCode = "report_invalid_period";

    /// <summary>Code of a period longer than <see cref="HostReportRules.MaxMonths"/> months.</summary>
    public const string PeriodTooLongCode = "report_period_too_long";

    /// <summary>Code of a <c>format</c> that is not <c>json</c>, <c>csv</c> or <c>xlsx</c>.</summary>
    public const string InvalidFormatCode = "report_invalid_format";

    /// <summary>Code of a <c>groupBy</c> that is not one of the cuts the report has.</summary>
    public const string InvalidGroupByCode = "report_invalid_group_by";

    /// <summary>Code of a <c>compare</c> that is not <c>none</c> or <c>previousYear</c>.</summary>
    public const string InvalidCompareCode = "report_invalid_compare";

    /// <summary>Code of a <c>bucket</c> that is not <c>auto</c>, <c>week</c> or <c>month</c>.</summary>
    public const string InvalidBucketCode = "report_invalid_bucket";

    /// <summary>What a performance report, a trend or an owner view asks, once its words are checked.</summary>
    public sealed record PerformanceRequest(
        DateOnly From,
        DateOnly To,
        HostPerformanceGroupBy GroupBy,
        bool CompareYear,
        HostTrendBucketUnit Bucket,
        ReportFormat Format);

    /// <summary><c>json</c> (the default), <c>csv</c> or <c>xlsx</c>, whatever the case; 400 <c>report_invalid_format</c> for anything else.</summary>
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

    /// <summary>The extension of the file of a report, without the dot: the one the documents put on the file name (<c>csv</c>, <c>xlsx</c>).</summary>
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

    /// <summary>
    /// The days of the report: 400 <c>report_invalid_period</c> when one is missing, they are out of order or out of the supported years,
    /// 422 <c>report_period_too_long</c> beyond <see cref="HostReportRules.MaxMonths"/> calendar months.
    /// </summary>
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

    /// <summary>
    /// A report that is cut and compared (NF-96), in the order of the mistakes: <c>format</c>, <c>groupBy</c> (<c>none</c>, the default,
    /// <c>property</c>, and <c>channel</c> where <paramref name="allowChannel"/>), <c>compare</c> (<c>none</c>, the default, or
    /// <c>previousYear</c>), <c>bucket</c> (<c>auto</c>, the default, <c>week</c> or <c>month</c>), then the days.
    /// </summary>
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

    // The value a word stands for, whatever its case, `empty` for no word at all; null for a word the report does not know.
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
