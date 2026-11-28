using System.Globalization;

namespace Casazen.Core.Suppliers;

/// <summary>
/// The color of the buttons of a showcase (SP-13a). The supplier may pick its own, but white text must stay readable on it
/// (WCAG AA for normal text, 4.5:1): the editor checks it while the supplier chooses, and the server checks it again, because a
/// color that cannot be read on a public page is not a style, it is a defect (<c>vet-config.js</c> of the prototype is the model).
/// </summary>
public static class ShowcaseColor
{
    public const string Fallback = "#1F2933";

    public static bool TryNormalize(string? value, out string hex)
    {
        hex = string.Empty;
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text))
            return false;

        if (text[0] == '#')
            text = text[1..];

        if (text.Length != 6 || !text.All(char.IsAsciiHexDigit))
            return false;

        hex = "#" + text.ToUpperInvariant();
        return true;
    }

    public static double ContrastWithWhite(string hex) => 1.05 / (RelativeLuminance(Channels(hex)) + 0.05);

    public static bool IsReadableWithWhite(string hex) => ContrastWithWhite(hex) >= ShowcaseContentLimits.MinContrastWithWhite;

    public static string Darkened(string hex)
    {
        var channels = Channels(hex);
        for (var step = 1; step <= 44; step++)
        {
            var k = 0.04 + (step - 1) * 0.02;
            if (k > 0.9)
                break;

            var candidate = "#" + string.Concat(channels.Select(v =>
                ((int)Math.Round(v * 255 * (1 - k), MidpointRounding.AwayFromZero)).ToString("X2", CultureInfo.InvariantCulture)));
            if (IsReadableWithWhite(candidate))
                return candidate;
        }

        return Fallback;
    }

    private static double[] Channels(string hex) =>
        new[] { 0, 2, 4 }.Select(i => int.Parse(hex.AsSpan(1 + i, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255.0).ToArray();

    private static double RelativeLuminance(double[] channels)
    {
        static double Linear(double c) => c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        return (0.2126 * Linear(channels[0])) + (0.7152 * Linear(channels[1])) + (0.0722 * Linear(channels[2]));
    }
}
