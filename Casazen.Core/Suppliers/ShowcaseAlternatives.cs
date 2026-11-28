using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;

namespace Casazen.Core.Suppliers;

public static class ShowcaseAlternativesLimits
{
    public const int Max = 3;

    public const int MaxCandidates = 12;
}

/// <summary>What is looked for: the comune and the category of the request that lapsed, and the supplier that did not answer (never offered again).</summary>
/// <param name="ExcludedSupplierOrgId">The supplier of the request.</param>
/// <param name="Category">The category of the request (a <see cref="ServiceCategories"/> code).</param>
/// <param name="ComuneIstat">ISTAT code of the comune of the work, when the customer chose it from the official list.</param>
/// <param name="City">The comune as the customer wrote it.</param>
public sealed record ShowcaseAlternativesQuery(Guid ExcludedSupplierOrgId, string Category, string? ComuneIstat, string? City);

/// <summary>The service of an alternative that corresponds to the request: the one in the same category, with its "from" price.</summary>
/// <param name="Slug">The slug of the service on the supplier's showcase (<c>/fornitori/{supplier}/servizi/{slug}</c>).</param>
/// <param name="Name">The name the supplier gave it.</param>
/// <param name="FromPriceCents">The "from" price in euro cents; <c>null</c> is "on quote".</param>
/// <param name="PriceUnit">What the price is for.</param>
/// <param name="RequiresQuote">The customer waits for the supplier's offer: no estimate is given.</param>
public sealed record ShowcaseAlternativeService(string Slug, string Name, int? FromPriceCents, SupplierServicePriceUnit PriceUnit, bool RequiresQuote);

/// <summary>
/// Another supplier offered to the customer of a request nobody answered, as the public may know it: only what its showcase says about itself
/// and what is measured. <b>No score, no id, no contact, no address</b>: the way to it is its public page (<paramref name="Slug"/>).
/// </summary>
/// <param name="Name">The name of the supplier, as on its showcase.</param>
/// <param name="Slug">The slug of its showcase.</param>
/// <param name="CoverUrl">The cover photo of its showcase; <c>null</c> when it has none.</param>
/// <param name="CoverAlt">The alternative text of the cover.</param>
/// <param name="Verified">«Verificato» (decision D11), as on its showcase.</param>
/// <param name="MedianResponseMinutes">The typical time to answer, measured (<see cref="SupplierResponseTimes"/>); <c>null</c> until it is.</param>
/// <param name="Service">The service that corresponds to the request; <c>null</c> when the supplier has none published in that category.</param>
public sealed record ShowcaseAlternative(
    string Name,
    string Slug,
    string? CoverUrl,
    string? CoverAlt,
    bool Verified,
    int? MedianResponseMinutes,
    ShowcaseAlternativeService? Service);

public static class ShowcaseAlternativeRules
{
    /// <summary>
    /// Whether a request that was closed shows the customer other suppliers: only when <b>CasaZen closed it because the supplier did not answer
    /// in time</b>. Not when the supplier refused it or took it back (it answered), not when the customer closed it or let an offer lapse, not
    /// while it is open. The alternatives are the way out of a silence, not a list of suppliers behind every request.
    /// </summary>
    public static bool Offers(ServiceRequestStatus status, ServiceRequestActorParty? cancelledBy, string? cancellationReason) =>
        status == ServiceRequestStatus.Annullato
        && cancelledBy == ServiceRequestActorParty.System
        && cancellationReason == ServiceRequestCancellationReasons.NoResponse;

    /// <summary>
    /// Whether a supplier can be offered: it has a published showcase (<paramref name="page"/> is <c>null</c> otherwise), not paused, that takes bookings now
    /// (<c>bookingsOpen</c>, decision VT-D7).
    /// The supplier is <c>Active</c> and covers the comune and the category before this is asked (the ranking only lists those).
    /// </summary>
    public static bool Qualifies(PublishedShowcasePage? page) => page is { Paused: false, BookingsOpen: true };

    public static ShowcaseAlternativeService? ServiceFor(IEnumerable<SupplierPublicService> services, string category)
    {
        ArgumentNullException.ThrowIfNull(services);

        var chosen = services
            .Where(service => string.Equals(service.Category, category, StringComparison.Ordinal))
            .OrderBy(service => service.PriceFromCents is null)
            .ThenBy(service => service.PriceFromCents)
            .ThenBy(service => service.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        return chosen is null
            ? null
            : new ShowcaseAlternativeService(chosen.Slug, chosen.Name, chosen.PriceFromCents, chosen.PriceUnit, chosen.RequiresQuote);
    }
}
