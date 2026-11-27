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

public class PublicSupplierSupplementDto
{
    public string Code { get; set; } = string.Empty;

    public string Label { get; set; } = string.Empty;

    public int AmountCents { get; set; }

    public string Per { get; set; } = string.Empty;

    public int? Max { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? IncludedSqm { get; set; }
}

/// <summary>
/// What a service offers for an urgent request (SP-04c): on only when the service <b>and</b> the supplier accept it, with the
/// surcharge the estimate adds. Off, with no surcharge, otherwise: the public never learns of a switch that has no effect.
/// </summary>
public class PublicSupplierUrgentDto
{
    public bool Enabled { get; set; }

    public int SurchargeCents { get; set; }
}

public class PublicSupplierServiceDto
{
    public string Slug { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Category { get; set; } = string.Empty;

    public string? Summary { get; set; }

    public int? PriceFromCents { get; set; }

    public SupplierServicePriceUnit PriceUnit { get; set; }

    /// <summary>
    /// Whether the prices (base and supplements) include VAT, <b>as the supplier declared it</b> (decision D4). False is not
    /// "VAT excluded": it means nothing is promised. CasaZen adds and removes no VAT.
    /// </summary>
    public bool PricesIncludeVat { get; set; }

    public bool RequiresQuote { get; set; }

    public int? DurationMinutes { get; set; }

    public IReadOnlyList<string> Included { get; set; } = [];

    public IReadOnlyList<string> Excluded { get; set; } = [];

    public IReadOnlyList<string> PhotoUrls { get; set; } = [];

    public PublicSupplierUrgentDto Urgent { get; set; } = new();

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? NextSlotUtc { get; set; }
}

public class PublicSupplierServiceDetailDto : PublicSupplierServiceDto
{
    public string? Description { get; set; }

    public IReadOnlyList<PublicSupplierSupplementDto> Supplements { get; set; } = [];
}

public class PublicSupplierServiceListResponse
{
    public IReadOnlyList<PublicSupplierServiceDetailDto> Items { get; set; } = [];

    public int Total { get; set; }
}

public class PublicSlotDto
{
    public DateTime StartUtc { get; set; }

    public DateTime EndUtc { get; set; }

    public DateTimeOffset StartLocal { get; set; }

    public DateTimeOffset EndLocal { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Urgent { get; set; }
}

public class PublicSlotDayDto
{
    public DateOnly Date { get; set; }

    public bool Available { get; set; }

    public IReadOnlyList<PublicSlotDto> Slots { get; set; } = [];
}

public class PublicSlotsResponse
{
    public string Service { get; set; } = string.Empty;

    public int DurationMinutes { get; set; }

    public string TimeZone { get; set; } = RomeCalendar.TimeZoneId;

    public DateOnly BookableUntil { get; set; }

    public IReadOnlyList<PublicSlotDayDto> Days { get; set; } = [];

    /// <summary>
    /// True when these are the urgent slots (SP-04c, <c>urgent=true</c>): today and tomorrow, in the supplier's urgent window, from
    /// 30 minutes from now; <see cref="BookableUntil"/> is then tomorrow, and <see cref="Days"/> is empty for a service or a supplier
    /// that does not accept urgent requests. Left out of the normal slots, which are as they always were.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Urgent { get; set; }
}

public class PublicQuoteOptionRequest
{
    [MaxLength(SupplierServiceCatalogLimits.SupplementCodeMaxLength)]
    public string? Code { get; set; }

    public int? Quantity { get; set; }
}

public class PublicQuoteRequest
{
    [MaxLength(SupplierServiceCatalogLimits.SlugMaxLength)]
    public string? Service { get; set; }

    public int? Quantity { get; set; }

    public int? SurfaceSqm { get; set; }

    [MaxLength(PublicShowcaseLimits.QuoteMaxOptions)]
    public List<PublicQuoteOptionRequest?>? Options { get; set; }

    /// <summary>Where the work is: the ISTAT code or the name of the comune. The supplier covers comuni; left out: nothing is checked.</summary>
    [MaxLength(PublicShowcaseLimits.QuoteComuneMaxLength)]
    public string? Comune { get; set; }

    [MaxLength(PublicShowcaseLimits.QuotePostalCodeMaxLength)]
    public string? PostalCode { get; set; }

    /// <summary>
    /// The customer asks for an urgent slot (SP-04c): the estimate gets one more line, the surcharge of the service
    /// (<c>kind: Urgent</c>), inside the total. 422 <c>supplier_quote_invalid</c> on the field <c>urgent</c> for a service that does not
    /// offer urgent requests (see <c>urgent</c> of the service). Left out or false: the estimate as it always was.
    /// </summary>
    public bool Urgent { get; set; }
}

public class PublicQuoteLineDto
{
    public SupplierQuoteLineKind Kind { get; set; }

    public string? Code { get; set; }

    public string Label { get; set; } = string.Empty;

    public string? Per { get; set; }

    public int Quantity { get; set; }

    public int UnitAmountCents { get; set; }

    public int AmountCents { get; set; }
}

public class PublicQuoteResponse
{
    public string Service { get; set; } = string.Empty;

    public string ServiceName { get; set; } = string.Empty;

    public string Currency { get; set; } = "EUR";

    public bool PricesIncludeVat { get; set; }

    public SupplierQuoteOutcome Outcome { get; set; }

    public SupplierQuoteReason? Reason { get; set; }

    public bool IsEstimate { get; set; }

    public bool RequiresQuote { get; set; }

    /// <summary>The total in euro cents; <c>null</c> unless <see cref="Outcome"/> is <c>Estimate</c>.</summary>
    public int? TotalCents { get; set; }

    public IReadOnlyList<PublicQuoteLineDto> Lines { get; set; } = [];

    public PublicQuoteCoverage Coverage { get; set; }

    public string? PostalCode { get; set; }
}

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
            Urgent = slots.Urgent,
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
                            Urgent = slots.Urgent,
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
            request.PostalCode,
            request.Urgent);

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
        dto.Urgent = new PublicSupplierUrgentDto { Enabled = service.Urgent.Enabled, SurchargeCents = service.Urgent.SurchargeCents };
        return dto;
    }

    private static DateTime AsUtc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);

    private static DateTimeOffset InRome(DateTime utc) =>
        TimeZoneInfo.ConvertTime(new DateTimeOffset(AsUtc(utc)), RomeCalendar.TimeZone);
}
