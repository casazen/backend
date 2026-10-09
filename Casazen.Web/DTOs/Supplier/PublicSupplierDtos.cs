using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Core.Utilities;

namespace Casazen.Web.DTOs.Supplier;

// The anonymous read side of a supplier's showcase (SP-09): services, free slots and price estimate. Nothing here has a field
// for a person (no phone, e-mail, VAT number, address, customer name or note), for the supplier's private calendar (no label,
// no kind of busy time, no reason why a day is closed) or for the console's bookkeeping (no id, no org, no status, no version).
// A test serializes the answers and searches them for those names (PublicSupplierShowcaseIntegrationTests).

// ─── Services ────────────────────────────────────────────────────────────────

/// <summary>
/// A supplement of a service as the public reads it: <c>{ code, label, amountCents, per, max }</c>, what the estimate adds up.
/// </summary>
public class PublicSupplierSupplementDto
{
    /// <summary>Stable identifier inside the service: what an estimate refers to.</summary>
    public string Code { get; set; } = string.Empty;

    public string Label { get; set; } = string.Empty;

    /// <summary>The amount of one unit, in euro cents.</summary>
    public int AmountCents { get; set; }

    /// <summary>
    /// What one unit is: <c>flat</c> (a fixed amount, once), <c>bathroom</c> (per extra bathroom), <c>sqm30</c> (per started
    /// 30 m² above <see cref="IncludedSqm"/>: it follows the surface of the estimate, it is not picked), <c>set</c> or <c>hour</c>
    /// (per extra hour).
    /// </summary>
    public string Per { get; set; } = string.Empty;

    /// <summary>Most units a customer can pick (for <c>sqm30</c>, the most 30 m² blocks the price covers); <c>null</c> for no limit.</summary>
    public int? Max { get; set; }

    /// <summary>
    /// Only for <c>sqm30</c>: the surface in square meters the base price already covers (the blocks count above it). Left out
    /// for the other units.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? IncludedSqm { get; set; }
}

/// <summary>
/// A published service of a supplier, as listed on its page (<c>GET api/public/suppliers/{slug}/services</c> and the
/// <c>services</c> of <c>GET api/public/suppliers/{slug}</c>): what the card shows.
/// </summary>
public class PublicSupplierServiceDto
{
    /// <summary>Unique among the supplier's services: the last part of <c>/fornitori/{supplierSlug}/servizi/{slug}</c>.</summary>
    public string Slug { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Category { get; set; } = string.Empty;

    public string? Summary { get; set; }

    /// <summary>The "from" price in euro cents; <c>null</c> is "on quote".</summary>
    public int? PriceFromCents { get; set; }

    /// <summary><c>PerJob</c>, <c>PerHour</c>, <c>PerSet</c> or <c>PerSquareMeter</c>.</summary>
    public SupplierServicePriceUnit PriceUnit { get; set; }

    /// <summary>
    /// Whether the prices (base and supplements) include VAT, <b>as the supplier declared it</b> (decision D4). False is not
    /// "VAT excluded": it means nothing is promised. CasaZen adds and removes no VAT.
    /// </summary>
    public bool PricesIncludeVat { get; set; }

    /// <summary>The customer asks and waits for the supplier's offer: no estimate is given.</summary>
    public bool RequiresQuote { get; set; }

    /// <summary>Indicative duration in minutes.</summary>
    public int? DurationMinutes { get; set; }

    /// <summary>What the price includes.</summary>
    public IReadOnlyList<string> Included { get; set; } = [];

    /// <summary>What the price does not include.</summary>
    public IReadOnlyList<string> Excluded { get; set; } = [];

    /// <summary>Absolute photo URLs in display order; the first is the cover.</summary>
    public IReadOnlyList<string> PhotoUrls { get; set; } = [];
}

/// <summary>One service with its description and the structured supplements the estimate is computed from.</summary>
public class PublicSupplierServiceDetailDto : PublicSupplierServiceDto
{
    public string? Description { get; set; }

    public IReadOnlyList<PublicSupplierSupplementDto> Supplements { get; set; } = [];
}

/// <summary>Body of <c>GET api/public/suppliers/{slug}/services</c>.</summary>
public class PublicSupplierServiceListResponse
{
    /// <summary>
    /// The published services in full, by the supplier's order: the same as the detail, with the structured supplements, so a
    /// booking form needs one request to offer every service and its options. (The page of the supplier carries the cards.)
    /// </summary>
    public IReadOnlyList<PublicSupplierServiceDetailDto> Items { get; set; } = [];

    public int Total { get; set; }
}

// ─── Slots ───────────────────────────────────────────────────────────────────

/// <summary>A free slot: the work can start at <see cref="StartUtc"/> and ends at <see cref="EndUtc"/>.</summary>
public class PublicSlotDto
{
    /// <summary>First instant, UTC.</summary>
    public DateTime StartUtc { get; set; }

    /// <summary>End, UTC.</summary>
    public DateTime EndUtc { get; set; }

    /// <summary>
    /// The same start on the clock of Rome, with its offset (<c>2026-10-20T09:00:00+02:00</c>): the offset also tells apart the
    /// two passes of the hour that happens twice when summer time ends.
    /// </summary>
    public DateTimeOffset StartLocal { get; set; }

    /// <summary>The same end on the clock of Rome, with its offset.</summary>
    public DateTimeOffset EndLocal { get; set; }
}

/// <summary>
/// One day of the window. <see cref="Available"/> false and no slot is all the public learns of a day without one: closed, on
/// leave, full or inside the notice look the same.
/// </summary>
public class PublicSlotDayDto
{
    /// <summary>The calendar day in Europe/Rome (<c>YYYY-MM-DD</c>).</summary>
    public DateOnly Date { get; set; }

    public bool Available { get; set; }

    /// <summary>The free slots in start order; empty when the day is not available.</summary>
    public IReadOnlyList<PublicSlotDto> Slots { get; set; } = [];
}

/// <summary>
/// Body of <c>GET api/public/suppliers/{slug}/slots?service=&amp;from=&amp;days=</c>: the free slots of a service. A slot shown
/// is not a promise (cached for 30 seconds; the booking recomputes it).
/// </summary>
public class PublicSlotsResponse
{
    /// <summary>Slug of the service the slots are for.</summary>
    public string Service { get; set; } = string.Empty;

    /// <summary>How long the work lasts: a slot is that long.</summary>
    public int DurationMinutes { get; set; }

    /// <summary>The time zone of <see cref="PublicSlotDayDto.Date"/> and of the local times: <c>Europe/Rome</c>.</summary>
    public string TimeZone { get; set; } = RomeCalendar.TimeZoneId;

    /// <summary>The last day that can be booked (today plus the supplier's horizon): the days after it are not listed.</summary>
    public DateOnly BookableUntil { get; set; }

    /// <summary>One entry for every day of the window, in date order. Empty when the window starts after <see cref="BookableUntil"/>.</summary>
    public IReadOnlyList<PublicSlotDayDto> Days { get; set; } = [];
}

// ─── Estimate ────────────────────────────────────────────────────────────────

/// <summary>A supplement the customer picked.</summary>
public class PublicQuoteOptionRequest
{
    /// <summary>The supplement's <c>code</c> (from the service). A <c>sqm30</c> supplement is not picked: it follows <c>surfaceSqm</c>.</summary>
    [MaxLength(SupplierServiceCatalogLimits.SupplementCodeMaxLength)]
    public string? Code { get; set; }

    /// <summary>How many units (default 1): a <c>flat</c> supplement is 1, the others up to its <c>max</c>.</summary>
    public int? Quantity { get; set; }
}

/// <summary>
/// Body of <c>POST api/public/suppliers/{slug}/quote</c>. The attributes only stop an oversized body (400
/// <c>validation_error</c>); every value, a missing service included, is checked against the service (422
/// <c>supplier_quote_invalid</c> with the fields at fault).
/// </summary>
public class PublicQuoteRequest
{
    /// <summary>Slug of the service (from <c>GET …/services</c>).</summary>
    [MaxLength(SupplierServiceCatalogLimits.SlugMaxLength)]
    public string? Service { get; set; }

    /// <summary>
    /// Units of the service's own price: hours (<c>PerHour</c>; left out: the indicative duration rounded up to whole hours),
    /// sets (<c>PerSet</c>; left out: 1) or square meters (<c>PerSquareMeter</c>; required). Not for a price <c>PerJob</c>.
    /// </summary>
    public int? Quantity { get; set; }

    /// <summary>Surface of the home in square meters: what the <c>sqm30</c> supplements count (above 60 m², a started block of 30 m² counts whole).</summary>
    public int? SurfaceSqm { get; set; }

    /// <summary>The supplements picked, at most 10, each once.</summary>
    [MaxLength(PublicShowcaseLimits.QuoteMaxOptions)]
    public List<PublicQuoteOptionRequest?>? Options { get; set; }

    /// <summary>Where the work is: the ISTAT code or the name of the comune. The supplier covers comuni; left out: nothing is checked.</summary>
    [MaxLength(PublicShowcaseLimits.QuoteComuneMaxLength)]
    public string? Comune { get; set; }

    /// <summary>Five digits. Checked for its shape and echoed; the supplier's zones are comuni, so it does not decide the coverage.</summary>
    [MaxLength(PublicShowcaseLimits.QuotePostalCodeMaxLength)]
    public string? PostalCode { get; set; }
}

/// <summary>One line of an estimate: <c>quantity × unitAmountCents = amountCents</c>.</summary>
public class PublicQuoteLineDto
{
    /// <summary><c>Base</c> (the service's price) or <c>Supplement</c>.</summary>
    public SupplierQuoteLineKind Kind { get; set; }

    /// <summary>The supplement's code; <c>null</c> for the base price.</summary>
    public string? Code { get; set; }

    /// <summary>The name of the service or the label of the supplement, as the supplier wrote it.</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>The unit of a supplement; <c>null</c> for the base price (its unit is the service's <c>priceUnit</c>).</summary>
    public string? Per { get; set; }

    public int Quantity { get; set; }

    public int UnitAmountCents { get; set; }

    public int AmountCents { get; set; }
}

/// <summary>
/// Body of <c>POST api/public/suppliers/{slug}/quote</c>: the estimate, or the reason there is none. Always 200 for a request
/// that fits the service: a service on quote and a place outside the supplier's zones are answers ("ask for a quote"), not
/// errors.
/// </summary>
public class PublicQuoteResponse
{
    /// <summary>Slug of the service.</summary>
    public string Service { get; set; } = string.Empty;

    /// <summary>The name of the service.</summary>
    public string ServiceName { get; set; } = string.Empty;

    /// <summary>Always <c>EUR</c>.</summary>
    public string Currency { get; set; } = "EUR";

    /// <summary>As the supplier declared it (decision D4); CasaZen computes no VAT.</summary>
    public bool PricesIncludeVat { get; set; }

    /// <summary><c>Estimate</c> (there is a total), <c>OnQuote</c> or <c>OutsideArea</c> (no total: the customer asks for a quote).</summary>
    public SupplierQuoteOutcome Outcome { get; set; }

    /// <summary>Why there is no total (<c>RequiresQuote</c>, <c>NoPrice</c>, <c>SurfaceOverLimit</c>, <c>AmountOverLimit</c>, <c>OutsideArea</c>); <c>null</c> for an estimate.</summary>
    public SupplierQuoteReason? Reason { get; set; }

    /// <summary>True when there is a total, which is an estimate: the supplier confirms the price before starting the work.</summary>
    public bool IsEstimate { get; set; }

    /// <summary>True when the customer has to ask for a quote and the supplier makes the offer ("su preventivo", or the supplier decides for this place).</summary>
    public bool RequiresQuote { get; set; }

    /// <summary>The total in euro cents; <c>null</c> unless <see cref="Outcome"/> is <c>Estimate</c>.</summary>
    public int? TotalCents { get; set; }

    /// <summary>The base price and the supplements that add up to the total; empty when there is no total.</summary>
    public IReadOnlyList<PublicQuoteLineDto> Lines { get; set; } = [];

    /// <summary><c>Unknown</c> (no comune given), <c>Covered</c> or <c>Outside</c> the supplier's zones.</summary>
    public PublicQuoteCoverage Coverage { get; set; }

    /// <summary>The postal code the customer gave, echoed; <c>null</c> when none.</summary>
    public string? PostalCode { get; set; }
}

// ─── Mapping ─────────────────────────────────────────────────────────────────

/// <summary>Maps the public views of the Core to the answers above, and the requests to the input of the estimate.</summary>
public static class PublicSupplierMapper
{
    public static PublicSupplierServiceDto ToSummaryDto(SupplierPublicService service) =>
        Fill(new PublicSupplierServiceDto(), service);

    public static PublicSupplierServiceDetailDto ToDetailDto(SupplierPublicService service)
    {
        var dto = Fill(new PublicSupplierServiceDetailDto(), service);
        dto.Description = service.Description;
        dto.Supplements = service.Supplements
            .Select(supplement => new PublicSupplierSupplementDto
            {
                Code = supplement.Code,
                Label = supplement.Label,
                AmountCents = supplement.AmountCents,
                Per = supplement.Per,
                Max = supplement.Max,
                IncludedSqm = supplement.Per == SupplierServiceSupplementUnits.Sqm30 ? PublicShowcaseLimits.QuoteIncludedSurfaceSqm : null,
            })
            .ToList();
        return dto;
    }

    public static PublicSlotsResponse ToDto(PublicSlots slots) =>
        new()
        {
            Service = slots.ServiceSlug,
            DurationMinutes = slots.DurationMinutes,
            TimeZone = RomeCalendar.TimeZoneId,
            BookableUntil = slots.BookableUntil,
            Days = slots.Days
                .Select(day => new PublicSlotDayDto
                {
                    Date = day.Date,
                    Available = day.Available,
                    Slots = day.Slots
                        .Select(slot => new PublicSlotDto
                        {
                            StartUtc = AsUtc(slot.StartUtc),
                            EndUtc = AsUtc(slot.EndUtc),
                            StartLocal = InRome(slot.StartUtc),
                            EndLocal = InRome(slot.EndUtc),
                        })
                        .ToList(),
                })
                .ToList(),
        };

    public static SupplierQuoteRequest ToInput(PublicQuoteRequest request) =>
        new(
            request.Quantity,
            request.SurfaceSqm,
            request.Options?.Select(option => option is null ? null : new SupplierQuoteOption(option.Code, option.Quantity)).ToList(),
            request.Comune,
            request.PostalCode);

    public static PublicQuoteResponse ToDto(PublicQuote result) =>
        new()
        {
            Service = result.Service.Slug,
            ServiceName = result.Service.Name,
            PricesIncludeVat = result.Quote.PricesIncludeVat,
            Outcome = result.Quote.Outcome,
            Reason = result.Quote.Reason,
            IsEstimate = result.Quote.IsEstimate,
            RequiresQuote = result.Quote.RequiresQuote,
            TotalCents = result.Quote.TotalCents,
            Lines = result.Quote.Lines
                .Select(line => new PublicQuoteLineDto
                {
                    Kind = line.Kind,
                    Code = line.Code,
                    Label = line.Label,
                    Per = line.Per,
                    Quantity = line.Quantity,
                    UnitAmountCents = line.UnitAmountCents,
                    AmountCents = line.AmountCents,
                })
                .ToList(),
            Coverage = result.Coverage,
            PostalCode = result.PostalCode,
        };

    private static T Fill<T>(T dto, SupplierPublicService service)
        where T : PublicSupplierServiceDto
    {
        dto.Slug = service.Slug;
        dto.Name = service.Name;
        dto.Category = service.Category;
        dto.Summary = service.Summary;
        dto.PriceFromCents = service.PriceFromCents;
        dto.PriceUnit = service.PriceUnit;
        dto.PricesIncludeVat = service.PricesIncludeVat;
        dto.RequiresQuote = service.RequiresQuote;
        dto.DurationMinutes = service.DurationMinutes;
        dto.Included = service.Included;
        dto.Excluded = service.Excluded;
        dto.PhotoUrls = service.PhotoUrls;
        return dto;
    }

    private static DateTime AsUtc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);

    private static DateTimeOffset InRome(DateTime utc) =>
        TimeZoneInfo.ConvertTime(new DateTimeOffset(AsUtc(utc)), RomeCalendar.TimeZone);
}
