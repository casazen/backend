using System.Collections.Frozen;
using System.Globalization;
using Casazen.Core.Search;
using Casazen.Core.Suppliers;
using Casazen.Infrastructure.Email.Templates;

namespace Casazen.Web.DTOs.Supplier;

public enum SupplierPickerSort
{
    /// <summary>
    /// The best-ranked supplier first (default, SP-REL): the score of <see cref="SupplierRanking"/>, that is the load of the supplier,
    /// what it wrote about itself and its reliability (the replies in time and the jobs without disputes raise it, the missed deadlines
    /// and the disputes lower it). The score itself is never sent. Ties are broken by name.
    /// </summary>
    Recommended,

    Name,

    Price,

    Response,
}

public static class SupplierPickerSearch
{
    private static readonly CultureInfo Italian = CultureInfo.GetCultureInfo("it-IT");
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en");

    private static readonly FrozenDictionary<string, string> CategoryWords = ServiceCategories.All.ToFrozenDictionary(
        code => code,
        code => SearchText.Fold(
            $"{code} {EmailTemplates.ServiceCategoryLabel(Italian, code)} {EmailTemplates.ServiceCategoryLabel(English, code)}"),
        StringComparer.Ordinal);

    public static bool TryParseSort(string? value, out SupplierPickerSort sort)
    {
        sort = SupplierPickerSort.Recommended;
        if (string.IsNullOrWhiteSpace(value))
            return true;

        switch (value.Trim().ToLowerInvariant())
        {
            case "recommended":
                return true;
            case "name":
                sort = SupplierPickerSort.Name;
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
    /// first ones): without them the search looks at the services the entries carry. <paramref name="scores"/> are the ranking scores of
    /// the suppliers (<see cref="SupplierRanking"/>, by org id) that <see cref="SupplierPickerSort.Recommended"/> orders by: a supplier
    /// without one counts as 0, so without scores the order is the one by name. In that order the first supplier is marked
    /// <see cref="SupplierPickerDto.Recommended"/> when it is strictly ahead of the second: with a tie nobody is "the" recommended one.
    /// </summary>
    public static List<SupplierPickerDto> Apply(
        IEnumerable<SupplierPickerDto> suppliers,
        string? q,
        SupplierPickerSort sort,
        IReadOnlyDictionary<Guid, IReadOnlyList<SupplierPickerService>>? services = null,
        IReadOnlyDictionary<Guid, int>? scores = null)
    {
        ArgumentNullException.ThrowIfNull(suppliers);

        var words = SearchText.Fold(q).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var matching = words.Length == 0
            ? suppliers
            : suppliers.Where(s => Matches(SearchKey(s, services), words));

        int ScoreOf(SupplierPickerDto supplier) => scores is not null && scores.TryGetValue(supplier.OrgId, out var score) ? score : 0;

        var ordered = sort switch
        {
            SupplierPickerSort.Recommended => matching.OrderByDescending(ScoreOf),
            SupplierPickerSort.Price => matching.OrderBy(s => s.PriceFromCents is null).ThenBy(s => s.PriceFromCents),
            SupplierPickerSort.Response => matching.OrderBy(s => s.MedianResponseMinutes is null).ThenBy(s => s.MedianResponseMinutes),
            _ => matching.OrderBy(_ => 0),
        };

        var list = ordered
            .ThenBy(s => s.LegalName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.OrgId)
            .ToList();

        if (sort == SupplierPickerSort.Recommended && scores is not null && list.Count > 1 && ScoreOf(list[0]) > ScoreOf(list[1]))
            list[0].Recommended = true;

        return list;
    }

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
