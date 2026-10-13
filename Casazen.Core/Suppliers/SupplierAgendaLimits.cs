namespace Casazen.Core.Suppliers;

/// <summary>
/// The values a supplier's agenda starts with, before the supplier sets its own (SP-03, <c>gap/05</c> §4.1). One place for
/// them: the settings entity, the rules endpoint (a supplier that never saved a rule gets these) and the planner read them
/// from here.
/// </summary>
public static class SupplierAgendaDefaults
{
    /// <summary>Minutes kept free before and after every job (the time to move).</summary>
    public const int BufferMinutes = 30;

    /// <summary>Jobs a day takes at most; the day then shows as full.</summary>
    public const int MaxJobsPerDay = 3;

    /// <summary>Shortest notice, in hours, between a booking and its work (a service can ask for its own).</summary>
    public const int MinNoticeHours = 24;

    /// <summary>How many days ahead of today a customer can book.</summary>
    public const int HorizonDays = 35;

    /// <summary>Distance, in minutes, between two slots that start inside the same working band.</summary>
    public const int SlotStepMinutes = 60;

    /// <summary>
    /// Jobs the supplier can do at the same time. Decision D10: <b>1</b>. The planner supports more, but the console does not
    /// offer it (the field is not part of <c>api/supplier/availability/rules</c>).
    /// </summary>
    public const int ParallelJobs = 1;

    /// <summary>How long a supplier has to answer a new request, in minutes (3 hours; used by the request flow, SP-04).</summary>
    public const int RespondWithinMinutes = 180;
}

/// <summary>
/// Technical bounds of the supplier's agenda (SP-03): they keep the agenda readable, the planner fast and a request small,
/// they are not product rules. One place, so the entities (database checks), the validation and the DTO attributes cannot
/// drift apart. A value is checked by <see cref="SupplierAgendaRules"/> (422 with the fields at fault) and, for the lengths,
/// by the request DTO attributes (400 <c>validation_error</c>).
/// </summary>
public static class SupplierAgendaLimits
{
    // ─── Working hours ───────────────────────────────────────────────────────────

    /// <summary>Minutes in a calendar day: the highest value of <c>EndMinute</c> ("until midnight").</summary>
    public const int MinutesPerDay = 24 * 60;

    /// <summary>Bands of working hours in one day (the demo shows morning and afternoon).</summary>
    public const int MaxBandsPerDay = 3;

    // ─── Time off ────────────────────────────────────────────────────────────────

    /// <summary>Longest time off, in days, both ends counted (a year).</summary>
    public const int MaxTimeOffDays = 366;

    /// <summary>Time off entries that have not ended yet, per supplier. Checked under the agenda lock.</summary>
    public const int MaxTimeOffEntries = 100;

    // ─── Blocks and extra openings ───────────────────────────────────────────────

    /// <summary>Shortest block or extra opening, in minutes.</summary>
    public const int MinWindowMinutes = 15;

    /// <summary>Longest block, in days (a block is for hours; a longer closure is a time off).</summary>
    public const int MaxBlockDays = 31;

    /// <summary>Manual windows (blocks and extra openings) that have not ended yet, per supplier. Checked under the agenda lock.</summary>
    public const int MaxManualWindows = 200;

    /// <summary>Longest label of a time off or a window (the demo counts "0/80"); only the supplier's console shows it.</summary>
    public const int LabelMaxLength = 80;

    /// <summary>Longest <c>ExternalUid</c> (the UID of an iCal event, SP-05).</summary>
    public const int ExternalUidMaxLength = 255;

    /// <summary>How far ahead, in days, a time off or a window may start (two years).</summary>
    public const int MaxAdvanceDays = 730;

    // ─── Rules ───────────────────────────────────────────────────────────────────

    /// <summary>The buffer and the slot step are multiples of this many minutes (no slot at 10:07).</summary>
    public const int RuleGranularityMinutes = 5;

    public const int MinBufferMinutes = 0;
    public const int MaxBufferMinutes = 240;

    public const int MinJobsPerDay = 1;
    public const int MaxJobsPerDayLimit = 50;

    public const int MinNoticeHours = 0;

    /// <summary>The same bound as the notice of a service of the catalog (30 days).</summary>
    public const int MaxNoticeHours = SupplierServiceCatalogLimits.MaxMinNoticeHours;

    public const int MinHorizonDays = 1;
    public const int MaxHorizonDays = 365;

    public const int MinSlotStepMinutes = 15;
    public const int MaxSlotStepMinutes = 240;

    public const int MinParallelJobs = 1;
    public const int MaxParallelJobs = 10;

    public const int MinRespondWithinMinutes = 15;
    public const int MaxRespondWithinMinutes = 7 * 24 * 60;

    // ─── Calendar ────────────────────────────────────────────────────────────────

    /// <summary><c>GET api/supplier/calendar</c> answers at most this many days, both ends counted (two months).</summary>
    public const int MaxCalendarDays = 62;

    /// <summary>The range of <c>GET api/supplier/calendar</c> when the request does not say: today and the next 30 days.</summary>
    public const int DefaultCalendarDays = 31;
}
