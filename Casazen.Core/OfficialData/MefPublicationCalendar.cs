namespace Casazen.Core.OfficialData;

public static class MefPublicationCalendar
{
    public static DateOnly ValidFrom(DateOnly mefPublishedOn) =>
        new DateOnly(mefPublishedOn.Year, mefPublishedOn.Month, 1).AddMonths(2);
}
