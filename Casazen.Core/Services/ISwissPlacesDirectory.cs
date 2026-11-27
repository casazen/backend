namespace Casazen.Core.Services;

/// <summary>
/// The read side of the official directory of the Swiss localities and municipalities (swisstopo, E29/F01/US01/T01, NF-124): the search of the
/// locality picker and the lookup of one municipality by its BFS number. Reference data, the same for every org, no personal data.
/// </summary>
public interface ISwissPlacesDirectory
{
    /// <summary>Most items one search returns.</summary>
    public const int MaxLimit = 20;

    /// <summary>Longest query accepted.</summary>
    public const int MaxQueryLength = 100;

    /// <summary>Shortest name fragment searched (after normalisation): below it the whole country would match.</summary>
    public const int MinNameLength = 2;

    /// <summary>
    /// The localities whose postal code starts with <paramref name="query"/> (one to four digits, a <c>CH-</c> prefix is ignored) or whose
    /// normalised name (<see cref="Entities.SwissLocality.NormalizeName"/>) contains it, exact matches first (the postal code, or the name, equal to the
    /// query), then the names that start with it, then the others; at most <paramref name="limit"/> (brought within 1 and <see cref="MaxLimit"/>).
    /// A locality that lies in several municipalities gives one item for each. A query that cannot match (blank, a name shorter than
    /// <see cref="MinNameLength"/>, five or more digits) gives an empty list.
    /// </summary>
    /// <exception cref="Exceptions.DomainRuleException">
    /// 422 <c>swiss_places_unavailable</c> when a search that could match finds nothing because the directory is not loaded (no municipality or no
    /// locality at all): an empty list means «no such place» only for a directory that is there.
    /// </exception>
    Task<IReadOnlyList<SwissLocalityMatch>> SearchLocalitiesAsync(string? query, int limit, CancellationToken cancellationToken = default);

    /// <summary>The municipality with this BFS number (digits, leading zeros and spaces ignored); <c>null</c> when the directory has none.</summary>
    /// <exception cref="Exceptions.DomainRuleException">422 <c>swiss_places_unavailable</c> when the number is not found because the directory is not loaded.</exception>
    Task<SwissMunicipalityInfo?> FindMunicipalityAsync(string? municipalityCode, CancellationToken cancellationToken = default);
}

/// <summary>One locality of a search: its postal code and name, and the municipality (BFS number, official name, canton) it lies in.</summary>
public sealed record SwissLocalityMatch(string PostalCode, string Name, string MunicipalityCode, string MunicipalityName, string CantonCode);

/// <summary>One municipality of the directory: BFS number, official name, canton.</summary>
public sealed record SwissMunicipalityInfo(string MunicipalityCode, string Name, string CantonCode);

/// <summary>The rules of the codes of the directory, apart from any storage.</summary>
public static class SwissPlaceCodes
{
    /// <summary>
    /// A BFS number as stored: one to four digits without leading zeros (<c>05192</c> and <c> 5192 </c> are <c>5192</c>); <c>null</c> when the
    /// value is not one (letters, five or more significant digits, zero).
    /// </summary>
    public static string? NormalizeMunicipalityCode(string? value)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text) || text.Length > 10 || !text.All(char.IsAsciiDigit))
            return null;

        var significant = text.TrimStart('0');
        return significant.Length is >= 1 and <= 4 ? significant : null;
    }

    /// <summary>
    /// The four digits of a Swiss postal code as a property stores it: a <c>CH-</c> (or <c>CH</c>) prefix and the spaces are ignored, and the six
    /// digits of a postal code with its add-on give their first four (<c>CH-6900</c>, <c>6 900</c> and <c>690000</c> are <c>6900</c>). <c>null</c>
    /// for anything else (letters, five digits such as an Italian CAP, a code below 1000): nothing is guessed from a malformed postal code.
    /// </summary>
    public static string? FourDigitPostalCode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var text = string.Concat(value.Where(c => !char.IsWhiteSpace(c))).ToUpperInvariant();
        if (text.StartsWith("CH-", StringComparison.Ordinal))
            text = text[3..];
        else if (text.StartsWith("CH", StringComparison.Ordinal))
            text = text[2..];

        if (text.Length is not (4 or 6) || !text.All(char.IsAsciiDigit) || text[0] == '0')
            return null;

        return text[..4];
    }
}
