using Casazen.Core.Entities.Enums;
using Casazen.Core.Utilities;

namespace Casazen.Core.Suppliers;

// ─── What a supplier sends (read from the request, nothing checked yet) ──────────

/// <summary>A band of one day in <c>PUT api/supplier/availability/hours</c>: minutes after midnight, Europe/Rome wall clock.</summary>
public sealed record SupplierHoursBandInput(int? StartMinute, int? EndMinute);

/// <summary>A day of the week with its bands; no band is a rest day.</summary>
public sealed record SupplierHoursDayInput(DayOfWeek? Weekday, IReadOnlyList<SupplierHoursBandInput?>? Bands);

/// <summary>The weekly working hours: the days sent; a weekday that is not sent has no hours.</summary>
public sealed record SupplierHoursInput(IReadOnlyList<SupplierHoursDayInput?>? Days);

/// <summary>A time off to add: first and last day (both included), reason, optional label.</summary>
public sealed record SupplierTimeOffInput(DateOnly? FromDate, DateOnly? ToDate, SupplierTimeOffReason? Reason, string? Label);

/// <summary>A time off after <see cref="SupplierAgendaRules.NormalizeTimeOff"/>: checked, label trimmed.</summary>
public sealed record SupplierTimeOffContent(DateOnly FromDate, DateOnly ToDate, SupplierTimeOffReason Reason, string? Label);

/// <summary>A block or an extra opening to add: kind, first and last instant (UTC), optional label.</summary>
public sealed record SupplierBlockInput(SupplierBusyWindowKind? Kind, DateTime? StartUtc, DateTime? EndUtc, string? Label);

/// <summary>A block or an extra opening after <see cref="SupplierAgendaRules.NormalizeBlock"/>: checked, instants in UTC, label trimmed.</summary>
public sealed record SupplierBlockContent(SupplierBusyWindowKind Kind, DateTime StartUtc, DateTime EndUtc, string? Label);

/// <summary>The rules a supplier edits: every one of the five is needed (<c>PUT</c> replaces them).</summary>
public sealed record SupplierRulesInput(
    int? BufferMinutes,
    int? MaxJobsPerDay,
    int? MinNoticeHours,
    int? HorizonDays,
    int? SlotStepMinutes);

/// <summary>The rules after <see cref="SupplierAgendaRules.NormalizeRules"/>: all present and inside their limits.</summary>
public sealed record SupplierRulesContent(
    int BufferMinutes,
    int MaxJobsPerDay,
    int MinNoticeHours,
    int HorizonDays,
    int SlotStepMinutes);

/// <summary>
/// What is a valid value of the supplier's agenda (SP-03): the weekly hours, a time off, a block or an extra opening, the
/// rules. The limits are in <see cref="SupplierAgendaLimits"/>. Pure functions, testable without a database. Every value that
/// is not valid is collected and refused together: 422 <see cref="SupplierAgendaRuleException"/> naming all the fields at
/// fault (JSON names, a day or a band by its position).
/// </summary>
public static class SupplierAgendaRules
{
    /// <summary>
    /// Checks the weekly hours and returns the bands in Monday-first order, then by start. A rest day is a day with no band
    /// (or a weekday that is not sent). Refused (422 <see cref="SupplierAgendaErrors.HoursInvalid"/>): <c>days</c> missing or
    /// with more than 7 entries; a day that is missing, without a weekday, with a weekday that is not one or that is repeated;
    /// more than <see cref="SupplierAgendaLimits.MaxBandsPerDay"/> bands in a day; a start outside 0-1439; an end outside
    /// 1-1440 or not after its start; two bands of a day that overlap (bands that only touch are allowed: the planner joins
    /// them).
    /// </summary>
    public static IReadOnlyList<SupplierWeeklyBand> NormalizeHours(SupplierHoursInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (input.Days is null)
            throw SupplierAgendaErrors.InvalidHours([SupplierAgendaFields.Days]);

        var invalid = new List<string>();
        if (input.Days.Count > 7)
            AddOnce(invalid, SupplierAgendaFields.Days);

        var bands = new List<SupplierWeeklyBand>();
        var weekdays = new HashSet<DayOfWeek>();
        for (var i = 0; i < input.Days.Count; i++)
        {
            var day = input.Days[i];
            var dayPath = $"{SupplierAgendaFields.Days}[{i}]";
            if (day is null)
            {
                AddOnce(invalid, dayPath);
                continue;
            }

            var weekday = day.Weekday is { } named && Enum.IsDefined(named) ? named : (DayOfWeek?)null;
            if (weekday is null || !weekdays.Add(weekday.Value))
                AddOnce(invalid, $"{dayPath}.{SupplierAgendaFields.Weekday}");

            var source = day.Bands ?? [];
            if (source.Count > SupplierAgendaLimits.MaxBandsPerDay)
                AddOnce(invalid, $"{dayPath}.{SupplierAgendaFields.Bands}");

            var valid = new List<(int Start, int End, int Index)>();
            for (var j = 0; j < source.Count; j++)
            {
                var band = source[j];
                var bandPath = $"{dayPath}.{SupplierAgendaFields.Bands}[{j}]";
                if (band is null)
                {
                    AddOnce(invalid, bandPath);
                    continue;
                }

                var startOk = band.StartMinute is >= 0 and < SupplierAgendaLimits.MinutesPerDay;
                var endOk = band.EndMinute is > 0 and <= SupplierAgendaLimits.MinutesPerDay;
                if (!startOk)
                    AddOnce(invalid, $"{bandPath}.{SupplierAgendaFields.StartMinute}");
                if (!endOk || (startOk && band.EndMinute <= band.StartMinute))
                    AddOnce(invalid, $"{bandPath}.{SupplierAgendaFields.EndMinute}");
                else if (startOk)
                    valid.Add((band.StartMinute!.Value, band.EndMinute!.Value, j));
            }

            valid.Sort((a, b) => a.Start.CompareTo(b.Start));
            for (var k = 1; k < valid.Count; k++)
            {
                if (valid[k].Start < valid[k - 1].End)
                    AddOnce(invalid, $"{dayPath}.{SupplierAgendaFields.Bands}[{valid[k].Index}]");
            }

            if (weekday is { } known)
                bands.AddRange(valid.Select(band => new SupplierWeeklyBand(known, band.Start, band.End)));
        }

        if (invalid.Count > 0)
            throw SupplierAgendaErrors.InvalidHours(invalid);

        return bands
            .OrderBy(band => MondayFirst(band.Weekday))
            .ThenBy(band => band.StartMinute)
            .ToList();
    }

    /// <summary>
    /// Checks a time off against <paramref name="today"/> (the Europe/Rome calendar day). Refused (422
    /// <see cref="SupplierAgendaErrors.TimeOffInvalid"/>): a date that is missing; a last day before the first or more than
    /// <see cref="SupplierAgendaLimits.MaxTimeOffDays"/> days from it; a last day already over; a first day more than
    /// <see cref="SupplierAgendaLimits.MaxAdvanceDays"/> days ahead; a reason that is not one; a label of more than
    /// <see cref="SupplierAgendaLimits.LabelMaxLength"/> characters or with control characters. A reason that is left out is
    /// <see cref="SupplierTimeOffReason.Vacation"/>.
    /// </summary>
    public static SupplierTimeOffContent NormalizeTimeOff(SupplierTimeOffInput input, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(input);

        var invalid = new List<string>();
        if (input.FromDate is null)
            AddOnce(invalid, SupplierAgendaFields.FromDate);
        if (input.ToDate is null)
            AddOnce(invalid, SupplierAgendaFields.ToDate);

        if (input.FromDate is { } from && input.ToDate is { } to)
        {
            if (to < from || to.DayNumber - from.DayNumber + 1 > SupplierAgendaLimits.MaxTimeOffDays)
                AddOnce(invalid, SupplierAgendaFields.ToDate);
            if (to < today)
                AddOnce(invalid, SupplierAgendaFields.ToDate);
        }

        if (input.FromDate is { } first && first.DayNumber - today.DayNumber > SupplierAgendaLimits.MaxAdvanceDays)
            AddOnce(invalid, SupplierAgendaFields.FromDate);

        var reason = input.Reason ?? SupplierTimeOffReason.Vacation;
        if (!Enum.IsDefined(reason))
            AddOnce(invalid, SupplierAgendaFields.Reason);

        var label = OptionalLabel(input.Label, invalid);

        if (invalid.Count > 0)
            throw SupplierAgendaErrors.InvalidTimeOff(invalid);

        return new SupplierTimeOffContent(input.FromDate!.Value, input.ToDate!.Value, reason, label);
    }

    /// <summary>
    /// Checks a block or an extra opening against <paramref name="nowUtc"/>. Refused (422
    /// <see cref="SupplierAgendaErrors.BlockInvalid"/>): a kind that is missing or is not <c>Block</c> or <c>ExtraOpening</c>
    /// (the engagements of the calendar feed are written by the sync, not by the supplier); an instant that is missing; an
    /// end that is not after the start, a stretch shorter than <see cref="SupplierAgendaLimits.MinWindowMinutes"/> minutes,
    /// a block longer than <see cref="SupplierAgendaLimits.MaxBlockDays"/> days, an extra opening that is not inside one
    /// Europe/Rome day; an end already over; a start more than <see cref="SupplierAgendaLimits.MaxAdvanceDays"/> days ahead;
    /// a label that is too long or has control characters.
    /// </summary>
    public static SupplierBlockContent NormalizeBlock(SupplierBlockInput input, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(input);

        var now = UtcDateTime.Normalize(nowUtc);
        var invalid = new List<string>();

        var kind = input.Kind;
        if (kind is not (SupplierBusyWindowKind.Block or SupplierBusyWindowKind.ExtraOpening))
            AddOnce(invalid, SupplierAgendaFields.Kind);

        var start = input.StartUtc is { } rawStart ? UtcDateTime.Normalize(rawStart) : (DateTime?)null;
        var end = input.EndUtc is { } rawEnd ? UtcDateTime.Normalize(rawEnd) : (DateTime?)null;
        if (start is null)
            AddOnce(invalid, SupplierAgendaFields.StartUtc);
        if (end is null)
            AddOnce(invalid, SupplierAgendaFields.EndUtc);

        if (start is { } from && end is { } to)
        {
            var length = to - from;
            if (length < TimeSpan.FromMinutes(SupplierAgendaLimits.MinWindowMinutes))
                AddOnce(invalid, SupplierAgendaFields.EndUtc);
            else if (kind == SupplierBusyWindowKind.Block && length > TimeSpan.FromDays(SupplierAgendaLimits.MaxBlockDays))
                AddOnce(invalid, SupplierAgendaFields.EndUtc);
            else if (kind == SupplierBusyWindowKind.ExtraOpening && !IsInsideOneRomeDay(from, to))
                AddOnce(invalid, SupplierAgendaFields.EndUtc);

            if (to <= now)
                AddOnce(invalid, SupplierAgendaFields.EndUtc);
            if (from > now.AddDays(SupplierAgendaLimits.MaxAdvanceDays))
                AddOnce(invalid, SupplierAgendaFields.StartUtc);
        }

        var label = OptionalLabel(input.Label, invalid);

        if (invalid.Count > 0)
            throw SupplierAgendaErrors.InvalidBlock(invalid);

        return new SupplierBlockContent(kind!.Value, start!.Value, end!.Value, label);
    }

    /// <summary>
    /// Checks the five rules. Refused (422 <see cref="SupplierAgendaErrors.RulesInvalid"/>): a rule that is missing or outside
    /// its limits (<see cref="SupplierAgendaLimits"/>); the buffer and the slot step must also be multiples of
    /// <see cref="SupplierAgendaLimits.RuleGranularityMinutes"/> minutes.
    /// </summary>
    public static SupplierRulesContent NormalizeRules(SupplierRulesInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var invalid = new List<string>();
        var buffer = Bounded(
            input.BufferMinutes,
            SupplierAgendaLimits.MinBufferMinutes,
            SupplierAgendaLimits.MaxBufferMinutes,
            SupplierAgendaLimits.RuleGranularityMinutes,
            SupplierAgendaFields.BufferMinutes,
            invalid);
        var maxJobs = Bounded(
            input.MaxJobsPerDay,
            SupplierAgendaLimits.MinJobsPerDay,
            SupplierAgendaLimits.MaxJobsPerDayLimit,
            1,
            SupplierAgendaFields.MaxJobsPerDay,
            invalid);
        var notice = Bounded(
            input.MinNoticeHours,
            SupplierAgendaLimits.MinNoticeHours,
            SupplierAgendaLimits.MaxNoticeHours,
            1,
            SupplierAgendaFields.MinNoticeHours,
            invalid);
        var horizon = Bounded(
            input.HorizonDays,
            SupplierAgendaLimits.MinHorizonDays,
            SupplierAgendaLimits.MaxHorizonDays,
            1,
            SupplierAgendaFields.HorizonDays,
            invalid);
        var step = Bounded(
            input.SlotStepMinutes,
            SupplierAgendaLimits.MinSlotStepMinutes,
            SupplierAgendaLimits.MaxSlotStepMinutes,
            SupplierAgendaLimits.RuleGranularityMinutes,
            SupplierAgendaFields.SlotStepMinutes,
            invalid);

        if (invalid.Count > 0)
            throw SupplierAgendaErrors.InvalidRules(invalid);

        return new SupplierRulesContent(buffer, maxJobs, notice, horizon, step);
    }

    /// <summary>Position of a weekday in an Italian week: Monday 0 ... Sunday 6 (<see cref="DayOfWeek"/> starts on Sunday).</summary>
    public static int MondayFirst(DayOfWeek day) => ((int)day + 6) % 7;

    /// <summary>
    /// True when the stretch from <paramref name="startUtc"/> to <paramref name="endUtc"/> is inside the Europe/Rome calendar
    /// day of its start (it may end at the midnight that closes that day): an extra opening is a band of one day.
    /// </summary>
    public static bool IsInsideOneRomeDay(DateTime startUtc, DateTime endUtc)
    {
        var dayEnd = RomeCalendar.StartOfDayUtc(RomeCalendar.DateInRome(startUtc).AddDays(1));
        return endUtc <= dayEnd;
    }

    private static int Bounded(int? value, int min, int max, int multipleOf, string field, List<string> invalid)
    {
        if (value is { } number && number >= min && number <= max && number % multipleOf == 0)
            return number;

        AddOnce(invalid, field);
        return 0;
    }

    /// <summary>A label: trimmed, <c>null</c> when empty; refused when too long or with control characters (no line break either).</summary>
    private static string? OptionalLabel(string? value, List<string> invalid)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text))
            return null;

        if (text.Length > SupplierAgendaLimits.LabelMaxLength || text.Any(char.IsControl))
            AddOnce(invalid, SupplierAgendaFields.Label);
        return text;
    }

    private static void AddOnce(List<string> fields, string field)
    {
        if (!fields.Contains(field, StringComparer.Ordinal))
            fields.Add(field);
    }
}
