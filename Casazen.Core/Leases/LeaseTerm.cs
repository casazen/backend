namespace Casazen.Core.Leases;

/// <summary>
/// Actual term of a lease computed from its dates (LT-03, A7-03): the contract states this value, never a fixed
/// "3+2" or "4+4". The end date is inclusive, as in "dal 01/09/2026 al 31/08/2030" = 4 years.
/// </summary>
/// <param name="Months">Whole calendar months from the start date.</param>
/// <param name="Days">Days left after <paramref name="Months"/>.</param>
public readonly record struct LeaseTerm(int Months, int Days)
{
    public bool IsWholeYears => Days == 0 && Months > 0 && Months % 12 == 0;

    /// <summary>Term between <paramref name="startDate"/> and the inclusive <paramref name="endDate"/>; null when the end is before the start.</summary>
    public static LeaseTerm? Between(DateTime startDate, DateTime endDate)
    {
        var from = startDate.Date;
        var toExclusive = endDate.Date.AddDays(1);
        if (toExclusive <= from)
            return null;

        var months = ((toExclusive.Year - from.Year) * 12) + toExclusive.Month - from.Month;
        if (from.AddMonths(months) > toExclusive)
            months--;

        var days = (toExclusive - from.AddMonths(months)).Days;
        return new LeaseTerm(months, days);
    }

    /// <summary>Italian wording used in the contract: "4 anni", "18 mesi", "1 anno", "2 mesi e 10 giorni".</summary>
    public string ToItalianText()
    {
        var parts = new List<string>(2);
        if (Months > 0 && Months % 12 == 0)
        {
            var years = Months / 12;
            parts.Add(years == 1 ? "1 anno" : $"{years} anni");
        }
        else if (Months > 0)
        {
            parts.Add(Months == 1 ? "1 mese" : $"{Months} mesi");
        }

        if (Days > 0)
            parts.Add(Days == 1 ? "1 giorno" : $"{Days} giorni");

        return string.Join(" e ", parts);
    }
}
