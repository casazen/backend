namespace Casazen.Core.Suppliers;

/// <summary>
/// JSON names of the fields of the agenda endpoints (<c>api/supplier/availability/*</c>), as they appear in <c>fields</c> of
/// a 422 (<see cref="SupplierAgendaRuleException.Fields"/>). A day of the working hours and a band are named by their
/// position: <c>days[1].bands[0].endMinute</c>.
/// </summary>
public static class SupplierAgendaFields
{
    // Working hours: PUT availability/hours { days: [{ weekday, bands: [{ startMinute, endMinute }] }] }
    public const string Days = "days";
    public const string Weekday = "weekday";
    public const string Bands = "bands";
    public const string StartMinute = "startMinute";
    public const string EndMinute = "endMinute";

    // Time off: POST availability/time-off { fromDate, toDate, reason, label }
    public const string FromDate = "fromDate";
    public const string ToDate = "toDate";
    public const string Reason = "reason";
    public const string Label = "label";

    // Blocks and extra openings: POST availability/blocks { kind, startUtc, endUtc, label }
    public const string Kind = "kind";
    public const string StartUtc = "startUtc";
    public const string EndUtc = "endUtc";

    // Rules: PUT availability/rules { bufferMinutes, maxJobsPerDay, minNoticeHours, horizonDays, slotStepMinutes }
    public const string BufferMinutes = "bufferMinutes";
    public const string MaxJobsPerDay = "maxJobsPerDay";
    public const string MinNoticeHours = "minNoticeHours";
    public const string HorizonDays = "horizonDays";
    public const string SlotStepMinutes = "slotStepMinutes";
}
