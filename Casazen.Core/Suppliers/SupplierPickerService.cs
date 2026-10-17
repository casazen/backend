using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Suppliers;

/// <summary>
/// One published service of a supplier as the host sees it in the supplier picker of the marketplace (SR-09a): enough to
/// show "da 60 € a intervento · 2 h" and to tell the services of one supplier apart. It carries the id of the catalog row
/// (a request keeps it), and nothing the supplier's console keeps to itself: no slug, status, version, supplements or photos.
/// </summary>
/// <param name="Id">The catalog row.</param>
/// <param name="Name">The name of the service.</param>
/// <param name="Category">A code of <see cref="ServiceCategories"/>.</param>
/// <param name="DurationMinutes">Indicative duration; <c>null</c> when the supplier gave none.</param>
/// <param name="PriceFromCents">The "from" price in euro cents; <c>null</c> is "on quote".</param>
/// <param name="PriceUnit">What the price is for (job, hour, set, square meter).</param>
/// <param name="RequiresQuote">The host waits for the supplier's offer: the price is confirmed before the work.</param>
public sealed record SupplierPickerService(
    Guid Id,
    string Name,
    string Category,
    int? DurationMinutes,
    int? PriceFromCents,
    SupplierServicePriceUnit PriceUnit,
    bool RequiresQuote);

/// <summary>Limits of the supplier picker of the marketplace (SR-09a).</summary>
public static class SupplierPickerLimits
{
    /// <summary>Services listed on each supplier of the picker (the catalog order; the "from" price is the lowest of all of them).</summary>
    public const int MaxServices = 3;
}
