using Casazen.Core.Enums;
using Casazen.Core.Regulatory;

namespace Casazen.Core.Services;

/// <summary>
/// CIN status as the API exposes it. The deadline is configuration (<c>Cin:ExposureDeadline</c>), not a constant: see
/// <see cref="CinDeadlineCalendar"/> (CO-20).
/// </summary>
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
