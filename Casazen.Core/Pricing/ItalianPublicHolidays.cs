namespace Casazen.Core.Pricing;

/// <summary>National public holidays of Italy (Legge 27 maggio 1949 n. 260, art. 2, as amended).</summary>
public enum ItalianHoliday
{
    /// <summary>1 January, Capodanno.</summary>
    NewYear,

    /// <summary>6 January, Epifania.</summary>
    Epiphany,

    /// <summary>Easter Sunday (Pasqua), computed.</summary>
    EasterSunday,

    /// <summary>Easter Monday (Lunedì dell'Angelo, Pasquetta), computed.</summary>
    EasterMonday,

    /// <summary>25 April, Festa della Liberazione.</summary>
    Liberation,

    /// <summary>1 May, Festa del Lavoro.</summary>
    Labour,

    /// <summary>2 June, Festa della Repubblica.</summary>
    Republic,

    /// <summary>15 August, Assunzione (Ferragosto).</summary>
    Assumption,

    /// <summary>4 October, San Francesco d'Assisi, from 2026 (Legge 8 ottobre 2025 n. 151).</summary>
    SaintFrancis,

    /// <summary>1 November, Ognissanti.</summary>
    AllSaints,

    /// <summary>8 December, Immacolata Concezione.</summary>
    ImmaculateConception,

    /// <summary>25 December, Natale.</summary>
    Christmas,

    /// <summary>26 December, Santo Stefano.</summary>
    SaintStephen,
}

/// <summary>
/// The national public holidays of Italy for a year, computed locally (PC-15): the fixed dates of Legge 260/1949 art. 2
/// (as amended by Legge 54/1977, DPR 792/1985 and Legge 151/2025) plus Easter Sunday and Easter Monday from the Gregorian
/// computus. Only official national dates: no patron saints of a comune, no local events, no external API.
/// </summary>
public static class ItalianPublicHolidays
{
    /// <summary>First year in which 4 October is a national holiday (Legge 151/2025, in force from 1 January 2026).</summary>
    public const int SaintFrancisFirstYear = 2026;

    /// <summary>
    /// Easter Sunday of <paramref name="year"/> in the Gregorian calendar (anonymous Gregorian algorithm, Meeus/Jones/Butcher).
    /// </summary>
    public static DateOnly EasterSunday(int year)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(year, 1583);

        var a = year % 19;
        var b = year / 100;
        var c = year % 100;
        var d = b / 4;
        var e = b % 4;
        var f = (b + 8) / 25;
        var g = (b - f + 1) / 3;
        var h = ((19 * a) + b - d - g + 15) % 30;
        var i = c / 4;
        var k = c % 4;
        var l = (32 + (2 * e) + (2 * i) - h - k) % 7;
        var m = (a + (11 * h) + (22 * l)) / 451;
        var month = (h + l - (7 * m) + 114) / 31;
        var day = ((h + l - (7 * m) + 114) % 31) + 1;
        return new DateOnly(year, month, day);
    }

    /// <summary>Every national holiday of <paramref name="year"/>, in date order.</summary>
    public static IReadOnlyList<(DateOnly Date, ItalianHoliday Holiday)> ForYear(int year)
    {
        var easter = EasterSunday(year);
        var holidays = new List<(DateOnly Date, ItalianHoliday Holiday)>
        {
            (new DateOnly(year, 1, 1), ItalianHoliday.NewYear),
            (new DateOnly(year, 1, 6), ItalianHoliday.Epiphany),
            (easter, ItalianHoliday.EasterSunday),
            (easter.AddDays(1), ItalianHoliday.EasterMonday),
            (new DateOnly(year, 4, 25), ItalianHoliday.Liberation),
            (new DateOnly(year, 5, 1), ItalianHoliday.Labour),
            (new DateOnly(year, 6, 2), ItalianHoliday.Republic),
            (new DateOnly(year, 8, 15), ItalianHoliday.Assumption),
            (new DateOnly(year, 11, 1), ItalianHoliday.AllSaints),
            (new DateOnly(year, 12, 8), ItalianHoliday.ImmaculateConception),
            (new DateOnly(year, 12, 25), ItalianHoliday.Christmas),
            (new DateOnly(year, 12, 26), ItalianHoliday.SaintStephen),
        };

        if (year >= SaintFrancisFirstYear)
            holidays.Add((new DateOnly(year, 10, 4), ItalianHoliday.SaintFrancis));

        // Easter Monday can fall on 25 April (e.g. 2011): both are kept, the first in the list wins in On().
        return holidays.OrderBy(h => h.Date).ToList();
    }

    /// <summary>The national holiday on <paramref name="date"/>, or <c>null</c> on an ordinary day.</summary>
    public static ItalianHoliday? On(DateOnly date)
    {
        foreach (var (day, holiday) in ForYear(date.Year))
        {
            if (day == date)
                return holiday;
        }

        return null;
    }
}
