using System.Text.RegularExpressions;

namespace Casazen.Core.Services;

public static class CinComplianceRules
{
    public static readonly DateOnly RegulatoryDeadline = new(2026, 3, 1);
    private static readonly Regex CinPattern = new(@"^IT-\d{5}-\d{10}$", RegexOptions.Compiled);

    public static string ResolveStatus(string? cinCode)
    {
        if (string.IsNullOrWhiteSpace(cinCode))
            return "missing";

        return CinPattern.IsMatch(cinCode) ? "valid" : "invalid";
    }

    public static bool IsCompliant(string? cinCode) => ResolveStatus(cinCode) == "valid";

    public static int DaysUntilDeadline(DateOnly? today = null)
    {
        var reference = today ?? DateOnly.FromDateTime(DateTime.UtcNow);
        return Math.Max(0, RegulatoryDeadline.DayNumber - reference.DayNumber);
    }
}
