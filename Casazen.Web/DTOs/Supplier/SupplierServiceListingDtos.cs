using System.ComponentModel.DataAnnotations;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Suppliers;

namespace Casazen.Web.DTOs.Supplier;

// ─── Requests ────────────────────────────────────────────────────────────────

/// <summary>
/// Body of <c>POST api/supplier/services</c> and, with <see cref="UpdateSupplierServiceRequest.Version"/>, of
/// <c>PUT api/supplier/services/{id}</c>: the content of a service. The attributes only stop an oversized body (a text or a
/// list longer than its limit: 400 <c>validation_error</c>); every value, a missing name included, is checked by
/// <see cref="SupplierServiceListingRules"/> (422 <c>supplier_service_invalid</c> with the fields at fault). A field left
/// out takes its default: this is a replacement.
/// </summary>
public class SaveSupplierServiceRequest
{
    [MaxLength(SupplierServiceCatalogLimits.NameMaxLength)]
    public string Name { get; set; } = string.Empty;

    /// <summary>A code of <c>GET /api/service-categories</c>; anything else is 422 <c>invalid_service_category</c>.</summary>
    [MaxLength(SupplierServiceCatalogLimits.CategoryMaxLength)]
    public string Category { get; set; } = string.Empty;

    [MaxLength(SupplierServiceCatalogLimits.SummaryMaxLength)]
    public string? Summary { get; set; }

    [MaxLength(SupplierServiceCatalogLimits.DescriptionMaxLength)]
    public string? Description { get; set; }

    /// <summary>The "from" price in cents (1 to 10,000,000); <c>null</c> is "on quote" (together with <see cref="RequiresQuote"/> to publish).</summary>
    public int? PriceFromCents { get; set; }

    /// <summary><c>PerJob</c> (default), <c>PerHour</c>, <c>PerSet</c> or <c>PerSquareMeter</c>.</summary>
    public SupplierServicePriceUnit PriceUnit { get; set; } = SupplierServicePriceUnit.PerJob;

    /// <summary>Whether the prices (base and supplements) include VAT. False when left out: nothing is promised.</summary>
    public bool PricesIncludeVat { get; set; }

    /// <summary>The price is confirmed with an offer before the work: the service can be published without a price.</summary>
    public bool RequiresQuote { get; set; }

    /// <summary>Indicative duration in minutes (5 to 1,440). Required to publish.</summary>
    public int? DurationMinutes { get; set; }

    /// <summary>Shortest notice in hours (0 to 720); <c>null</c> is the supplier's default notice.</summary>
    public int? MinNoticeHours { get; set; }

    /// <summary>The days the service is offered on (<c>Monday</c> ... <c>Sunday</c>); left out is every day.</summary>
    [MaxLength(7)]
    public List<DayOfWeek>? Weekdays { get; set; }

    [MaxLength(SupplierServiceCatalogLimits.MaxSupplements)]
    public List<SupplierServiceSupplementDto?>? Supplements { get; set; }

    /// <summary>What the price includes: at most 10 lines once blank and repeated ones are dropped (40 in the body).</summary>
    [MaxLength(SupplierServiceCatalogLimits.MaxListLinesInBody)]
    public List<string?>? Included { get; set; }

    /// <summary>What the price does not include: at most 10 lines once blank and repeated ones are dropped (40 in the body).</summary>
    [MaxLength(SupplierServiceCatalogLimits.MaxListLinesInBody)]
    public List<string?>? Excluded { get; set; }

    /// <summary>
    /// Only on <c>PUT</c>: the photos to keep, in the new order (the first is the cover), a subset of the service's own
    /// photos: this is how a photo is removed or moved. Left out (<c>null</c>) keeps the photos as they are. A photo is
    /// added with <c>POST .../photos</c>; on <c>POST</c> of a new service the list must be empty.
    /// </summary>
    [MaxLength(SupplierServiceCatalogLimits.MaxPhotos)]
    public List<string?>? PhotoUrls { get; set; }

    /// <summary>Position in the catalog (0 to 9,999); left out keeps it (a new service goes last).</summary>
    public int? SortOrder { get; set; }
}

/// <summary>Body of <c>PUT api/supplier/services/{id}</c>: the content plus the version the client read.</summary>
public class UpdateSupplierServiceRequest : SaveSupplierServiceRequest
{
    /// <summary>
    /// The <c>version</c> of the service the client read (<c>xmin</c>, it changes with every update of the row). Another
    /// one is a 409 <c>supplier_service_changed</c>: reload and apply the change again.
    /// </summary>
    [Required]
    public uint? Version { get; set; }
}

/// <summary>
/// A supplement of a service, in the structured form the price estimate of the booking uses (SP-02):
/// <c>{ code, label, amountCents, per, max }</c>.
/// </summary>
public class SupplierServiceSupplementDto
{
    /// <summary>Stable identifier inside the service: lowercase letters, digits and single hyphens, at most 40 characters.</summary>
    [MaxLength(SupplierServiceCatalogLimits.SupplementCodeMaxLength)]
    public string Code { get; set; } = string.Empty;

    [MaxLength(SupplierServiceCatalogLimits.SupplementLabelMaxLength)]
    public string Label { get; set; } = string.Empty;

    /// <summary>The amount of one unit in cents (1 to 10,000,000).</summary>
    public int AmountCents { get; set; }

    /// <summary>What one unit is: <c>flat</c>, <c>bathroom</c>, <c>sqm30</c>, <c>set</c> or <c>hour</c>.</summary>
    [MaxLength(20)]
    public string Per { get; set; } = string.Empty;

    /// <summary>Most units a customer can pick (1 to 99); <c>null</c> for no limit.</summary>
    public int? Max { get; set; }
}

// ─── Responses ───────────────────────────────────────────────────────────────

/// <summary>A service of the supplier's catalog.</summary>
public class SupplierServiceDto
{
    public Guid Id { get; set; }

    /// <summary>Unique among the supplier's services; the last part of the public address of the service.</summary>
    public string Slug { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Category { get; set; } = string.Empty;

    public string? Summary { get; set; }

    public string? Description { get; set; }

    public int? PriceFromCents { get; set; }

    public SupplierServicePriceUnit PriceUnit { get; set; }

    public bool PricesIncludeVat { get; set; }

    public bool RequiresQuote { get; set; }

    public int? DurationMinutes { get; set; }

    public int? MinNoticeHours { get; set; }

    /// <summary>The days the service is offered on, Monday first.</summary>
    public IReadOnlyList<DayOfWeek> Weekdays { get; set; } = [];

    public IReadOnlyList<SupplierServiceSupplementDto> Supplements { get; set; } = [];

    public IReadOnlyList<string> Included { get; set; } = [];

    public IReadOnlyList<string> Excluded { get; set; } = [];

    /// <summary>Absolute photo URLs in display order; the first is the cover.</summary>
    public IReadOnlyList<string> PhotoUrls { get; set; } = [];

    /// <summary><c>Draft</c>, <c>Active</c> or <c>Paused</c>.</summary>
    public SupplierServiceListingStatus Status { get; set; }

    public int SortOrder { get; set; }

    /// <summary>True when the service meets the publication requirements (name, category, duration, price or quote).</summary>
    public bool Publishable { get; set; }

    /// <summary>What a service that is not publishable lacks: <c>name</c>, <c>category</c>, <c>durationMinutes</c>, <c>priceFromCents</c>.</summary>
    public IReadOnlyList<string> MissingForPublication { get; set; } = [];

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    /// <summary>To send back with the next <c>PUT</c> (and the one to use after a photo upload, which changes it).</summary>
    public uint Version { get; set; }
}

/// <summary>Body of <c>GET api/supplier/services</c>.</summary>
public class SupplierServiceListResponse
{
    /// <summary>The services that are not deleted, by position then creation date.</summary>
    public IReadOnlyList<SupplierServiceDto> Items { get; set; } = [];

    public int Total { get; set; }

    /// <summary>How many services a supplier can have (<c>total</c> at the limit: no more can be created).</summary>
    public int Limit { get; set; } = SupplierServiceCatalogLimits.MaxServicesPerSupplier;
}

/// <summary>Maps the entity of a service to its response and a request to the input of the rules.</summary>
public static class SupplierServiceMapper
{
    public static SupplierServiceDto ToDto(SupplierServiceListing listing)
    {
        var missing = SupplierServiceListingRules.MissingForPublication(listing);
        return new SupplierServiceDto
        {
            Id = listing.Id,
            Slug = listing.Slug,
            Name = listing.Name,
            Category = listing.Category,
            Summary = listing.Summary,
            Description = listing.Description,
            PriceFromCents = listing.PriceFromCents,
            PriceUnit = listing.PriceUnit,
            PricesIncludeVat = listing.PricesIncludeVat,
            RequiresQuote = listing.RequiresQuote,
            DurationMinutes = listing.DurationMinutes,
            MinNoticeHours = listing.MinNoticeHours,
            Weekdays = SupplierServiceWeekdays.FromMask(listing.WeekdaysMask),
            Supplements = SupplierServiceListingJson.ReadSupplements(listing.SupplementsJson)
                .Select(s => new SupplierServiceSupplementDto
                {
                    Code = s.Code,
                    Label = s.Label,
                    AmountCents = s.AmountCents,
                    Per = s.Per,
                    Max = s.Max,
                })
                .ToList(),
            Included = SupplierServiceListingJson.ReadStrings(listing.IncludedJson),
            Excluded = SupplierServiceListingJson.ReadStrings(listing.ExcludedJson),
            PhotoUrls = SupplierServiceListingJson.ReadStrings(listing.PhotoUrlsJson),
            Status = listing.Status,
            SortOrder = listing.SortOrder,
            Publishable = missing.Count == 0,
            MissingForPublication = missing,
            CreatedAt = listing.CreatedAt,
            UpdatedAt = listing.UpdatedAt,
            Version = listing.Version,
        };
    }

    public static SupplierServiceListingInput ToInput(SaveSupplierServiceRequest request) =>
        new(
            request.Name,
            request.Category,
            request.Summary,
            request.Description,
            request.PriceFromCents,
            request.PriceUnit,
            request.PricesIncludeVat,
            request.RequiresQuote,
            request.DurationMinutes,
            request.MinNoticeHours,
            request.Weekdays,
            request.Supplements?
                .Select(s => s is null
                    ? null
                    : new SupplierServiceSupplement(s.Code, s.Label, s.AmountCents, s.Per, s.Max))
                .ToList(),
            request.Included,
            request.Excluded,
            request.PhotoUrls,
            request.SortOrder);
}
