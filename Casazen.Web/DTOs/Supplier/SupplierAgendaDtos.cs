using System.ComponentModel.DataAnnotations;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;

namespace Casazen.Web.DTOs.Supplier;

// ─── Requests ────────────────────────────────────────────────────────────────
//
// The attributes only stop an absurd body (a list or a text far beyond what is allowed: 400 validation_error). Every value is
// checked by SupplierAgendaRules (422, with the fields at fault): a fourth band, an end before its start, a label of 81
// characters are 422, not 400. A value left out is a missing value (422 naming the field), never a silent default.

/// <summary>A band of one day in <c>PUT api/supplier/availability/hours</c>: minutes after midnight on the wall clock of Rome.</summary>
public class SupplierHoursBandRequest
{
    /// <summary>When the band starts, in minutes after midnight (0 to 1439): 540 is 09:00.</summary>
    public int? StartMinute { get; set; }

    /// <summary>When the band ends, in minutes after midnight, after the start (up to 1440, midnight): 1080 is 18:00.</summary>
    public int? EndMinute { get; set; }
}

/// <summary>A day of the week of <c>PUT api/supplier/availability/hours</c> with its bands (up to 3); no band is a rest day.</summary>
public class SupplierHoursDayRequest
{
    /// <summary><c>Monday</c> ... <c>Sunday</c>; each weekday at most once.</summary>
    public DayOfWeek? Weekday { get; set; }

    [MaxLength(10)]
    public List<SupplierHoursBandRequest?>? Bands { get; set; }
}

/// <summary>Body of <c>PUT api/supplier/availability/hours</c>: the whole week. A weekday that is not in it becomes a rest day.</summary>
public class SaveSupplierHoursRequest
{
    [MaxLength(14)]
    public List<SupplierHoursDayRequest?>? Days { get; set; }
}

/// <summary>Body of <c>POST api/supplier/availability/time-off</c>.</summary>
public class CreateSupplierTimeOffRequest
{
    /// <summary>First closed day, <c>yyyy-MM-dd</c> (a calendar day of Rome).</summary>
    public DateOnly? FromDate { get; set; }

    /// <summary>Last closed day, not before the first (the same day for a single day off), at most 366 days in all.</summary>
    public DateOnly? ToDate { get; set; }

    /// <summary><c>Vacation</c> (default), <c>Holiday</c>, <c>Illness</c> or <c>Other</c>: only a label for the supplier.</summary>
    public SupplierTimeOffReason? Reason { get; set; }

    /// <summary>A name for the console ("Ferie (ponte di Ognissanti)"), at most 80 characters; never public.</summary>
    [MaxLength(400)]
    public string? Label { get; set; }
}

/// <summary>Body of <c>POST api/supplier/availability/blocks</c>: hours the supplier blocks, or opens on top of the weekly hours.</summary>
public class CreateSupplierBlockRequest
{
    /// <summary><c>Block</c> or <c>ExtraOpening</c> (the engagements of the calendar feed are not created by the supplier).</summary>
    public SupplierBusyWindowKind? Kind { get; set; }

    /// <summary>First instant, in UTC (ISO 8601, with <c>Z</c> or an offset).</summary>
    public DateTime? StartUtc { get; set; }

    /// <summary>Instant it ends, in UTC, at least 15 minutes after the start; an extra opening stays inside one day of Rome.</summary>
    public DateTime? EndUtc { get; set; }

    /// <summary>A name for the console ("Dentista"), at most 80 characters; never public.</summary>
    [MaxLength(400)]
    public string? Label { get; set; }
}

/// <summary>
/// Body of <c>PUT api/supplier/availability/rules</c>: the five rules, all of them (<c>PUT</c> replaces them; one that is
/// left out is a 422 naming it, never reset to its default).
/// </summary>
public class SaveSupplierRulesRequest
{
    /// <summary>Minutes kept free before and after every job (0 to 240, multiple of 5; default 30).</summary>
    public int? BufferMinutes { get; set; }

    /// <summary>Jobs a day at most (1 to 50; default 3): the day then shows as full.</summary>
    public int? MaxJobsPerDay { get; set; }

    /// <summary>Shortest notice in hours between a booking and its work (0 to 720; default 24).</summary>
    public int? MinNoticeHours { get; set; }

    /// <summary>How many days ahead of today a customer can book (1 to 365; default 35).</summary>
    public int? HorizonDays { get; set; }

    /// <summary>Minutes between two slots in the same working band (15 to 240, multiple of 5; default 60).</summary>
    public int? SlotStepMinutes { get; set; }
}

// ─── Responses ───────────────────────────────────────────────────────────────

/// <summary>A band of working hours: minutes after midnight on the wall clock of Rome.</summary>
public class SupplierHoursBandDto
{
    public int StartMinute { get; set; }

    public int EndMinute { get; set; }
}

/// <summary>A weekday with its bands (in start order); no band is a rest day.</summary>
public class SupplierHoursDayDto
{
    /// <summary><c>Monday</c> ... <c>Sunday</c>.</summary>
    public DayOfWeek Weekday { get; set; }

    public IReadOnlyList<SupplierHoursBandDto> Bands { get; set; } = [];
}

/// <summary>Body of <c>GET</c> and <c>PUT api/supplier/availability/hours</c>: the whole week, Monday first (seven days, rest days included).</summary>
public class SupplierHoursDto
{
    public IReadOnlyList<SupplierHoursDayDto> Days { get; set; } = [];

    /// <summary>When the supplier last saved hours with at least one band; <c>null</c> while it has none.</summary>
    public DateTime? ConfiguredAt { get; set; }
}

/// <summary>A time off.</summary>
public class SupplierTimeOffDto
{
    public Guid Id { get; set; }

    public DateOnly FromDate { get; set; }

    public DateOnly ToDate { get; set; }

    /// <summary><c>Vacation</c>, <c>Holiday</c>, <c>Illness</c> or <c>Other</c>.</summary>
    public SupplierTimeOffReason Reason { get; set; }

    public string? Label { get; set; }

    public DateTime CreatedAt { get; set; }
}

/// <summary>Body of <c>GET api/supplier/availability/time-off</c>: the time off that has not ended yet, by first day.</summary>
public class SupplierTimeOffListResponse
{
    public IReadOnlyList<SupplierTimeOffDto> Items { get; set; } = [];

    public int Total { get; set; }

    /// <summary>How many time off entries a supplier can have that have not ended (<c>total</c> at the limit: no more can be added).</summary>
    public int Limit { get; set; } = SupplierAgendaLimits.MaxTimeOffEntries;
}

/// <summary>A block, an extra opening or an engagement of the supplier's calendar feed.</summary>
public class SupplierBlockDto
{
    public Guid Id { get; set; }

    /// <summary><c>Block</c>, <c>ExtraOpening</c> or <c>External</c> (an engagement of the calendar feed).</summary>
    public SupplierBusyWindowKind Kind { get; set; }

    /// <summary><c>Manual</c> (the supplier) or <c>ICalFeed</c> (the sync): only the manual ones can be deleted.</summary>
    public SupplierBusyWindowSource Source { get; set; }

    public DateTime StartUtc { get; set; }

    public DateTime EndUtc { get; set; }

    /// <summary>Only the supplier's console shows it, never a public page.</summary>
    public string? Label { get; set; }

    public DateTime CreatedAt { get; set; }
}

/// <summary>Body of <c>GET api/supplier/availability/blocks</c>: the blocks and extra openings set by hand that have not ended yet, by start.</summary>
public class SupplierBlockListResponse
{
    public IReadOnlyList<SupplierBlockDto> Items { get; set; } = [];

    public int Total { get; set; }

    /// <summary>How many blocks and extra openings a supplier can have that have not ended.</summary>
    public int Limit { get; set; } = SupplierAgendaLimits.MaxManualWindows;
}

/// <summary>Body of <c>GET</c> and <c>PUT api/supplier/availability/rules</c>: the five rules (the defaults while none was saved).</summary>
public class SupplierRulesDto
{
    public int BufferMinutes { get; set; }

    public int MaxJobsPerDay { get; set; }

    public int MinNoticeHours { get; set; }

    public int HorizonDays { get; set; }

    public int SlotStepMinutes { get; set; }
}

/// <summary>A day closed by hand or by the supplier's calendar feed.</summary>
public class SupplierClosedDayDto
{
    public DateOnly Date { get; set; }

    /// <summary><c>Manual</c> (the supplier) or <c>ICalFeed</c> (an event of the calendar feed).</summary>
    public SupplierAvailabilitySource Source { get; set; }
}

/// <summary>
/// A service request as a whole-day item of the calendar (its time arrives with SP-04). No property, address or contact:
/// those are in the request's detail, and only after the supplier took it.
/// </summary>
public class SupplierCalendarRequestDto
{
    public Guid Id { get; set; }

    /// <summary>The Europe/Rome day of the work (the check-out day of the stay of a short-rent request).</summary>
    public DateOnly Date { get; set; }

    /// <summary><c>Richiesto</c> (to confirm), <c>PresoInCarico</c>, <c>InCorso</c>, <c>Completato</c> or <c>Pagato</c>.</summary>
    public ServiceRequestStatus Status { get; set; }

    /// <summary>A code of <c>GET /api/service-categories</c>.</summary>
    public string Category { get; set; } = string.Empty;
}

/// <summary>Body of <c>GET api/supplier/calendar</c>: what the supplier's calendar shows for the days asked.</summary>
public class SupplierCalendarDto
{
    public DateOnly From { get; set; }

    public DateOnly To { get; set; }

    /// <summary>The time zone of the hours and of the days: <c>Europe/Rome</c>.</summary>
    public string TimeZone { get; set; } = string.Empty;

    /// <summary>The weekly working hours, Monday first (seven days, rest days included).</summary>
    public IReadOnlyList<SupplierHoursDayDto> WorkingHours { get; set; } = [];

    /// <summary>The days in the range closed by hand or by the calendar feed.</summary>
    public IReadOnlyList<SupplierClosedDayDto> ClosedDays { get; set; } = [];

    /// <summary>The time off that touches the range.</summary>
    public IReadOnlyList<SupplierTimeOffDto> TimeOff { get; set; } = [];

    /// <summary>The blocks, extra openings and calendar engagements that touch the range, by start.</summary>
    public IReadOnlyList<SupplierBlockDto> Blocks { get; set; } = [];

    /// <summary>The service requests with a day in the range, as whole-day items.</summary>
    public IReadOnlyList<SupplierCalendarRequestDto> Requests { get; set; } = [];
}

/// <summary>Maps the agenda rows to their responses and the requests to the input of the rules.</summary>
public static class SupplierAgendaMapper
{
    private static readonly DayOfWeek[] MondayFirst =
    [
        DayOfWeek.Monday,
        DayOfWeek.Tuesday,
        DayOfWeek.Wednesday,
        DayOfWeek.Thursday,
        DayOfWeek.Friday,
        DayOfWeek.Saturday,
        DayOfWeek.Sunday,
    ];

    /// <summary>The week as seven days, Monday first: a weekday with no band is a rest day with an empty list.</summary>
    public static IReadOnlyList<SupplierHoursDayDto> ToWeek(IEnumerable<SupplierWeeklyBand> bands)
    {
        var byDay = bands.ToLookup(band => band.Weekday);
        return MondayFirst
            .Select(day => new SupplierHoursDayDto
            {
                Weekday = day,
                Bands = byDay[day]
                    .OrderBy(band => band.StartMinute)
                    .Select(band => new SupplierHoursBandDto { StartMinute = band.StartMinute, EndMinute = band.EndMinute })
                    .ToList(),
            })
            .ToList();
    }

    public static SupplierHoursDto ToDto(SupplierHours hours) =>
        new() { Days = ToWeek(hours.Bands), ConfiguredAt = hours.ConfiguredAt };

    public static SupplierHoursInput ToInput(SaveSupplierHoursRequest request) =>
        new(request.Days?
            .Select(day => day is null
                ? null
                : new SupplierHoursDayInput(
                    day.Weekday,
                    day.Bands?
                        .Select(band => band is null ? null : new SupplierHoursBandInput(band.StartMinute, band.EndMinute))
                        .ToList()))
            .ToList());

    public static SupplierTimeOffDto ToDto(SupplierTimeOff entry) =>
        new()
        {
            Id = entry.Id,
            FromDate = entry.FromDate,
            ToDate = entry.ToDate,
            Reason = entry.Reason,
            Label = entry.Label,
            CreatedAt = entry.CreatedAt,
        };

    public static SupplierTimeOffInput ToInput(CreateSupplierTimeOffRequest request) =>
        new(request.FromDate, request.ToDate, request.Reason, request.Label);

    public static SupplierBlockDto ToDto(SupplierBusyWindow window) =>
        new()
        {
            Id = window.Id,
            Kind = window.Kind,
            Source = window.Source,
            StartUtc = window.StartUtc,
            EndUtc = window.EndUtc,
            Label = window.Label,
            CreatedAt = window.CreatedAt,
        };

    public static SupplierBlockInput ToInput(CreateSupplierBlockRequest request) =>
        new(request.Kind, request.StartUtc, request.EndUtc, request.Label);

    public static SupplierRulesDto ToDto(SupplierPlanningRules rules) =>
        new()
        {
            BufferMinutes = rules.BufferMinutes,
            MaxJobsPerDay = rules.MaxJobsPerDay,
            MinNoticeHours = rules.MinNoticeHours,
            HorizonDays = rules.HorizonDays,
            SlotStepMinutes = rules.SlotStepMinutes,
        };

    public static SupplierRulesInput ToInput(SaveSupplierRulesRequest request) =>
        new(request.BufferMinutes, request.MaxJobsPerDay, request.MinNoticeHours, request.HorizonDays, request.SlotStepMinutes);

    public static SupplierCalendarDto ToDto(SupplierCalendar calendar) =>
        new()
        {
            From = calendar.From,
            To = calendar.To,
            TimeZone = Casazen.Core.Utilities.RomeCalendar.TimeZoneId,
            WorkingHours = ToWeek(calendar.WorkingHours),
            ClosedDays = calendar.ClosedDays
                .Select(day => new SupplierClosedDayDto { Date = day.Date, Source = day.Source })
                .ToList(),
            TimeOff = calendar.TimeOff.Select(entry => ToDto(entry)).ToList(),
            Blocks = calendar.Windows.Select(window => ToDto(window)).ToList(),
            Requests = calendar.Requests
                .Select(request => new SupplierCalendarRequestDto
                {
                    Id = request.Id,
                    Date = request.Date,
                    Status = request.Status,
                    Category = request.Category,
                })
                .ToList(),
        };
}
