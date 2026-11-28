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

public class SupplierHoursBandRequest
{
    public int? StartMinute { get; set; }

    public int? EndMinute { get; set; }
}

public class SupplierHoursDayRequest
{
    public DayOfWeek? Weekday { get; set; }

    [MaxLength(10)]
    public List<SupplierHoursBandRequest?>? Bands { get; set; }
}

public class SaveSupplierHoursRequest
{
    [MaxLength(14)]
    public List<SupplierHoursDayRequest?>? Days { get; set; }
}

public class CreateSupplierTimeOffRequest
{
    public DateOnly? FromDate { get; set; }

    public DateOnly? ToDate { get; set; }

    public SupplierTimeOffReason? Reason { get; set; }

    [MaxLength(400)]
    public string? Label { get; set; }
}

public class CreateSupplierBlockRequest
{
    public SupplierBusyWindowKind? Kind { get; set; }

    public DateTime? StartUtc { get; set; }

    public DateTime? EndUtc { get; set; }

    [MaxLength(400)]
    public string? Label { get; set; }
}

public class SaveSupplierRulesRequest
{
    public int? BufferMinutes { get; set; }

    public int? MaxJobsPerDay { get; set; }

    public int? MinNoticeHours { get; set; }

    public int? HorizonDays { get; set; }

    public int? SlotStepMinutes { get; set; }
}

public class SaveSupplierUrgentRequest
{
    public bool? Enabled { get; set; }

    public int? FromMinute { get; set; }

    public int? ToMinute { get; set; }

    public int? SurchargeCents { get; set; }
}

public class SupplierHoursBandDto
{
    public int StartMinute { get; set; }

    public int EndMinute { get; set; }
}

public class SupplierHoursDayDto
{
    public DayOfWeek Weekday { get; set; }

    public IReadOnlyList<SupplierHoursBandDto> Bands { get; set; } = [];
}

public class SupplierHoursDto
{
    public IReadOnlyList<SupplierHoursDayDto> Days { get; set; } = [];

    public DateTime? ConfiguredAt { get; set; }
}

public class SupplierTimeOffDto
{
    public Guid Id { get; set; }

    public DateOnly FromDate { get; set; }

    public DateOnly ToDate { get; set; }

    public SupplierTimeOffReason Reason { get; set; }

    public string? Label { get; set; }

    public DateTime CreatedAt { get; set; }
}

public class SupplierTimeOffListResponse
{
    public IReadOnlyList<SupplierTimeOffDto> Items { get; set; } = [];

    public int Total { get; set; }

    public int Limit { get; set; } = SupplierAgendaLimits.MaxTimeOffEntries;
}

public class SupplierBlockDto
{
    public Guid Id { get; set; }

    public SupplierBusyWindowKind Kind { get; set; }

    public SupplierBusyWindowSource Source { get; set; }

    public DateTime StartUtc { get; set; }

    public DateTime EndUtc { get; set; }

    public string? Label { get; set; }

    public DateTime CreatedAt { get; set; }
}

public class SupplierBlockListResponse
{
    public IReadOnlyList<SupplierBlockDto> Items { get; set; } = [];

    public int Total { get; set; }

    public int Limit { get; set; } = SupplierAgendaLimits.MaxManualWindows;
}

public class SupplierRulesDto
{
    public int BufferMinutes { get; set; }

    public int MaxJobsPerDay { get; set; }

    public int MinNoticeHours { get; set; }

    public int HorizonDays { get; set; }

    public int SlotStepMinutes { get; set; }
}

public class SupplierUrgentDto
{
    public bool Enabled { get; set; }

    public int FromMinute { get; set; }

    public int ToMinute { get; set; }

    public int SurchargeCents { get; set; }
}

public class SupplierClosedDayDto
{
    public DateOnly Date { get; set; }

    public SupplierAvailabilitySource Source { get; set; }
}

public class SupplierCalendarRequestDto
{
    public Guid Id { get; set; }

    public DateOnly Date { get; set; }

    public DateTime? StartUtc { get; set; }

    public DateTime? EndUtc { get; set; }

    public ServiceRequestStatus Status { get; set; }

    public string Category { get; set; } = string.Empty;

    public string? ServiceName { get; set; }

    /// <summary>
    /// Who asks, as the supplier may know it before taking the request (decision D9): the name of the host's org for a host's
    /// request; for a private customer of the public showcase <c>Nome C.</c> until the request is taken, the full name after.
    /// Never an address, a contact or a note.
    /// </summary>
    public string? ClientLabel { get; set; }

    public string? Comune { get; set; }

    public string Source { get; set; } = string.Empty;

    public string Phase { get; set; } = string.Empty;
}

public class SupplierCalendarDto
{
    public IReadOnlyList<SupplierGuestOrderAppointment> GuestOrders { get; set; } = [];

    public DateOnly From { get; set; }

    public DateOnly To { get; set; }

    public string TimeZone { get; set; } = string.Empty;

    public IReadOnlyList<SupplierHoursDayDto> WorkingHours { get; set; } = [];

    public IReadOnlyList<SupplierClosedDayDto> ClosedDays { get; set; } = [];

    public IReadOnlyList<SupplierTimeOffDto> TimeOff { get; set; } = [];

    public IReadOnlyList<SupplierBlockDto> Blocks { get; set; } = [];

    public IReadOnlyList<SupplierCalendarRequestDto> Requests { get; set; } = [];
}

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

    public static SupplierUrgentDto ToDto(SupplierUrgentSettings urgent) =>
        new()
        {
            Enabled = urgent.Enabled,
            FromMinute = urgent.FromMinute,
            ToMinute = urgent.ToMinute,
            SurchargeCents = urgent.SurchargeCents,
        };

    public static SupplierUrgentInput ToInput(SaveSupplierUrgentRequest request) =>
        new(request.Enabled, request.FromMinute, request.ToMinute, request.SurchargeCents);

    public static SupplierCalendarDto ToDto(SupplierCalendar calendar) =>
        new()
        {
            GuestOrders = calendar.GuestOrders,
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
                    StartUtc = request.StartUtc,
                    EndUtc = request.EndUtc,
                    Status = request.Status,
                    Category = request.Category,
                    ServiceName = request.ServiceName,
                    ClientLabel = request.ClientLabel,
                    Comune = request.Comune,
                    Source = request.Source,
                    Phase = request.Phase,
                })
                .ToList(),
        };
}
