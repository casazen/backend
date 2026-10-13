using Casazen.Core.Regulatory;

namespace Casazen.Core.Demo;

/// <summary>
/// Invented CIN in the official format (<see cref="CinFormat"/>): <c>IT</c> + the property's ISTAT comune + category
/// <c>C2</c> + <c>TEST</c> and a two-digit sequence. The letters TEST are there on purpose so the code is never
/// mistaken for one issued by the Ministero del Turismo.
/// </summary>
public static class DemoCin
{
    public static string ForIstat(string istatCode, int sequence)
    {
        if (istatCode is not { Length: 6 } || !istatCode.All(char.IsAsciiDigit))
            throw new ArgumentException("An ISTAT comune code is 6 digits.", nameof(istatCode));
        if (sequence is < 1 or > 99)
            throw new ArgumentOutOfRangeException(nameof(sequence));

        var cin = $"IT{istatCode}C2TEST{sequence:00}";
        if (!CinFormat.IsValid(cin) || CinFormat.GetIstatComuneCode(cin) != istatCode)
            throw new InvalidOperationException("The demo CIN builder produced a code the official format rejects.");

        return cin;
    }
}
