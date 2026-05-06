namespace Casazen.Core.Services;

public interface IPublicHolidayService
{
    /// <summary>
    /// Check if a given date is a public holiday in Italy.
    /// </summary>
    /// <param name="date">The date to check (UTC).</param>
    /// <returns>True if the date is a public holiday in Italy.</returns>
    Task<bool> IsPublicHolidayAsync(DateTime date);

    /// <summary>
    /// Get all public holidays in Italy for a given year.
    /// </summary>
    /// <param name="year">The year to retrieve holidays for.</param>
    /// <returns>A list of public holiday dates.</returns>
    Task<IEnumerable<DateTime>> GetPublicHolidaysAsync(int year);
}
