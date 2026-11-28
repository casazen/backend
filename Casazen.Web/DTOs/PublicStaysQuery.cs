using System.ComponentModel.DataAnnotations;
using Casazen.Core.Enums;
using Casazen.Core.Services;
using Casazen.Core.SiteProfile;

namespace Casazen.Web.DTOs;

public sealed class PublicStaysQuery
{
    private const int MaxTokens = 20;
    private const int MaxTokenLength = 40;

    private static readonly string[] KnownRules = ["pets", "children", "selfCheckin", "smoking", "parties"];

    private static readonly string[] KnownSorts =
        [PublicSiteSearch.Recommended, PublicSiteSearch.PriceAsc, PublicSiteSearch.PriceDesc, PublicSiteSearch.Rating, PublicSiteSearch.Largest];

    public DateOnly? CheckIn { get; set; }

    public DateOnly? CheckOut { get; set; }

    [Range(1, 50)]
    public int Adults { get; set; } = 2;

    [Range(0, 50)]
    public int Children { get; set; }

    public bool Pets { get; set; }

    [StringLength(100)]
    public string? Where { get; set; }

    [Range(0, 100_000)]
    public decimal? MinPrice { get; set; }

    [Range(0, 100_000)]
    public decimal? MaxPrice { get; set; }

    [Range(0, 50)]
    public int? Bedrooms { get; set; }

    [Range(0, 50)]
    public int? Bathrooms { get; set; }

    [Range(0, 10_000)]
    public int? MinSqm { get; set; }

    [StringLength(1000)]
    public string? Amenities { get; set; }

    [StringLength(200)]
    public string? Rules { get; set; }

    [StringLength(500)]
    public string? Tags { get; set; }

    [StringLength(20)]
    public string? Sort { get; set; }

    [Range(1, 10_000)]
    public int Page { get; set; } = 1;

    [Range(1, PublicSiteSearch.MaxPageSize)]
    public int? PageSize { get; set; }

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

    private static IEnumerable<string> Tokens(string? list) =>
        (list ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(token => token.Length is > 0 and <= MaxTokenLength)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxTokens);
}
