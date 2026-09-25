using System.Globalization;
using Microsoft.Extensions.Options;

namespace Casazen.Core.Options;

/// <summary>
/// Section <c>Cin</c> (CO-20, A5-31): the CIN deadline shown to hosts and the alerts before it. Runbook
/// <c>docs/runbooks/cin-format.md</c>, section "CIN deadline and host alerts".
/// </summary>
public class CinOptions
{
    public const string SectionName = "Cin";

    /// <summary>Format of <see cref="ExposureDeadline"/>.</summary>
    public const string DateFormat = "yyyy-MM-dd";

    /// <summary>Default <see cref="AlertDaysBefore"/>: 30, 7 and 1 day before the deadline.</summary>
    public static readonly IReadOnlyList<int> DefaultAlertDaysBefore = [30, 7, 1];

    /// <summary>
    /// Date (<c>yyyy-MM-dd</c>, Europe/Rome calendar) by which the hosts must have a CIN, e.g. <c>2026-03-01</c>; empty
    /// or missing = no date: the console and the alert show the obligation without a date. No default: the
    /// "01/03/2026 for existing operators" date was not found in any official source (RS-2,
    /// <c>.claude/context/regulations/cin.md</c>), so it is never presented as a legal deadline unless the product owner
    /// sets it.
    /// </summary>
    public string? ExposureDeadline { get; set; }

    /// <summary>
    /// Days before <see cref="ExposureDeadline"/> at which the hosts of properties without a valid CIN are alerted, once
    /// each (<c>Cin__AlertDaysBefore__0=30</c>, …); empty = <see cref="DefaultAlertDaysBefore"/>. The deadline day and
    /// the day after it always have their own alert. No default in the property itself: the configuration binder would
    /// append the configured values to it.
    /// </summary>
    public int[]? AlertDaysBefore { get; set; }

    /// <summary>
    /// <see cref="ExposureDeadline"/> as a date: null when it is not set. False when it is set but is not a
    /// <c>yyyy-MM-dd</c> date (the startup validation refuses it).
    /// </summary>
    public bool TryGetExposureDeadline(out DateOnly? deadline)
    {
        deadline = null;
        if (string.IsNullOrWhiteSpace(ExposureDeadline))
            return true;

        if (!DateOnly.TryParseExact(
                ExposureDeadline.Trim(), DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            return false;

        deadline = parsed;
        return true;
    }

    /// <summary>The configured deadline, or null when it is not set (or not a valid date: refused at startup).</summary>
    public DateOnly? GetExposureDeadline() => TryGetExposureDeadline(out var deadline) ? deadline : null;

    /// <summary>The alert thresholds in days, distinct and from the farthest to the nearest.</summary>
    public IReadOnlyList<int> GetAlertDaysBefore()
    {
        var configured = AlertDaysBefore is { Length: > 0 } ? AlertDaysBefore : DefaultAlertDaysBefore;
        return configured.Distinct().OrderDescending().ToList();
    }

    /// <summary>Configuration errors, empty when the section is valid.</summary>
    public IReadOnlyList<string> Validate()
    {
        var failures = new List<string>();
        if (!TryGetExposureDeadline(out _))
            failures.Add($"Cin__ExposureDeadline must be a date in the {DateFormat} format (e.g. 2026-03-01), or empty.");
        if (AlertDaysBefore is not null && AlertDaysBefore.Any(days => days is < 1 or > 366))
            failures.Add("Cin__AlertDaysBefore values must be days before the deadline between 1 and 366.");
        return failures;
    }
}

/// <summary>Refuses a <c>Cin</c> section that is not valid at startup, so a typo never hides or moves the deadline silently.</summary>
public sealed class CinOptionsValidator : IValidateOptions<CinOptions>
{
    public ValidateOptionsResult Validate(string? name, CinOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = options.Validate();
        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
