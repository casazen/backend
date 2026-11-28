using System.Text.Json.Serialization;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Core.Utilities;

namespace Casazen.Web.DTOs.Supplier;

// The reads that the Portal's agenda adds to the supplier's console (SP-03b): the conflicts of the jobs to come with a solution,
// and the preview "how customers see you". Nothing here carries a text meant to be read (the frontend writes them, i18n): codes,
// ids, instants. A person's name, an address or a note never reaches these answers; the conflicts name the other request or window
// by id and the calendar (GET api/supplier/calendar) is where its title is.

public class SupplierConflictDto
{
    /// <summary>
    /// <c>time_off</c>, <c>overlap</c>, <c>external</c>, <c>block</c> (danger) or <c>day_off</c>, <c>outside_hours</c>,
    /// <c>day_full</c>, <c>buffer</c> (warning). Stable: the frontend branches on it and writes the text.
    /// </summary>
    public string Code { get; set; } = string.Empty;

    /// <summary><c>danger</c> (the time cannot be kept as it is) or <c>warning</c> (the supplier may decide to live with it).</summary>
    public string Severity { get; set; } = string.Empty;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? OtherRequestId { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? WindowId { get; set; }
}

public class SupplierRequestConflictsDto
{
    public Guid RequestId { get; set; }

    public ServiceRequestStatus Status { get; set; }

    public DateOnly Date { get; set; }

    public DateTime StartUtc { get; set; }

    public DateTime EndUtc { get; set; }

    /// <summary>The conflicts, the most serious first (danger before warning); at most one per code.</summary>
    public IReadOnlyList<SupplierConflictDto> Conflicts { get; set; } = [];

    /// <summary>
    /// A time to propose instead (<c>POST api/service-requests/{id}/propose-time</c> with this slot's <c>startUtc</c>): the first free
    /// slot as long as the job, from its day on, as the planner offers it, in the shape of a slot of the public page. Only for a
    /// request still <c>Richiesto</c> (the only one a time can be proposed on) and only when a slot is free within 21 days of its day
    /// (and inside the supplier's horizon); <c>null</c> otherwise.
    /// </summary>
    public PublicSlotDto? SuggestedSlot { get; set; }
}

public class SupplierConflictsResponse
{
    public DateOnly From { get; set; }

    public DateOnly To { get; set; }

    public string TimeZone { get; set; } = RomeCalendar.TimeZoneId;

    public IReadOnlyList<SupplierRequestConflictsDto> Items { get; set; } = [];

    public int Total { get; set; }

    public bool Truncated { get; set; }

    public int Limit { get; set; } = SupplierAgendaLimits.MaxConflictRequests;
}

public class SupplierPreviewCoverageDto
{
    public string Outcome { get; set; } = string.Empty;

    public string Comune { get; set; } = string.Empty;
}

/// <summary>
/// Body of <c>GET api/supplier/preview/slots</c>: the slots of one service as the supplier's customers would see them: the same
/// shape as the public page (<see cref="PublicSlotsResponse"/>: <c>durationMinutes</c>, <c>timeZone</c>, <c>bookableUntil</c>,
/// <c>days</c>), with what only the supplier needs added. Never cached.
/// </summary>
public class SupplierSlotPreviewResponse : PublicSlotsResponse
{
    public Guid ServiceId { get; set; }

    public SupplierServiceListingStatus ServiceStatus { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SupplierPreviewCoverageDto? Coverage { get; set; }
}

public static class SupplierAgendaReadsMapper
{
    public static SupplierConflictsResponse ToDto(SupplierConflictReport report) =>
        new()
        {
            From = report.From,
            To = report.To,
            Items = report.Items.Select(ToDto).ToList(),
            Total = report.Items.Count,
            Truncated = report.Truncated,
        };

    public static SupplierRequestConflictsDto ToDto(SupplierRequestConflicts item) =>
        new()
        {
            RequestId = item.RequestId,
            Status = item.Status,
            Date = item.Date,
            StartUtc = UtcDateTime.Normalize(item.StartUtc),
            EndUtc = UtcDateTime.Normalize(item.EndUtc),
            Conflicts = item.Conflicts
                .Select(conflict => new SupplierConflictDto
                {
                    Code = conflict.Code,
                    Severity = conflict.Severity,
                    OtherRequestId = conflict.OtherRequestId,
                    WindowId = conflict.WindowId,
                })
                .ToList(),
            SuggestedSlot = item.SuggestedSlot is { } slot
                ? new PublicSlotDto
                {
                    StartUtc = UtcDateTime.Normalize(slot.StartUtc),
                    EndUtc = UtcDateTime.Normalize(slot.EndUtc),
                    StartLocal = RomeCalendar.ToRome(slot.StartUtc),
                    EndLocal = RomeCalendar.ToRome(slot.EndUtc),
                }
                : null,
        };

    public static SupplierSlotPreviewResponse ToDto(SupplierSlotPreview preview)
    {
        var days = PublicSupplierMapper.ToDto(
            new PublicSlots(preview.ServiceSlug, preview.DurationMinutes, preview.BookableUntil, preview.Days, preview.Urgent));
        return new SupplierSlotPreviewResponse
        {
            Service = days.Service,
            DurationMinutes = days.DurationMinutes,
            TimeZone = days.TimeZone,
            BookableUntil = days.BookableUntil,
            Urgent = days.Urgent,
            Days = days.Days,
            ServiceId = preview.ServiceId,
            ServiceStatus = preview.ServiceStatus,
            Coverage = preview.Coverage is { } coverage
                ? new SupplierPreviewCoverageDto { Outcome = coverage.Inside ? "inside" : "outside", Comune = coverage.Comune }
                : null,
        };
    }
}
