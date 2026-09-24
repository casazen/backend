namespace Casazen.Infrastructure.Services.ICal;

/// <summary>
/// Window in which recurring feed events (<c>RRULE</c>/<c>RDATE</c>) are expanded, bound from the optional
/// <c>ICalImport</c> configuration section (Railway: <c>ICalImport__RecurrenceMonthsAhead</c>, ...). Single events are
/// imported whatever their dates. See <c>docs/runbooks/ical.md</c>.
/// </summary>
public sealed class ICalImportOptions
{
    public const string SectionName = "ICalImport";

    /// <summary>Months after today (Europe/Rome) in which occurrences are imported. Default 18, range 1-60.</summary>
    public int RecurrenceMonthsAhead { get; set; } = 18;

    /// <summary>Months before today (Europe/Rome) still imported, for the calendar views. Default 1, range 0-12.</summary>
    public int RecurrenceMonthsBack { get; set; } = 1;

    internal int EffectiveMonthsAhead => Math.Clamp(RecurrenceMonthsAhead, 1, 60);

    internal int EffectiveMonthsBack => Math.Clamp(RecurrenceMonthsBack, 0, 12);
}
