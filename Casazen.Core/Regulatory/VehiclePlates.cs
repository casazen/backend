namespace Casazen.Core.Regulatory;

/// <summary>
/// The plate of the car the guests of a stay arrive with (<see cref="Entities.Booking.VehiclePlate"/>): «Numero autovettura» of the Ticino police
/// file (NF-132, casazen/backend#979). Optional; the form asks it only where the property is in canton Ticino. Plates of every country are accepted:
/// letters (with their accents: «MÜ» is a German district), digits and hyphens, without spaces, upper case.
/// </summary>
public static class VehiclePlates
{
    /// <summary>Longest plate stored, once normalized.</summary>
    public const int MaxLength = 20;

    /// <summary>The plate as stored: without spaces, upper case; <c>null</c> when blank (no plate).</summary>
    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var compact = new string(value.Where(c => !char.IsWhiteSpace(c)).ToArray()).ToUpperInvariant();
        return compact.Length == 0 ? null : compact;
    }

    /// <summary>
    /// True for a normalized plate of 1 to <see cref="MaxLength"/> letters, digits and hyphens with at least one letter or digit; true for no
    /// plate (<c>null</c>).
    /// </summary>
    public static bool IsValid(string? normalized) =>
        normalized is null
        || (normalized.Length <= MaxLength
            && normalized.All(c => char.IsLetterOrDigit(c) || c == '-')
            && normalized.Any(char.IsLetterOrDigit));
}
