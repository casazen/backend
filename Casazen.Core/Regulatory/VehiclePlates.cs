namespace Casazen.Core.Regulatory;

public static class VehiclePlates
{
    public const int MaxLength = 20;

    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var compact = new string(value.Where(c => !char.IsWhiteSpace(c)).ToArray()).ToUpperInvariant();
        return compact.Length == 0 ? null : compact;
    }

    public static bool IsValid(string? normalized) =>
        normalized is null
        || (normalized.Length <= MaxLength
            && normalized.All(c => char.IsLetterOrDigit(c) || c == '-')
            && normalized.Any(char.IsLetterOrDigit));
}
