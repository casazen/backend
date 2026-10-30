using System.Globalization;

namespace Casazen.Core.Services;

/// <summary>
/// The expected arrival time a guest can give at the public checkout (DB-03b): <c>HH:mm</c> (24 hours, <c>09:30</c>, <c>15:00</c>) or
/// <see cref="Late"/>, "after the check-in window". It is a preference the host reads ("arrivo previsto alle…"), never a rule: it moves
/// no price, no availability and no check, and the window of the check-in is <b>not</b> enforced (the property has no end of window, and an
/// optional note must never be what stops a paid booking); only the shape is checked.
/// </summary>
public static class ArrivalTimeRules
{
    /// <summary>Stored when the guest arrives after the check-in window ("dopo le …" in the checkout).</summary>
    public const string Late = "late";

    /// <summary>Longest stored value (<c>HH:mm</c> is 5 characters, <c>late</c> 4).</summary>
    public const int MaxLength = 5;

    /// <summary>
    /// The value to store for <paramref name="value"/>: <c>null</c> for none (a missing or blank value), <see cref="Late"/>, or the time as
    /// <c>HH:mm</c> (<c>9:30</c> is read as <c>09:30</c>). <c>false</c> for anything else.
    /// </summary>
    public static bool TryNormalize(string? value, out string? normalized)
    {
        normalized = null;
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text))
            return true;

        if (string.Equals(text, Late, StringComparison.OrdinalIgnoreCase))
        {
            normalized = Late;
            return true;
        }

        var parts = text.Split(':');
        if (parts.Length == 2
            && parts[0].Length is >= 1 and <= 2
            && parts[1].Length == 2
            && parts.All(part => part.All(char.IsAsciiDigit))
            && int.Parse(parts[0], CultureInfo.InvariantCulture) is var hour and <= 23
            && int.Parse(parts[1], CultureInfo.InvariantCulture) is var minute and <= 59)
        {
            normalized = string.Create(CultureInfo.InvariantCulture, $"{hour:00}:{minute:00}");
            return true;
        }

        return false;
    }
}
