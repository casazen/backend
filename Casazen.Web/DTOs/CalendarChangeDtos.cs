using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Microsoft.Extensions.Localization;

namespace Casazen.Web.DTOs;

/// <summary>
/// A change of the calendar sent to the channel manager (E08/F05/US02/T02, T03, NF-36): the process, its state (derived from its chunks, never «published»: sent is
/// «accepted by the channel manager») and the state of each property with its stable code. Dates are calendar dates (<c>yyyy-MM-dd</c>), instants are UTC, enums are
/// names. A value that is not set is absent, never <c>null</c>. No personal data. Definitions in <c>docs/TECHNICAL.md</c> («Calendar changes»).
/// </summary>
public sealed class CalendarChangeDto
{
    public Guid Id { get; init; }

    /// <summary><c>DayRules</c>, <c>Block</c>, <c>Bulk</c> or <c>Publication</c>.</summary>
    public CalendarChangeKind Kind { get; init; }

    /// <summary><c>Applied</c> (stays in CasaZen), <c>Queued</c>, <c>Sending</c>, <c>Sent</c> (accepted by the channel manager), <c>Confirmed</c>, <c>PartiallySent</c> or <c>Failed</c>.</summary>
    public CalendarChangeState State { get; init; }

    public DateTime RequestedAt { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? StartedAt { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? FinishedAt { get; init; }

    public int PropertyCount { get; init; }

    public int DayCount { get; init; }

    /// <summary>The units of quota of the channel manager the change is expected to use: an estimate.</summary>
    public int QuotaUnitsEstimated { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateOnly? From { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateOnly? To { get; init; }

    /// <summary>Why the process is not whole: a stable code (<c>octorate_quota_low</c>, <c>octorate_unavailable</c>, ...).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ErrorCode { get; init; }

    /// <summary>The sentence of <see cref="ErrorCode"/> in the language of the caller.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ErrorMessage { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? NextAttemptAt { get; init; }

    public IReadOnlyList<CalendarChangePropertyDto> Properties { get; init; } = [];

    public static CalendarChangeDto Of(CalendarChangeView view, IStringLocalizer localizer)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(localizer);

        return new CalendarChangeDto
        {
            Id = view.Id,
            Kind = view.Kind,
            State = view.State,
            RequestedAt = view.RequestedAt,
            StartedAt = view.StartedAt,
            FinishedAt = view.FinishedAt,
            PropertyCount = view.PropertyCount,
            DayCount = view.DayCount,
            QuotaUnitsEstimated = view.QuotaUnitsEstimated,
            From = view.From,
            To = view.To,
            ErrorCode = view.ErrorCode,
            ErrorMessage = MessageOf(view.ErrorCode, localizer),
            NextAttemptAt = view.NextAttemptAt,
            Properties = view.Properties.Select(p => CalendarChangePropertyDto.Of(p, localizer)).ToList(),
        };
    }

    /// <summary>The sentence of a stable code of <see cref="CalendarPushErrorCodes"/>; <c>null</c> for no code or one that has none.</summary>
    internal static string? MessageOf(string? code, IStringLocalizer localizer) =>
        CalendarPushErrorCodes.MessageKeyOf(code) is { } key ? localizer[key].Value : null;
}

/// <summary>One property of a change with its state.</summary>
public sealed class CalendarChangePropertyDto
{
    public Guid PropertyId { get; init; }

    /// <summary><c>Pending</c>, <c>Accepted</c>, <c>Confirmed</c>, <c>Failed</c> or <c>Skipped</c> (nothing is sent: the property is not on the channel manager).</summary>
    public CalendarChangePropertyState State { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ErrorCode { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ErrorMessage { get; init; }

    public int ChunkCount { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateOnly? From { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateOnly? To { get; init; }

    public static CalendarChangePropertyDto Of(CalendarChangePropertyView property, IStringLocalizer localizer)
    {
        ArgumentNullException.ThrowIfNull(property);

        return new CalendarChangePropertyDto
        {
            PropertyId = property.PropertyId,
            State = property.State,
            ErrorCode = property.ErrorCode,
            ErrorMessage = CalendarChangeDto.MessageOf(property.ErrorCode, localizer),
            ChunkCount = property.ChunkCount,
            From = property.From,
            To = property.To,
        };
    }
}

/// <summary>What to do to one value of the rules of the nights: <c>Keep</c> it (the default), <c>Set</c> it to <c>value</c>, or <c>Clear</c> it.</summary>
/// <typeparam name="T">The type of the value: a price, a number of nights or days, a flag.</typeparam>
public sealed class FieldPatchDto<T>
    where T : struct
{
    /// <summary><c>Keep</c>, <c>Set</c> or <c>Clear</c>.</summary>
    public FieldPatchKind Action { get; init; } = FieldPatchKind.Keep;

    /// <summary>The value to set; required with <c>Set</c>, ignored otherwise.</summary>
    public T? Value { get; init; }

    /// <summary>The patch; <c>false</c> for a <c>Set</c> without a value.</summary>
    public bool TryToPatch(out FieldPatch<T> patch)
    {
        switch (Action)
        {
            case FieldPatchKind.Set when Value is { } value:
                patch = FieldPatch<T>.Set(value);
                return true;
            case FieldPatchKind.Set:
                patch = FieldPatch<T>.Keep;
                return false;
            case FieldPatchKind.Clear:
                patch = FieldPatch<T>.Clear;
                return true;
            default:
                patch = FieldPatch<T>.Keep;
                return true;
        }
    }
}

/// <summary>
/// The body of <c>POST /api/calendar/changes/preview</c> and <c>POST /api/calendar/changes</c> (E08/F05/US02/T03): one to fifty properties, a period of at most 366 days
/// (both days included), optionally only some weekdays, and a <c>{ action, value }</c> for each value of the rules of the nights. A value nobody mentions is kept.
/// </summary>
public sealed class CalendarChangeRequestDto
{
    [Required]
    public List<Guid>? PropertyIds { get; init; }

    [Required]
    public DateOnly? From { get; init; }

    [Required]
    public DateOnly? To { get; init; }

    /// <summary>Only the nights of these weekdays; absent for every day. An empty list is refused, never read as «all».</summary>
    public List<DayOfWeek>? DaysOfWeek { get; init; }

    public FieldPatchDto<decimal>? Price { get; init; }

    public FieldPatchDto<int>? MinNights { get; init; }

    public FieldPatchDto<int>? MaxNights { get; init; }

    public FieldPatchDto<bool>? ClosedToArrival { get; init; }

    public FieldPatchDto<bool>? ClosedToDeparture { get; init; }

    public FieldPatchDto<int>? MinAdvanceDays { get; init; }

    public FieldPatchDto<int>? MaxAdvanceDays { get; init; }

    /// <summary>The request of the service; <c>false</c> when a <c>Set</c> has no value.</summary>
    public bool TryToRequest([NotNullWhen(true)] out CalendarChangeRequest? request)
    {
        request = null;
        if (!TryPatch(Price, out var price)
            || !TryPatch(MinNights, out var minNights)
            || !TryPatch(MaxNights, out var maxNights)
            || !TryPatch(ClosedToArrival, out var closedToArrival)
            || !TryPatch(ClosedToDeparture, out var closedToDeparture)
            || !TryPatch(MinAdvanceDays, out var minAdvanceDays)
            || !TryPatch(MaxAdvanceDays, out var maxAdvanceDays))
        {
            return false;
        }

        request = new CalendarChangeRequest(
            PropertyIds ?? [],
            From!.Value,
            To!.Value,
            DaysOfWeek is null ? null : new HashSet<DayOfWeek>(DaysOfWeek),
            new DayRulePatch(price, minNights, maxNights, closedToArrival, closedToDeparture, minAdvanceDays, maxAdvanceDays));
        return true;
    }

    private static bool TryPatch<T>(FieldPatchDto<T>? dto, out FieldPatch<T> patch)
        where T : struct
    {
        if (dto is null)
        {
            patch = FieldPatch<T>.Keep;
            return true;
        }

        return dto.TryToPatch(out patch);
    }
}

/// <summary>What a night holds: the price a guest is quoted and the rules in force (the same members as a day of <c>GET /api/calendar/days</c>).</summary>
public sealed class CalendarChangeValuesDto
{
    public long PriceCents { get; init; }

    public int MinNights { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MaxNights { get; init; }

    public bool ClosedToArrival { get; init; }

    public bool ClosedToDeparture { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MinAdvanceDays { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MaxAdvanceDays { get; init; }

    public static CalendarChangeValuesDto From(CalendarChangeValues values)
    {
        ArgumentNullException.ThrowIfNull(values);

        return new CalendarChangeValuesDto
        {
            PriceCents = values.PriceCents,
            MinNights = values.MinNights,
            MaxNights = values.MaxNights,
            ClosedToArrival = values.ClosedToArrival,
            ClosedToDeparture = values.ClosedToDeparture,
            MinAdvanceDays = values.MinAdvanceDays,
            MaxAdvanceDays = values.MaxAdvanceDays,
        };
    }
}

/// <summary>A night the change would change, with its values before and after.</summary>
public sealed class CalendarChangeSampleDto
{
    public DateOnly Date { get; init; }

    public CalendarChangeValuesDto Before { get; init; } = new();

    public CalendarChangeValuesDto After { get; init; } = new();
}

/// <summary>What the change would do to one property.</summary>
public sealed class CalendarChangePreviewPropertyDto
{
    public Guid PropertyId { get; init; }

    public string Name { get; init; } = string.Empty;

    /// <summary>The property books into the channel manager: the change would be sent there.</summary>
    public bool Mirrored { get; init; }

    public int Days { get; init; }

    public int ChangedDays { get; init; }

    public int UnchangedDays { get; init; }

    /// <summary>Nights taken by a booking, a hold, a request or a block, counted and not skipped: a rule on a booked night changes nothing for that booking.</summary>
    public int TakenDays { get; init; }

    public int SkippedPastDays { get; init; }

    /// <summary>The first five changed nights with their values before and after.</summary>
    public IReadOnlyList<CalendarChangeSampleDto> Samples { get; init; } = [];

    /// <summary>An estimate of the units of quota of the channel manager the change would use.</summary>
    public int EstimatedQuotaUnits { get; init; }
}

/// <summary>Something to know before applying: a stable code, the property and the sentence in the language of the caller.</summary>
public sealed class CalendarChangeWarningDto
{
    /// <summary><c>property_not_mirrored</c> (the change stays in CasaZen) or <c>room_not_chosen</c> (nothing is sent until the room is chosen).</summary>
    public string Code { get; init; } = string.Empty;

    public Guid PropertyId { get; init; }

    public string Message { get; init; } = string.Empty;
}

/// <summary>The answer of <c>POST /api/calendar/changes/preview</c>: nothing was changed, and the quota is an estimate.</summary>
public sealed class CalendarChangePreviewDto
{
    public IReadOnlyList<CalendarChangePreviewPropertyDto> Properties { get; init; } = [];

    public int Days { get; init; }

    public int ChangedDays { get; init; }

    public int UnchangedDays { get; init; }

    public int TakenDays { get; init; }

    public int SkippedPastDays { get; init; }

    public int EstimatedQuotaUnits { get; init; }

    public IReadOnlyList<CalendarChangeWarningDto> Warnings { get; init; } = [];

    public static CalendarChangePreviewDto Of(CalendarChangePreview preview, IStringLocalizer localizer)
    {
        ArgumentNullException.ThrowIfNull(preview);
        ArgumentNullException.ThrowIfNull(localizer);

        return new CalendarChangePreviewDto
        {
            Properties = preview.Properties
                .Select(p => new CalendarChangePreviewPropertyDto
                {
                    PropertyId = p.PropertyId,
                    Name = p.Name,
                    Mirrored = p.Mirrored,
                    Days = p.Days,
                    ChangedDays = p.ChangedDays,
                    UnchangedDays = p.UnchangedDays,
                    TakenDays = p.TakenDays,
                    SkippedPastDays = p.SkippedPastDays,
                    Samples = p.Samples
                        .Select(s => new CalendarChangeSampleDto
                        {
                            Date = s.Date,
                            Before = CalendarChangeValuesDto.From(s.Before),
                            After = CalendarChangeValuesDto.From(s.After),
                        })
                        .ToList(),
                    EstimatedQuotaUnits = p.EstimatedQuotaUnits,
                })
                .ToList(),
            Days = preview.Days,
            ChangedDays = preview.ChangedDays,
            UnchangedDays = preview.UnchangedDays,
            TakenDays = preview.TakenDays,
            SkippedPastDays = preview.SkippedPastDays,
            EstimatedQuotaUnits = preview.EstimatedQuotaUnits,
            Warnings = preview.Warnings
                .Select(w => new CalendarChangeWarningDto
                {
                    Code = w.Code,
                    PropertyId = w.PropertyId,
                    Message = CalendarChangeDto.MessageOf(w.Code, localizer) ?? w.Code,
                })
                .ToList(),
        };
    }
}
