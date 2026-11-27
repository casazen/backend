namespace Casazen.Core.Suppliers;

public static class SupplierAgendaDefaults
{
    public const int BufferMinutes = 30;

    public const int MaxJobsPerDay = 3;

    public const int MinNoticeHours = 24;

    public const int HorizonDays = 35;

    public const int SlotStepMinutes = 60;

    /// <summary>
    /// Jobs the supplier can do at the same time. Decision D10: <b>1</b>. The planner supports more, but the console does not
    /// offer it (the field is not part of <c>api/supplier/availability/rules</c>).
    /// </summary>
    public const int ParallelJobs = 1;

    public const int RespondWithinMinutes = 180;

    public const int UrgentFromMinute = 7 * 60;

    public const int UrgentToMinute = 22 * 60;

    public const int UrgentSurchargeCents = 0;
}

public static class SupplierAgendaLimits
{
    public const int MinutesPerDay = 24 * 60;

    public const int MaxBandsPerDay = 3;

    public const int MaxTimeOffDays = 366;

    public const int MaxTimeOffEntries = 100;

    public const int MinWindowMinutes = 15;

    public const int MaxBlockDays = 31;

    public const int MaxManualWindows = 200;

    public const int LabelMaxLength = 80;

    public const int ExternalUidMaxLength = 255;

    public const int MaxAdvanceDays = 730;

    public const int RuleGranularityMinutes = 5;

    public const int MinBufferMinutes = 0;
    public const int MaxBufferMinutes = 240;

    public const int MinJobsPerDay = 1;
    public const int MaxJobsPerDayLimit = 50;

    public const int MinNoticeHours = 0;

    public const int MaxNoticeHours = SupplierServiceCatalogLimits.MaxMinNoticeHours;

    public const int MinHorizonDays = 1;
    public const int MaxHorizonDays = 365;

    public const int MinSlotStepMinutes = 15;
    public const int MaxSlotStepMinutes = 240;

    public const int MinParallelJobs = 1;
    public const int MaxParallelJobs = 10;

    public const int MinRespondWithinMinutes = 15;
    public const int MaxRespondWithinMinutes = 7 * 24 * 60;

    public const int MaxUrgentSurchargeCents = 50_000;

    public const int UrgentNoticeMinutes = 30;

    public const int UrgentHorizonDays = 1;

    public const int MaxCalendarDays = 62;

    public const int DefaultCalendarDays = 31;

    /// <summary>
    /// Days kept clear at each end of the calendar (the year 1 and the year 9999) by a range of the agenda
    /// (<see cref="SupplierAgendaRange"/>): the arithmetic around a range (a day more or less on each side, the days a suggestion looks
    /// ahead, the start of a day in UTC) overflows there, so a date that close to an end is a 400 and never a 500.
    /// </summary>
    public const int RangeEdgeDays = 90;

    public const int MaxConflictRequests = 300;

    public const int ConflictSuggestionDays = 21;

    public const int ConflictsMaxYearsBack = 1;

    public const int ConflictsMaxYearsAhead = 10;
}
