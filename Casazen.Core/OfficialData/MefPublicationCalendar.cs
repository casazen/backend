namespace Casazen.Core.OfficialData;

/// <summary>
/// Effectiveness of a municipal tax act published in the MEF archive <c>nuova_at</c>: the first day of the second
/// month after the MEF publication date (fixture Cesano Maderno: published 10/01/2025 → 01/03/2025).
/// </summary>
public static class MefPublicationCalendar
{
    public static DateOnly ValidFrom(DateOnly mefPublishedOn) =>
        new DateOnly(mefPublishedOn.Year, mefPublishedOn.Month, 1).AddMonths(2);
}
