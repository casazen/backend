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

    public CalendarChangeKind Kind { get; init; }

    public CalendarChangeState State { get; init; }

    public DateTime RequestedAt { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? StartedAt { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? FinishedAt { get; init; }

    public int PropertyCount { get; init; }

    public int DayCount { get; init; }

    public int QuotaUnitsEstimated { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateOnly? From { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateOnly? To { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ErrorCode { get; init; }

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

    internal static string? MessageOf(string? code, IStringLocalizer localizer) =>
        CalendarPushErrorCodes.MessageKeyOf(code) is { } key ? localizer[key].Value : null;
}

public sealed class CalendarChangePropertyDto
{
    public Guid PropertyId { get; init; }

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

public sealed class FieldPatchDto<T>
    where T : struct
{
    public FieldPatchKind Action { get; init; } = FieldPatchKind.Keep;

    /// <summary>The value to set; required with <c>Set</c>, ignored otherwise.</summary>
    public T? Value { get; init; }

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

public sealed class CalendarChangeRequestDto
{
    [Required]
    public List<Guid>? PropertyIds { get; init; }

    [Required]
    public DateOnly? From { get; init; }

    [Required]
    public DateOnly? To { get; init; }

    public List<DayOfWeek>? DaysOfWeek { get; init; }

    public FieldPatchDto<decimal>? Price { get; init; }

    public FieldPatchDto<int>? MinNights { get; init; }

    public FieldPatchDto<int>? MaxNights { get; init; }

    public FieldPatchDto<bool>? ClosedToArrival { get; init; }

    public FieldPatchDto<bool>? ClosedToDeparture { get; init; }

    public FieldPatchDto<int>? MinAdvanceDays { get; init; }

    public FieldPatchDto<int>? MaxAdvanceDays { get; init; }

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

public sealed class CalendarChangeSampleDto
{
    public DateOnly Date { get; init; }

    public CalendarChangeValuesDto Before { get; init; } = new();

    public CalendarChangeValuesDto After { get; init; } = new();
}

public sealed class CalendarChangePreviewPropertyDto
{
    public Guid PropertyId { get; init; }

    public string Name { get; init; } = string.Empty;

    public bool Mirrored { get; init; }

    public int Days { get; init; }

    public int ChangedDays { get; init; }

    public int UnchangedDays { get; init; }

    public int TakenDays { get; init; }

    public int SkippedPastDays { get; init; }

    public IReadOnlyList<CalendarChangeSampleDto> Samples { get; init; } = [];

    public int EstimatedQuotaUnits { get; init; }
}

public sealed class CalendarChangeWarningDto
{
    public string Code { get; init; } = string.Empty;

    public Guid PropertyId { get; init; }

    public string Message { get; init; } = string.Empty;
}

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
