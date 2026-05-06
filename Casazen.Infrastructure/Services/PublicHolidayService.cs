using Casazen.Core.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

public class PublicHolidayService(
    HttpClient httpClient,
    IMemoryCache cache,
    ILogger<PublicHolidayService> logger) : IPublicHolidayService
{
    private const string NagerDateBaseUrl = "https://date.nager.at/api/v3";
    private const string ItalyCountryCode = "IT";
    private const int CacheDurationMinutes = 1440; // 24 hours

    public async Task<bool> IsPublicHolidayAsync(DateTime date)
    {
        try
        {
            var holidays = await GetPublicHolidaysAsync(date.Year);
            return holidays.Any(h => h.Date == date.Date);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error checking public holiday for date {Date}", date);
            return false;
        }
    }

    public async Task<IEnumerable<DateTime>> GetPublicHolidaysAsync(int year)
    {
        var cacheKey = $"public_holidays_italy_{year}";

        if (cache.TryGetValue(cacheKey, out IEnumerable<DateTime>? cachedHolidays))
        {
            return cachedHolidays!;
        }

        try
        {
            var url = $"{NagerDateBaseUrl}/PublicHolidays/{year}/{ItalyCountryCode}";
            var response = await httpClient.GetAsync(url);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Nager.Date API returned {StatusCode} for year {Year}", response.StatusCode, year);
                return Enumerable.Empty<DateTime>();
            }

            var content = await response.Content.ReadAsStringAsync();
            var holidays = ParseHolidaysFromJson(content);

            // Cache the results
            cache.Set(cacheKey, holidays, TimeSpan.FromMinutes(CacheDurationMinutes));

            logger.LogInformation("Retrieved {Count} public holidays for Italy in {Year} from Nager.Date API", holidays.Count(), year);
            return holidays;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error fetching public holidays from Nager.Date API for year {Year}", year);
            return Enumerable.Empty<DateTime>();
        }
    }

    private static IEnumerable<DateTime> ParseHolidaysFromJson(string json)
    {
        var holidays = new List<DateTime>();

        try
        {
            // Simple JSON parsing - extract dates in YYYY-MM-DD format
            // Expected format: [{"date":"2024-01-01","name":"New Year's Day",...}]
            var datePattern = @"""date"":""(\d{4}-\d{2}-\d{2})""";
            var matches = System.Text.RegularExpressions.Regex.Matches(json, datePattern);

            foreach (var match in matches.Cast<System.Text.RegularExpressions.Match>())
            {
                if (DateTime.TryParse(match.Groups[1].Value, out var date))
                {
                    holidays.Add(date);
                }
            }
        }
        catch
        {
            // If parsing fails, return empty list
        }

        return holidays;
    }
}
