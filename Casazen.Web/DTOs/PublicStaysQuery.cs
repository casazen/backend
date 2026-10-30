using System.ComponentModel.DataAnnotations;
using Casazen.Core.Enums;
using Casazen.Core.Services;
using Casazen.Core.SiteProfile;

namespace Casazen.Web.DTOs;

/// <summary>
/// The query of <c>GET api/public/orgs/{slug}/stays</c> (DB-07a): the dates and the party, then the filters and the sort of the design.
/// Every part is optional; a value that is out of range is a 400, a value that no house can match (an amenity or a rule that does not
/// exist) is left out and declared in the answer.
/// </summary>
public sealed class PublicStaysQuery
{
    private const int MaxTokens = 20;
    private const int MaxTokenLength = 40;

    private static readonly string[] KnownRules = ["pets", "children", "selfCheckin", "smoking", "parties"];

    private static readonly string[] KnownSorts =
        [PublicSiteSearch.Recommended, PublicSiteSearch.PriceAsc, PublicSiteSearch.PriceDesc, PublicSiteSearch.Rating, PublicSiteSearch.Largest];

    /// <summary>First night of the stay (<c>yyyy-MM-dd</c>); given together with <see cref="CheckOut"/> or not at all.</summary>
    public DateOnly? CheckIn { get; set; }

    /// <summary>Day of departure (<c>yyyy-MM-dd</c>), after <see cref="CheckIn"/>.</summary>
    public DateOnly? CheckOut { get; set; }

    [Range(1, 50)]
    public int Adults { get; set; } = 2;

    [Range(0, 50)]
    public int Children { get; set; }

    /// <summary>The party brings a pet: a house that does not accept pets is answered with the reason <c>pets</c>.</summary>
    public bool Pets { get; set; }

    /// <summary>The place: the <c>id</c> of a destination (<c>GET …/destinations</c>) or the name of a comune.</summary>
    [StringLength(100)]
    public string? Where { get; set; }

    /// <summary>Lowest price per night, in euros.</summary>
    [Range(0, 100_000)]
    public decimal? MinPrice { get; set; }

    /// <summary>Highest price per night, in euros.</summary>
    [Range(0, 100_000)]
    public decimal? MaxPrice { get; set; }

    [Range(0, 50)]
    public int? Bedrooms { get; set; }

    [Range(0, 50)]
    public int? Bathrooms { get; set; }

    [Range(0, 10_000)]
    public int? MinSqm { get; set; }

    /// <summary>Names of the amenities (<c>Pool,WiFi,Terrace</c>), comma separated: the house has all of them.</summary>
    [StringLength(1000)]
    public string? Amenities { get; set; }

    /// <summary><c>pets,children,selfCheckin,smoking,parties</c>, comma separated: the house allows all of them.</summary>
    [StringLength(200)]
    public string? Rules { get; set; }

    /// <summary>Labels of the houses (<c>mare,centro,piscina,pet,campagna,terrazza,vista-mare</c>), comma separated: the house has all of them. An unknown label is left out and declared.</summary>
    [StringLength(500)]
    public string? Tags { get; set; }

    /// <summary><c>recommended</c> (default), <c>priceAsc</c>, <c>priceDesc</c>, <c>rating</c> or <c>largest</c>.</summary>
    [StringLength(20)]
    public string? Sort { get; set; }

    [Range(1, 10_000)]
    public int Page { get; set; } = 1;

    /// <summary>Houses per page, 50 at most; left out, every house of the site (24 across all the sites).</summary>
    [Range(1, PublicSiteSearch.MaxPageSize)]
    public int? PageSize { get; set; }

    /// <param name="scope"><see cref="PublicStaysScope.Platform"/> for the search across all the sites (<c>GET api/public/stays</c>).</param>
    public PublicStaysCriteria ToCriteria(PublicStaysScope scope = PublicStaysScope.Site)
    {
        var ignored = new List<string>();

        var amenities = new List<string>();
        foreach (var token in Tokens(Amenities))
        {
            if (token.All(char.IsAsciiLetter) && Enum.TryParse<PropertyAmenity>(token, ignoreCase: true, out var amenity) && Enum.IsDefined(amenity))
                amenities.Add(amenity.ToString());
            else
                ignored.Add($"amenities:{token}");
        }

        var rules = new List<string>();
        foreach (var token in Tokens(Rules))
        {
            var rule = KnownRules.FirstOrDefault(known => string.Equals(known, token, StringComparison.OrdinalIgnoreCase));
            if (rule is null)
                ignored.Add($"rules:{token}");
            else
                rules.Add(rule);
        }

        var tags = new List<string>();
        foreach (var token in Tokens(Tags))
        {
            var tag = token.ToLowerInvariant();
            if (SitePropertyProfileLimits.Tags.Contains(tag, StringComparer.Ordinal))
                tags.Add(tag);
            else
                ignored.Add($"tags:{token}");
        }

        var sort = KnownSorts.FirstOrDefault(known => string.Equals(known, Sort?.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? PublicSiteSearch.Recommended;

        return new PublicStaysCriteria(
            CheckIn,
            CheckOut,
            Adults,
            Children,
            Pets,
            string.IsNullOrWhiteSpace(Where) ? null : Where.Trim(),
            MinPrice,
            MaxPrice,
            Bedrooms,
            Bathrooms,
            MinSqm,
            amenities.Distinct(StringComparer.Ordinal).ToList(),
            rules.Distinct(StringComparer.Ordinal).ToList(),
            tags.Distinct(StringComparer.Ordinal).ToList(),
            sort,
            Page,
            PageSize,
            ignored,
            scope);
    }

    /// <summary>The comma separated values of a filter: trimmed, without repeats, at most 20, each of a sensible length.</summary>
    private static IEnumerable<string> Tokens(string? list) =>
        (list ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(token => token.Length is > 0 and <= MaxTokenLength)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxTokens);
}
