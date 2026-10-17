namespace Casazen.Web.DTOs.Supplier;

/// <summary>The order of <c>GET /api/suppliers</c> (SR-09a): <c>?sort=name|price|response</c>.</summary>
public enum SupplierPickerSort
{
    /// <summary>By the legal name of the supplier (default).</summary>
    Name,

    /// <summary>The lowest "from" price first; the suppliers without a price last.</summary>
    Price,

    /// <summary>The fastest measured response first; the suppliers whose response is not measured last.</summary>
    Response,
}

/// <summary>
/// Search and order of the supplier picker of the marketplace (SR-09a), made on the entries already built: the list of a comune
/// is short and every entry carries what the order needs (price, response time).
/// </summary>
public static class SupplierPickerSearch
{
    /// <summary>
    /// Reads <c>?sort=</c>: blank is <see cref="SupplierPickerSort.Name"/>; <c>name</c>, <c>price</c> and <c>response</c> (any case)
    /// are the three orders. False for anything else.
    /// </summary>
    public static bool TryParseSort(string? value, out SupplierPickerSort sort)
    {
        sort = SupplierPickerSort.Name;
        if (string.IsNullOrWhiteSpace(value))
            return true;

        switch (value.Trim().ToLowerInvariant())
        {
            case "name":
                return true;
            case "price":
                sort = SupplierPickerSort.Price;
                return true;
            case "response":
                sort = SupplierPickerSort.Response;
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// The suppliers that match <paramref name="q"/> (every word of it is in the legal name or in a category code of the supplier,
    /// whatever the case; blank keeps all), in the order of <paramref name="sort"/>. Ties are broken by name, then by org id, so the
    /// order is always the same.
    /// </summary>
    public static List<SupplierPickerDto> Apply(IEnumerable<SupplierPickerDto> suppliers, string? q, SupplierPickerSort sort)
    {
        ArgumentNullException.ThrowIfNull(suppliers);

        var words = (q ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var matching = words.Length == 0
            ? suppliers
            : suppliers.Where(s => words.All(word => Matches(s, word)));

        var ordered = sort switch
        {
            SupplierPickerSort.Price => matching.OrderBy(s => s.PriceFromCents is null).ThenBy(s => s.PriceFromCents),
            SupplierPickerSort.Response => matching.OrderBy(s => s.MedianResponseMinutes is null).ThenBy(s => s.MedianResponseMinutes),
            _ => matching.OrderBy(_ => 0),
        };

        return ordered
            .ThenBy(s => s.LegalName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.OrgId)
            .ToList();
    }

    private static bool Matches(SupplierPickerDto supplier, string word) =>
        supplier.LegalName.Contains(word, StringComparison.OrdinalIgnoreCase)
        || supplier.Categories.Any(category => category.Contains(word, StringComparison.OrdinalIgnoreCase));
}
