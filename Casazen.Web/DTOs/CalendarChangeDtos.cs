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
