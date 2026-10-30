using System.Collections.Frozen;
using System.Globalization;
using Casazen.Core.Search;
using Casazen.Core.Suppliers;
using Casazen.Infrastructure.Email.Templates;

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
    private static readonly CultureInfo Italian = CultureInfo.GetCultureInfo("it-IT");
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en");

    /// <summary>
    /// What a host may type to find a category (MKT): its code and its name in Italian and in English, folded like the words
    /// typed (<see cref="SearchText.Fold"/>): lower case, no accents, "Check-in ospiti" is "check in ospiti".
    /// </summary>
    private static readonly FrozenDictionary<string, string> CategoryWords = ServiceCategories.All.ToFrozenDictionary(
        code => code,
        code => SearchText.Fold(
            $"{code} {EmailTemplates.ServiceCategoryLabel(Italian, code)} {EmailTemplates.ServiceCategoryLabel(English, code)}"),
        StringComparer.Ordinal);

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
    /// The suppliers that match <paramref name="q"/> (every word of it is in the legal name, in a category of the supplier, as its
    /// code or its Italian or English name, or in the name of one of its published services; the case and the accents do not
    /// matter; blank keeps all), in the order of <paramref name="sort"/>. Ties are broken by name, then by org id, so the order
    /// is always the same. <paramref name="services"/> are all the published services of each supplier (the entries list only the
    /// first ones): without them the search looks at the services the entries carry.
    /// </summary>
    public static List<SupplierPickerDto> Apply(
        IEnumerable<SupplierPickerDto> suppliers,
        string? q,
        SupplierPickerSort sort,
        IReadOnlyDictionary<Guid, IReadOnlyList<SupplierPickerService>>? services = null)
    {
        ArgumentNullException.ThrowIfNull(suppliers);

        var words = SearchText.Fold(q).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var matching = words.Length == 0
            ? suppliers
            : suppliers.Where(s => Matches(SearchKey(s, services), words));

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

    /// <summary>The words a supplier is found by, folded: its name, its categories and the names of its published services.</summary>
    private static string SearchKey(SupplierPickerDto supplier, IReadOnlyDictionary<Guid, IReadOnlyList<SupplierPickerService>>? services)
    {
        var serviceNames = services is not null && services.TryGetValue(supplier.OrgId, out var published)
            ? published.Select(service => service.Name)
            : supplier.Services.Select(service => service.Name);

        var parts = new List<string> { SearchText.Fold(supplier.LegalName) };
        parts.AddRange(supplier.Categories.Select(category => CategoryWords.TryGetValue(category, out var words) ? words : SearchText.Fold(category)));
        parts.AddRange(serviceNames.Select(SearchText.Fold));
        return string.Join(' ', parts);
    }

    private static bool Matches(string key, string[] words) =>
        words.All(word => key.Contains(word, StringComparison.Ordinal));
}
