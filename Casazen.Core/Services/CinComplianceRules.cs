using Casazen.Core.Enums;
using Casazen.Core.Regulatory;

namespace Casazen.Core.Services;

/// <summary>
/// CIN status as the API exposes it. The deadline is configuration (<c>Cin:ExposureDeadline</c>), not a constant: see
/// <see cref="CinDeadlineCalendar"/> (CO-20).
/// </summary>
/// <remarks>
/// CIN compliance and alerts apply <b>only to short-rent (STR) properties</b> (CO-20, PO decision 2026-10-08).
/// Long-rent (LTR) properties — owned by users with <c>RentalType.LongTerm</c> — are excluded from CIN
/// checks and alerts. The filter is applied upstream in <c>CinDeadlineAlertService</c>.
/// </remarks>
public static class CinComplianceRules
{
    /// <summary>Computed CIN status as the lowercase API value: "valid", "missing" or "invalid" (see <see cref="CinFormat"/>).</summary>
    public static string ResolveStatus(string? cinCode) => CinFormat.GetStatus(cinCode) switch
    {
        CinStatus.Valid => "valid",
        CinStatus.Missing => "missing",
        _ => "invalid",
    };

    public static bool IsCompliant(string? cinCode) => CinFormat.IsValid(cinCode);
}
