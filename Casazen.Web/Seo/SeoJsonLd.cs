using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Unicode;
using Casazen.Core.Entities;

namespace Casazen.Web.Seo;

/// <summary>
/// schema.org JSON-LD of the public pages (BK-15). Only real data goes in: a value the database does not hold (rating,
/// check-in time, pets rule, price availability) is left out, never invented, and an empty value is never written. Every
/// public builder returns a complete object with its <c>@context</c>.
/// </summary>
public static class SeoJsonLd
{
    public const string Context = "https://schema.org";

    /// <summary>
    /// Serialization for a <c>&lt;script type="application/ld+json"&gt;</c> block: the built-in encoders always escape
    /// <c>&lt;</c>, <c>&gt;</c>, <c>&amp;</c>, <c>'</c> and <c>"</c>, so a text can never close the script element; letters
    /// are left as they are (all Unicode ranges), so the data stays readable.
    /// </summary>
    private static readonly JsonSerializerOptions ScriptSafeOptions = new()
    {
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
        WriteIndented = false,
    };

    public static string Serialize(JsonObject node) => node.ToJsonString(ScriptSafeOptions);

    /// <summary>The org on its landing page: name, public URL, logo and tagline, when it has them.</summary>
    public static JsonObject Organization(string name, string? url, string? logoUrl, string? description) =>
        Root("Organization",
            ("name", Text(name)),
            ("url", Text(url)),
            ("logo", Text(logoUrl)),
            ("description", Text(description)));

    /// <summary>
    /// A supplier's public showcase: a <c>ProfessionalService</c> with name, URL, description, photos and the comuni it works in
    /// (<c>areaServed</c>). Only what the showcase publishes: never a phone, an email, a VAT number or an address.
    /// </summary>
    public static JsonObject Supplier(
        string name, string? url, string? description, IReadOnlyList<string> imageUrls, IReadOnlyList<string> areaServed)
    {
        JsonNode? images = imageUrls.Count > 0
            ? new JsonArray(imageUrls.Select(i => (JsonNode?)JsonValue.Create(i)).ToArray())
            : null;
        JsonNode? areas = areaServed.Count > 0
            ? new JsonArray(areaServed.Select(a => (JsonNode?)Node("AdministrativeArea", ("name", Text(a)))).ToArray())
            : null;
        return Root("ProfessionalService",
            ("@id", Text(url)),
            ("name", Text(name)),
            ("url", Text(url)),
            ("description", Text(description)),
            ("image", images),
            ("areaServed", areas));
    }

    /// <summary>A list of pages (the properties of an org): position, name and URL of each.</summary>
    public static JsonObject ItemList(IEnumerable<(string Name, string? Url)> items) =>
        Root("ItemList", ("itemListElement", ListItems(items, "url")));

    /// <summary>Breadcrumb trail, from the top of the site to the current page.</summary>
    public static JsonObject BreadcrumbList(IEnumerable<(string Name, string? Url)> items) =>
        Root("BreadcrumbList", ("itemListElement", ListItems(items, "item")));

    /// <summary>An editorial page (guide, tourist tax page).</summary>
    public static JsonObject Article(
        string headline,
        string? description,
        string? url,
        DateTime? dateModified,
        string language,
        string publisherName) =>
        Root("Article",
            ("headline", Text(headline)),
            ("description", Text(description)),
            ("inLanguage", Text(language)),
            ("mainEntityOfPage", Text(url)),
            ("url", Text(url)),
            ("dateModified", dateModified is { } modified
                ? Text(modified.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
                : null),
            ("publisher", Node("Organization", ("name", Text(publisherName)))));

    /// <summary>A list page (the hub of the guides): the pages it lists are its <c>mainEntity</c>.</summary>
    public static JsonObject CollectionPage(
        string name,
        string? description,
        string? url,
        string language,
        IEnumerable<(string Name, string? Url)> items) =>
        Root("CollectionPage",
            ("name", Text(name)),
            ("description", Text(description)),
            ("inLanguage", Text(language)),
            ("url", Text(url)),
            ("mainEntity", Node("ItemList", ("itemListElement", ListItems(items, "url")))));

    /// <summary>What <see cref="VacationRental"/> reads of a property: published data only.</summary>
    public sealed record VacationRentalData(
        string Name,
        string? Description,
        string? Url,
        IReadOnlyList<string> ImageUrls,
        string? City,
        string? PostalCode,
        string? CountryCode,
        decimal Latitude,
        decimal Longitude,
        int MaxGuests,
        int Bedrooms,
        int Bathrooms,
        IReadOnlyList<string> AmenityNames,
        bool PetsAllowed,
        string Identifier,
        decimal NightlyRate,
        string Currency,
        string? OrgName,
        string? OrgUrl,
        int? FloorSizeSqm = null,
        TimeOnly? CheckInTime = null,
        TimeOnly? CheckOutTime = null,
        bool PetsNotAllowed = false);

    /// <summary>
    /// <c>VacationRental</c> of a property page (spec-branded-booking-site AC12). The whole property is rented
    /// (<c>EntirePlace</c>: a booking is per property). Only what the host said goes in (DB-11a): the floor size, the check-in and
    /// check-out times and the pets rule are written when the host stated them (a "pets not allowed" rule only when the host said No: an
    /// absent amenity is not a rule). Left out: <c>aggregateRating</c> (until the reviews are on the page) and availability (it depends on
    /// the dates). The nightly price is a <c>makesOffer</c> of the business: schema.org has no <c>offers</c> on a lodging business.
    /// </summary>
    public static JsonObject VacationRental(VacationRentalData data)
    {
        var amenities = new JsonArray();
        foreach (var name in data.AmenityNames)
        {
            amenities.Add(Node("LocationFeatureSpecification",
                ("name", Text(name)),
                ("value", JsonValue.Create(true))));
        }

        var place = Node("Accommodation",
            ("additionalType", Text("EntirePlace")),
            ("occupancy", data.MaxGuests > 0
                ? Node("QuantitativeValue", ("value", JsonValue.Create(data.MaxGuests)))
                : null),
            ("numberOfBedrooms", JsonValue.Create(data.Bedrooms)),
            ("numberOfBathroomsTotal", data.Bathrooms > 0 ? JsonValue.Create(data.Bathrooms) : null),
            ("floorSize", data.FloorSizeSqm is > 0
                ? Node("QuantitativeValue", ("value", JsonValue.Create(data.FloorSizeSqm.Value)), ("unitCode", Text("MTK")))
                : null),
            ("amenityFeature", amenities.Count > 0 ? amenities : null));

        var hasAddress = !string.IsNullOrWhiteSpace(data.City) || !string.IsNullOrWhiteSpace(data.PostalCode);
        // DB-03: the position of a house is private. Whatever the caller hands over, the page carries it rounded to about
        // 1 km (PropertyAddress.ToPublicCoordinate): enough for a search engine to place the property in its zone, never
        // its door. The exact value is not in this record's output under any circumstance.
        var latitude = PropertyAddress.ToPublicCoordinate(data.Latitude);
        var longitude = PropertyAddress.ToPublicCoordinate(data.Longitude);
        // 0,0 is the default of an unset position, not a place: never published as coordinates.
        var hasPosition = latitude != 0m || longitude != 0m;

        JsonNode? images = null;
        if (data.ImageUrls.Count > 0)
            images = new JsonArray(data.ImageUrls.Select(url => (JsonNode?)JsonValue.Create(url)).ToArray());

        JsonNode? offers = null;
        if (data.NightlyRate > 0m)
        {
            offers = new JsonArray(Node("Offer",
                ("priceSpecification", Node("UnitPriceSpecification",
                    ("price", JsonValue.Create(data.NightlyRate)),
                    ("priceCurrency", Text(data.Currency)),
                    ("unitCode", Text("DAY")),
                    ("unitText", Text("night"))))));
        }

        return Root("VacationRental",
            ("@id", Text(data.Url)),
            ("name", Text(data.Name)),
            ("description", Text(data.Description)),
            ("url", Text(data.Url)),
            ("identifier", Text(data.Identifier)),
            ("image", images),
            ("address", hasAddress
                ? Node("PostalAddress",
                    ("addressLocality", Text(data.City)),
                    ("postalCode", Text(data.PostalCode)),
                    ("addressCountry", Text(data.CountryCode)))
                : null),
            ("geo", hasPosition
                ? Node("GeoCoordinates",
                    ("latitude", JsonValue.Create(latitude)),
                    ("longitude", JsonValue.Create(longitude)))
                : null),
            ("containsPlace", place),
            ("checkinTime", data.CheckInTime is { } checkIn ? Text(checkIn.ToString("HH:mm", CultureInfo.InvariantCulture)) : null),
            ("checkoutTime", data.CheckOutTime is { } checkOut ? Text(checkOut.ToString("HH:mm", CultureInfo.InvariantCulture)) : null),
            // A rule the host stated: «Yes» (or the amenity) is true, «No» is false; «on request» and a rule never given say nothing.
            ("petsAllowed", data.PetsAllowed ? JsonValue.Create(true) : data.PetsNotAllowed ? JsonValue.Create(false) : null),
            ("brand", string.IsNullOrWhiteSpace(data.OrgName)
                ? null
                : Node("Organization", ("name", Text(data.OrgName)), ("url", Text(data.OrgUrl)))),
            ("makesOffer", offers));
    }

    private static JsonArray ListItems(IEnumerable<(string Name, string? Url)> items, string urlKey)
    {
        var elements = new JsonArray();
        var position = 1;
        foreach (var (name, url) in items)
        {
            elements.Add(Node("ListItem",
                ("position", JsonValue.Create(position++)),
                ("name", Text(name)),
                (urlKey, Text(url))));
        }

        return elements;
    }

    private static JsonNode? Text(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : JsonValue.Create(value.Trim());

    /// <summary>A complete top-level object: <c>@context</c>, <c>@type</c> and the properties whose value is not null.</summary>
    private static JsonObject Root(string type, params (string Key, JsonNode? Value)[] properties)
    {
        var node = new JsonObject { ["@context"] = Context };
        Fill(node, type, properties);
        return node;
    }

    /// <summary>A nested object (no <c>@context</c>), with the properties whose value is not null.</summary>
    private static JsonObject Node(string type, params (string Key, JsonNode? Value)[] properties)
    {
        var node = new JsonObject();
        Fill(node, type, properties);
        return node;
    }

    private static void Fill(JsonObject node, string type, (string Key, JsonNode? Value)[] properties)
    {
        node["@type"] = type;
        foreach (var (key, value) in properties)
        {
            if (value is not null)
                node[key] = value;
        }
    }
}
