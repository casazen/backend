using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Suppliers;

/// <summary>
/// The price estimate of a service for what the customer chose (SP-09, <c>gap/05</c> §4.1-§4.2): a <b>pure function</b>, no
/// clock, no database, so every rule below is a table of cases. The same function will price the booking (SP-10), so the
/// total the customer saw is the total the request carries.
/// </summary>
/// <remarks>
/// <para><b>The base price.</b> The service's "from" price times the quantity of its unit: nothing to choose for a price
/// <c>PerJob</c> (quantity 1); the hours of a price <c>PerHour</c> (not said: the indicative duration of the service rounded
/// <b>up</b> to whole hours, at least 1); the sets of a price <c>PerSet</c> (not said: 1); the square meters of a price
/// <c>PerSquareMeter</c> (always said).</para>
/// <para><b>The supplements</b>, in the order the supplier wrote them, each only when it has units:
/// <c>flat</c> (a fixed amount, once: picked or not), <c>bathroom</c> (per extra bathroom), <c>set</c> (per set), <c>hour</c>
/// (per extra hour) take the quantity the customer picked, at most <c>max</c> (a missing <c>max</c> is no limit, up to
/// <see cref="PublicShowcaseLimits.QuoteMaxQuantity"/>); <c>sqm30</c> (per 30 m²) is <b>not picked</b>: it follows the surface
/// and counts the blocks of <see cref="PublicShowcaseLimits.QuoteSurfaceStepSqm"/> m² above
/// <see cref="PublicShowcaseLimits.QuoteIncludedSurfaceSqm"/> m², <b>a started block counts as a whole one</b> (61 m² is one
/// block, 90 m² is one, 91 m² is two). A surface that needs more blocks than the supplement's <c>max</c> is not an error: the
/// supplier prices a bigger home, so the answer is "on quote".</para>
/// <para><b>Money.</b> Integer euro cents, no fraction anywhere: a line is <c>quantity × unit amount</c>, the total is the
/// sum of the lines. The only roundings are the two "up" ones above (whole hours, started 30 m² blocks). A total above the
/// highest price the catalog accepts (<see cref="SupplierServiceCatalogLimits.MaxAmountCents"/>) is not promised: "on quote".
/// <b>VAT</b> is never computed: the answer carries the supplier's declaration (<see cref="SupplierPublicService.PricesIncludeVat"/>).</para>
/// <para><b>No total, no error.</b> A service on quote or without a price, a place outside the supplier's zones, a surface
/// over the limit, an amount over the limit: the answer is a <see cref="SupplierQuote"/> with no total and the reason. Only a
/// value that does not fit the service (an unknown supplement, a quantity over <c>max</c>...) is a
/// <see cref="SupplierQuoteRuleException"/>.</para>
/// </remarks>
public static class SupplierQuoteCalculator
{
    /// <summary>
    /// Checks <paramref name="request"/> against <paramref name="service"/> and returns the choices ready to be priced. Every
    /// value that is not valid is collected and refused together (422 <see cref="SupplierQuoteErrors.Invalid"/> naming all the
    /// fields). The checks do not depend on the outcome: a request that is wrong is wrong also for a service on quote.
    /// </summary>
    /// <exception cref="SupplierQuoteRuleException">The fields that are not valid.</exception>
    public static SupplierQuoteChoices Validate(SupplierPublicService service, SupplierQuoteRequest request)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(request);

        var invalid = new List<string>();
        var quantity = ReadQuantity(service, request.Quantity, invalid);
        var surface = ReadSurface(request.SurfaceSqm, invalid);
        var picks = ReadPicks(service, request.Options, invalid);
        var place = ReadPlace(request.Comune, invalid);
        var postalCode = ReadPostalCode(request.PostalCode, invalid);

        if (invalid.Count > 0)
            throw SupplierQuoteErrors.InvalidFields(invalid);

        return new SupplierQuoteChoices(quantity, surface, picks, place, postalCode);
    }

    /// <summary>
    /// The estimate. <paramref name="insideArea"/> is the answer of the zones check: <c>false</c> outside, <c>true</c> inside,
    /// <c>null</c> when no place was given (nothing is known, so nothing is refused). Order of the answers: outside the
    /// supplier's zones, service on quote, service without a price, then the sum.
    /// </summary>
    public static SupplierQuote Calculate(SupplierPublicService service, SupplierQuoteChoices choices, bool? insideArea)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(choices);

        var vat = service.PricesIncludeVat;
        if (insideArea == false)
            return Declined(SupplierQuoteOutcome.OutsideArea, SupplierQuoteReason.OutsideArea, vat);
        if (service.RequiresQuote)
            return Declined(SupplierQuoteOutcome.OnQuote, SupplierQuoteReason.RequiresQuote, vat);
        if (service.PriceFromCents is not > 0)
            return Declined(SupplierQuoteOutcome.OnQuote, SupplierQuoteReason.NoPrice, vat);

        var unitAmount = service.PriceFromCents.Value;
        var baseQuantity = BaseQuantity(service, choices);
        var lines = new List<(SupplierQuoteLineKind Kind, string? Code, string Label, string? Per, int Quantity, int Unit, long Amount)>
        {
            (SupplierQuoteLineKind.Base, null, service.Name, null, baseQuantity, unitAmount, (long)unitAmount * baseQuantity),
        };

        foreach (var supplement in service.Supplements)
        {
            var units = UnitsOf(supplement, choices);
            if (units == 0)
                continue;

            if (supplement.Per == SupplierServiceSupplementUnits.Sqm30 && supplement.Max is { } most && units > most)
                return Declined(SupplierQuoteOutcome.OnQuote, SupplierQuoteReason.SurfaceOverLimit, vat);

            lines.Add((SupplierQuoteLineKind.Supplement, supplement.Code, supplement.Label, supplement.Per, units, supplement.AmountCents, (long)supplement.AmountCents * units));
        }

        var total = lines.Sum(line => line.Amount);
        if (total > SupplierServiceCatalogLimits.MaxAmountCents)
            return Declined(SupplierQuoteOutcome.OnQuote, SupplierQuoteReason.AmountOverLimit, vat);

        // Every line is at most the total, which is within the limit: the amounts fit an int.
        return new SupplierQuote(
            SupplierQuoteOutcome.Estimate,
            null,
            (int)total,
            lines.Select(line => new SupplierQuoteLine(line.Kind, line.Code, line.Label, line.Per, line.Quantity, line.Unit, (int)line.Amount)).ToList(),
            vat);
    }

    /// <summary>
    /// The 30 m² blocks a surface adds to the price: none up to <see cref="PublicShowcaseLimits.QuoteIncludedSurfaceSqm"/> m²,
    /// then one for every started block of <see cref="PublicShowcaseLimits.QuoteSurfaceStepSqm"/> m² (rounded up).
    /// </summary>
    public static int SurfaceBlocks(int surfaceSqm)
    {
        var above = surfaceSqm - PublicShowcaseLimits.QuoteIncludedSurfaceSqm;
        return above <= 0 ? 0 : (above + PublicShowcaseLimits.QuoteSurfaceStepSqm - 1) / PublicShowcaseLimits.QuoteSurfaceStepSqm;
    }

    /// <summary>The hours of a price per hour when the customer says none: the indicative duration rounded up to whole hours, at least one.</summary>
    public static int DefaultHours(int? durationMinutes) =>
        durationMinutes is > 0
            ? Math.Clamp((durationMinutes.Value + 59) / 60, 1, PublicShowcaseLimits.QuoteMaxQuantity)
            : 1;

    private static SupplierQuote Declined(SupplierQuoteOutcome outcome, SupplierQuoteReason reason, bool pricesIncludeVat) =>
        new(outcome, reason, null, [], pricesIncludeVat);

    private static int BaseQuantity(SupplierPublicService service, SupplierQuoteChoices choices) =>
        service.PriceUnit switch
        {
            SupplierServicePriceUnit.PerHour => choices.Quantity ?? DefaultHours(service.DurationMinutes),
            SupplierServicePriceUnit.PerSet => choices.Quantity ?? 1,
            SupplierServicePriceUnit.PerSquareMeter => choices.Quantity ?? 1,
            _ => 1,
        };

    /// <summary>The units a supplement adds: the surface's blocks for <c>sqm30</c>, what the customer picked for the others.</summary>
    private static int UnitsOf(SupplierServiceSupplement supplement, SupplierQuoteChoices choices)
    {
        if (supplement.Per == SupplierServiceSupplementUnits.Sqm30)
            return choices.SurfaceSqm is { } surface ? SurfaceBlocks(surface) : 0;

        var pick = choices.Picks.FirstOrDefault(p => string.Equals(p.Supplement.Code, supplement.Code, StringComparison.Ordinal));
        return pick?.Quantity ?? 0;
    }

    // ─── Reading the request ─────────────────────────────────────────────────────

    private static int? ReadQuantity(SupplierPublicService service, int? requested, List<string> invalid)
    {
        switch (service.PriceUnit)
        {
            case SupplierServicePriceUnit.PerJob:
                // A price per job has no quantity to choose: leaving it out and saying 1 are the same thing.
                if (requested is not null and not 1)
                    AddOnce(invalid, SupplierQuoteFields.Quantity);
                return null;

            case SupplierServicePriceUnit.PerSquareMeter:
                // The surface to price is the quantity: it has to be said.
                if (requested is not { } surface || !InQuantityRange(surface))
                    AddOnce(invalid, SupplierQuoteFields.Quantity);
                return requested;

            default:
                if (requested is { } units && !InQuantityRange(units))
                    AddOnce(invalid, SupplierQuoteFields.Quantity);
                return requested;
        }
    }

    private static int? ReadSurface(int? surface, List<string> invalid)
    {
        if (surface is { } value && value is < 1 or > PublicShowcaseLimits.QuoteMaxSurfaceSqm)
            AddOnce(invalid, SupplierQuoteFields.SurfaceSqm);
        return surface;
    }

    private static List<SupplierQuotePick> ReadPicks(
        SupplierPublicService service,
        IReadOnlyList<SupplierQuoteOption?>? options,
        List<string> invalid)
    {
        var picks = new List<SupplierQuotePick>();
        if (options is null || options.Count == 0)
            return picks;

        if (options.Count > PublicShowcaseLimits.QuoteMaxOptions)
        {
            AddOnce(invalid, SupplierQuoteFields.Options);
            return picks;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < options.Count; i++)
        {
            var path = $"{SupplierQuoteFields.Options}[{i}]";
            var option = options[i];
            var code = option?.Code?.Trim().ToLowerInvariant();
            var supplement = string.IsNullOrEmpty(code)
                ? null
                : service.Supplements.FirstOrDefault(s => string.Equals(s.Code, code, StringComparison.Ordinal));

            // Unknown, repeated, or a supplement that follows the surface instead of being picked.
            if (supplement is null || !seen.Add(code!) || supplement.Per == SupplierServiceSupplementUnits.Sqm30)
            {
                AddOnce(invalid, $"{path}.code");
                continue;
            }

            var quantity = option!.Quantity ?? 1;
            var most = supplement.Per == SupplierServiceSupplementUnits.Flat
                ? 1
                : Math.Min(supplement.Max ?? PublicShowcaseLimits.QuoteMaxQuantity, PublicShowcaseLimits.QuoteMaxQuantity);
            if (quantity < 1 || quantity > most)
            {
                AddOnce(invalid, $"{path}.quantity");
                continue;
            }

            picks.Add(new SupplierQuotePick(supplement, quantity));
        }

        return picks;
    }

    private static ComuneTarget? ReadPlace(string? comune, List<string> invalid)
    {
        var text = comune?.Trim();
        if (string.IsNullOrEmpty(text))
            return null;

        if (text.Length > PublicShowcaseLimits.QuoteComuneMaxLength || text.Any(char.IsControl))
        {
            AddOnce(invalid, SupplierQuoteFields.Comune);
            return null;
        }

        return ComuneTarget.FromInput(text);
    }

    private static string? ReadPostalCode(string? postalCode, List<string> invalid)
    {
        var text = postalCode?.Trim();
        if (string.IsNullOrEmpty(text))
            return null;

        if (text.Length != 5 || !text.All(c => c is >= '0' and <= '9'))
        {
            AddOnce(invalid, SupplierQuoteFields.PostalCode);
            return null;
        }

        return text;
    }

    private static bool InQuantityRange(int quantity) => quantity is >= 1 and <= PublicShowcaseLimits.QuoteMaxQuantity;

    private static void AddOnce(List<string> fields, string field)
    {
        if (!fields.Contains(field, StringComparer.Ordinal))
            fields.Add(field);
    }
}
