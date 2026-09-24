using Casazen.Core.Enums;
using Casazen.Core.Regulatory;
using Casazen.Core.Utilities;

namespace Casazen.Core.Services;

public static class CinComplianceRules
{
    public static readonly DateOnly RegulatoryDeadline = new(2026, 3, 1);

    /// <summary>Computed CIN status as the lowercase API value: "valid", "missing" or "invalid" (see <see cref="CinFormat"/>).</summary>
    public static string ResolveStatus(string? cinCode) => CinFormat.GetStatus(cinCode) switch
    {
        CinStatus.Valid => "valid",
        CinStatus.Missing => "missing",
        _ => "invalid",
    };

    public static bool IsCompliant(string? cinCode) => CinFormat.IsValid(cinCode);

    public static int DaysUntilDeadline(DateOnly? today = null)
    {
        var reference = today ?? TimeProvider.System.TodayInRomeAsDateOnly();
        return Math.Max(0, RegulatoryDeadline.DayNumber - reference.DayNumber);
    }
}
