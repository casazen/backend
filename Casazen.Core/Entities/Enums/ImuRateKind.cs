namespace Casazen.Core.Entities.Enums;

/// <summary>
/// How the IMU rate on a <see cref="ComuneImuChannel"/> was obtained (LT-13, A7-22): a rate published in an official
/// deliberation, or one derived from another year's rate (never treated as authoritative in the notification draft).
/// </summary>
public enum ImuRateKind
{
    /// <summary>Published in an official comune deliberation (<c>RateSourceUrl</c> points to it).</summary>
    Official = 0,

    /// <summary>Computed from another known rate (e.g. a previous year's), not itself published.</summary>
    Derived = 1,
}
