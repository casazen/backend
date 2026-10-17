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

// ─── Conflicts ───────────────────────────────────────────────────────────────

/// <summary>One conflict of a job: a stable code, how serious it is, and what it is about when it is about another thing.</summary>
public class SupplierConflictDto
{
    /// <summary>
    /// <c>time_off</c>, <c>overlap</c>, <c>external</c>, <c>block</c> (danger) or <c>day_off</c>, <c>outside_hours</c>,
    /// <c>day_full</c>, <c>buffer</c> (warning). Stable: the frontend branches on it and writes the text.
    /// </summary>
    public string Code { get; set; } = string.Empty;

    /// <summary><c>danger</c> (the time cannot be kept as it is) or <c>warning</c> (the supplier may decide to live with it).</summary>
    public string Severity { get; set; } = string.Empty;

    /// <summary>
    /// The other request in the way (<c>overlap</c>, <c>buffer</c>): its id, the id of its entry in <c>requests</c> of
    /// <c>GET api/supplier/calendar</c>. The calendar lists a request on the Europe/Rome day it starts: a request that lies across the
    /// edge of the range (a pause or a job over midnight reaches into the next day) is in the calendar of the day before or after, so
    /// read the calendar one day more on each side of the range.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? OtherRequestId { get; set; }

    /// <summary>
    /// The block or engagement of the supplier's calendar in the way (<c>block</c>, <c>external</c>, <c>buffer</c>): the id of its
    /// entry in <c>blocks</c> of <c>GET api/supplier/calendar</c>, which lists the windows that overlap the range: one that is only
    /// close to the job (<c>buffer</c>) can be just outside it, so read the calendar one day more on each side of the range.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? WindowId { get; set; }
}

/// <summary>A job of the supplier that has conflicts.</summary>
public class SupplierRequestConflictsDto
{
    /// <summary>The service request (the id of its entry in <c>requests</c> of <c>GET api/supplier/calendar</c>).</summary>
    public Guid RequestId { get; set; }

    /// <summary><c>Richiesto</c>, <c>PresoInCarico</c> or <c>InCorso</c>: the only statuses that are checked.</summary>
    public ServiceRequestStatus Status { get; set; }

    /// <summary>The Europe/Rome day of the job.</summary>
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

/// <summary>Body of <c>GET api/supplier/calendar/conflicts</c>: the jobs to come with something wrong, by start.</summary>
public class SupplierConflictsResponse
{
    public DateOnly From { get; set; }

    public DateOnly To { get; set; }

    /// <summary>The time zone of the days: <c>Europe/Rome</c>.</summary>
    public string TimeZone { get; set; } = RomeCalendar.TimeZoneId;

    /// <summary>The jobs that have at least one conflict; a job with none is not listed.</summary>
    public IReadOnlyList<SupplierRequestConflictsDto> Items { get; set; } = [];

    /// <summary>How many jobs are in <see cref="Items"/>.</summary>
    public int Total { get; set; }

    /// <summary>True when the range held more jobs than the check looks at (<c>limit</c>): only the nearest were checked.</summary>
    public bool Truncated { get; set; }

    /// <summary>The most jobs one answer checks.</summary>
    public int Limit { get; set; } = SupplierAgendaLimits.MaxConflictRequests;
}

// ─── Preview ─────────────────────────────────────────────────────────────────

/// <summary>Whether the comune asked about is one of the supplier's zones (decision D10: zones are comuni).</summary>
public class SupplierPreviewCoverageDto
{
    /// <summary><c>inside</c> (the supplier covers the comune) or <c>outside</c> (it does not, or the comune could not be recognized).</summary>
    public string Outcome { get; set; } = string.Empty;

    /// <summary>The comune as asked (an ISTAT code or a name), trimmed.</summary>
    public string Comune { get; set; } = string.Empty;
}

/// <summary>
/// Body of <c>GET api/supplier/preview/slots</c>: the slots of one service as the supplier's customers would see them: the same
/// shape as the public page (<see cref="PublicSlotsResponse"/>: <c>durationMinutes</c>, <c>timeZone</c>, <c>bookableUntil</c>,
/// <c>days</c>), with what only the supplier needs added. Never cached.
/// </summary>
public class SupplierSlotPreviewResponse : PublicSlotsResponse
{
    /// <summary>The service the slots are for.</summary>
    public Guid ServiceId { get; set; }

    /// <summary><c>Draft</c>, <c>Active</c> or <c>Paused</c>: the preview also works for a service that is not public.</summary>
    public SupplierServiceListingStatus ServiceStatus { get; set; }

    /// <summary>Only when <c>comune</c> was asked: whether it is one of the supplier's zones.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SupplierPreviewCoverageDto? Coverage { get; set; }
}

// ─── Mapping ─────────────────────────────────────────────────────────────────

/// <summary>Maps the reads of the Portal's agenda (SP-03b) to their answers.</summary>
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
        // The days are the public ones, and so is their shape: the same mapper as the public page.
        var days = PublicSupplierMapper.ToDto(new PublicSlots(preview.ServiceSlug, preview.DurationMinutes, preview.BookableUntil, preview.Days));
        return new SupplierSlotPreviewResponse
        {
            Service = days.Service,
            DurationMinutes = days.DurationMinutes,
            TimeZone = days.TimeZone,
            BookableUntil = days.BookableUntil,
            Days = days.Days,
            ServiceId = preview.ServiceId,
            ServiceStatus = preview.ServiceStatus,
            Coverage = preview.Coverage is { } coverage
                ? new SupplierPreviewCoverageDto { Outcome = coverage.Inside ? "inside" : "outside", Comune = coverage.Comune }
                : null,
        };
    }
}
