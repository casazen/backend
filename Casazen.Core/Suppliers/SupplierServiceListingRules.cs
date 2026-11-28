using System.Text.RegularExpressions;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Suppliers;

/// <summary>
/// What a supplier sends to create or replace a service (<c>api/supplier/services</c>), as read from the request:
/// nothing is checked yet. <see cref="SupplierServiceListingRules.Normalize"/> turns it into a
/// <see cref="SupplierServiceListingContent"/> or refuses it.
/// </summary>
/// <param name="Weekdays">The days the service is offered on; <c>null</c> is every day.</param>
/// <param name="PhotoUrls">
/// Only on an update: the photos to keep, in the new order (a subset of the current ones, see the service); <c>null</c>
/// leaves the photos as they are. Uploading is <c>POST .../photos</c>.
/// </param>
/// <param name="SortOrder">Position in the catalog; <c>null</c> keeps it (a new service goes last).</param>
/// <param name="UrgentEnabled">The service accepts urgent requests (SP-04c); a replacement, so a left out value is off.</param>
/// <param name="UrgentSurchargeCents">
/// The service's own urgent surcharge in cents, 0 to <see cref="SupplierAgendaLimits.MaxUrgentSurchargeCents"/>; <c>null</c> (also a
/// left out value) is the supplier's default.
/// </param>
public sealed record SupplierServiceListingInput(
    string? Name,
    string? Category,
    string? Summary,
    string? Description,
    int? PriceFromCents,
    SupplierServicePriceUnit PriceUnit,
    bool PricesIncludeVat,
    bool RequiresQuote,
    int? DurationMinutes,
    int? MinNoticeHours,
    IReadOnlyCollection<DayOfWeek>? Weekdays,
    IReadOnlyList<SupplierServiceSupplement?>? Supplements,
    IReadOnlyList<string?>? Included,
    IReadOnlyList<string?>? Excluded,
    IReadOnlyList<string?>? PhotoUrls,
    int? SortOrder,
    bool UrgentEnabled = false,
    int? UrgentSurchargeCents = null);

public sealed record SupplierServiceListingContent(
    string Name,
    string Category,
    string? Summary,
    string? Description,
    int? PriceFromCents,
    SupplierServicePriceUnit PriceUnit,
    bool PricesIncludeVat,
    bool RequiresQuote,
    int? DurationMinutes,
    int? MinNoticeHours,
    int WeekdaysMask,
    IReadOnlyList<SupplierServiceSupplement> Supplements,
    IReadOnlyList<string> Included,
    IReadOnlyList<string> Excluded,
    IReadOnlyList<string>? PhotoUrls,
    int? SortOrder,
    bool UrgentEnabled = false,
    int? UrgentSurchargeCents = null)
{
    public void ApplyTo(SupplierServiceListing listing)
    {
        ArgumentNullException.ThrowIfNull(listing);
        listing.Name = Name;
        listing.Category = Category;
        listing.Summary = Summary;
        listing.Description = Description;
        listing.PriceFromCents = PriceFromCents;
        listing.PriceUnit = PriceUnit;
        listing.PricesIncludeVat = PricesIncludeVat;
        listing.RequiresQuote = RequiresQuote;
        listing.DurationMinutes = DurationMinutes;
        listing.MinNoticeHours = MinNoticeHours;
        listing.WeekdaysMask = WeekdaysMask;
        listing.UrgentEnabled = UrgentEnabled;
        listing.UrgentSurchargeCents = UrgentSurchargeCents;
        listing.SupplementsJson = SupplierServiceListingJson.Serialize(Supplements);
        listing.IncludedJson = SupplierServiceListingJson.Serialize(Included);
        listing.ExcludedJson = SupplierServiceListingJson.Serialize(Excluded);
    }
}

public static partial class SupplierServiceListingRules
{
    public const string SlugFallback = "servizio";

    public static SupplierServiceListingContent Normalize(SupplierServiceListingInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var category = ServiceCategories.Require(input.Category);
        var invalid = new List<string>();

        var name = input.Name?.Trim() ?? string.Empty;
        if (name.Length is 0 or > SupplierServiceCatalogLimits.NameMaxLength || HasControlCharacters(name, allowLineBreaks: false))
            AddOnce(invalid, SupplierServiceFields.Name);

        var summary = OptionalText(
            input.Summary, SupplierServiceCatalogLimits.SummaryMaxLength, allowLineBreaks: false, SupplierServiceFields.Summary, invalid);
        var description = OptionalText(
            input.Description, SupplierServiceCatalogLimits.DescriptionMaxLength, allowLineBreaks: true, SupplierServiceFields.Description, invalid);

        if (input.PriceFromCents is { } price && price is < 1 or > SupplierServiceCatalogLimits.MaxAmountCents)
            AddOnce(invalid, SupplierServiceFields.PriceFromCents);
        if (!Enum.IsDefined(input.PriceUnit))
            AddOnce(invalid, SupplierServiceFields.PriceUnit);

        if (input.DurationMinutes is { } duration
            && duration is < SupplierServiceCatalogLimits.MinDurationMinutes or > SupplierServiceCatalogLimits.MaxDurationMinutes)
            AddOnce(invalid, SupplierServiceFields.DurationMinutes);
        if (input.MinNoticeHours is { } notice && notice is < 0 or > SupplierServiceCatalogLimits.MaxMinNoticeHours)
            AddOnce(invalid, SupplierServiceFields.MinNoticeHours);

        var weekdaysMask = SupplierServiceWeekdays.AllMask;
        if (input.Weekdays is not null)
        {
            if (input.Weekdays.Any(day => !Enum.IsDefined(day)))
                AddOnce(invalid, SupplierServiceFields.Weekdays);
            weekdaysMask = SupplierServiceWeekdays.ToMask(input.Weekdays);
        }

        var supplements = NormalizeSupplements(input.Supplements, invalid);
        var included = NormalizeList(input.Included, SupplierServiceFields.Included, invalid);
        var excluded = NormalizeList(input.Excluded, SupplierServiceFields.Excluded, invalid);
        var photoUrls = NormalizePhotoUrls(input.PhotoUrls, invalid);

        if (input.SortOrder is { } sortOrder && sortOrder is < 0 or > SupplierServiceCatalogLimits.MaxSortOrder)
            AddOnce(invalid, SupplierServiceFields.SortOrder);

        if (input.UrgentSurchargeCents is { } urgentSurcharge && !SupplierUrgentRules.IsValidSurcharge(urgentSurcharge))
            AddOnce(invalid, SupplierServiceFields.UrgentSurchargeCents);

        if (invalid.Count > 0)
            throw SupplierServiceCatalogErrors.InvalidFields(invalid);

        return new SupplierServiceListingContent(
            name,
            category,
            summary,
            description,
            input.PriceFromCents,
            input.PriceUnit,
            input.PricesIncludeVat,
            input.RequiresQuote,
            input.DurationMinutes,
            input.MinNoticeHours,
            weekdaysMask,
            supplements,
            included,
            excluded,
            photoUrls,
            input.SortOrder,
            input.UrgentEnabled,
            input.UrgentSurchargeCents);
    }

    public static IReadOnlyList<string> MissingForPublication(SupplierServiceListing listing)
    {
        ArgumentNullException.ThrowIfNull(listing);

        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(listing.Name))
            missing.Add(SupplierServiceFields.Name);
        if (!ServiceCategories.IsKnown(listing.Category))
            missing.Add(SupplierServiceFields.Category);
        if (listing.DurationMinutes is not > 0)
            missing.Add(SupplierServiceFields.DurationMinutes);
        if (!listing.RequiresQuote && listing.PriceFromCents is not > 0)
            missing.Add(SupplierServiceFields.PriceFromCents);
        return missing;
    }

    public static void EnsurePublishable(SupplierServiceListing listing)
    {
        var missing = MissingForPublication(listing);
        if (missing.Count > 0)
            throw SupplierServiceCatalogErrors.NotPublishableFields(missing);
    }

    public static string SlugFromName(string? name) => SupplierShowcaseSlug.FromName(name, SlugFallback);

    public static string CopyName(string name, string? suffix)
    {
        ArgumentNullException.ThrowIfNull(name);

        suffix ??= string.Empty;
        var limit = SupplierServiceCatalogLimits.NameMaxLength;
        if (suffix.Length >= limit)
            return suffix[..limit].Trim();

        var room = limit - suffix.Length;
        var head = name.Length > room ? name[..room].TrimEnd() : name;
        return head + suffix;
    }

    /// <summary>
    /// <paramref name="baseSlug"/> if it is free, otherwise the first of <c>base-2</c>, <c>base-3</c>... that is not in
    /// <paramref name="taken"/> (the slugs the supplier's other services use).
    /// </summary>
    public static string NextFreeSlug(string baseSlug, IReadOnlySet<string> taken)
    {
        ArgumentException.ThrowIfNullOrEmpty(baseSlug);
        ArgumentNullException.ThrowIfNull(taken);

        if (!taken.Contains(baseSlug))
            return baseSlug;

        for (var n = 2; ; n++)
        {
            var candidate = $"{baseSlug}-{n}";
            if (!taken.Contains(candidate))
                return candidate;
        }
    }

    public static bool SlugFollowsName(string slug, string baseSlug)
    {
        if (string.Equals(slug, baseSlug, StringComparison.Ordinal))
            return true;

        var prefix = baseSlug + "-";
        return slug.StartsWith(prefix, StringComparison.Ordinal)
               && slug.Length > prefix.Length
               && slug.AsSpan(prefix.Length).IndexOfAnyExceptInRange('0', '9') < 0;
    }

    private static List<SupplierServiceSupplement> NormalizeSupplements(
        IReadOnlyList<SupplierServiceSupplement?>? source,
        List<string> invalid)
    {
        var result = new List<SupplierServiceSupplement>();
        if (source is null)
            return result;

        if (source.Count > SupplierServiceCatalogLimits.MaxSupplements)
            AddOnce(invalid, SupplierServiceFields.Supplements);

        var codes = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < source.Count; i++)
        {
            var supplement = source[i];
            var path = $"{SupplierServiceFields.Supplements}[{i}]";
            if (supplement is null)
            {
                AddOnce(invalid, path);
                continue;
            }

            var code = supplement.Code?.Trim().ToLowerInvariant() ?? string.Empty;
            if (code.Length is 0 or > SupplierServiceCatalogLimits.SupplementCodeMaxLength
                || !SupplementCode().IsMatch(code)
                || !codes.Add(code))
                AddOnce(invalid, $"{path}.code");

            var label = supplement.Label?.Trim() ?? string.Empty;
            if (label.Length is 0 or > SupplierServiceCatalogLimits.SupplementLabelMaxLength
                || HasControlCharacters(label, allowLineBreaks: false))
                AddOnce(invalid, $"{path}.label");

            if (supplement.AmountCents is < 1 or > SupplierServiceCatalogLimits.MaxAmountCents)
                AddOnce(invalid, $"{path}.amountCents");

            var per = supplement.Per?.Trim().ToLowerInvariant() ?? string.Empty;
            if (!SupplierServiceSupplementUnits.All.Contains(per, StringComparer.Ordinal))
                AddOnce(invalid, $"{path}.per");

            if (supplement.Max is { } max && max is < 1 or > SupplierServiceCatalogLimits.MaxSupplementQuantity)
                AddOnce(invalid, $"{path}.max");

            result.Add(new SupplierServiceSupplement(code, label, supplement.AmountCents, per, supplement.Max));
        }

        return result;
    }

    private static List<string> NormalizeList(IReadOnlyList<string?>? source, string field, List<string> invalid)
    {
        var result = new List<string>();
        if (source is null)
            return result;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in source)
        {
            var text = item?.Trim();
            if (string.IsNullOrEmpty(text))
                continue;

            if (text.Length > SupplierServiceCatalogLimits.ListItemMaxLength || HasControlCharacters(text, allowLineBreaks: false))
                AddOnce(invalid, field);
            else if (seen.Add(text))
                result.Add(text);
        }

        if (result.Count > SupplierServiceCatalogLimits.MaxListItems)
            AddOnce(invalid, field);
        return result;
    }

    private static List<string>? NormalizePhotoUrls(IReadOnlyList<string?>? source, List<string> invalid)
    {
        if (source is null)
            return null;

        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in source)
        {
            var url = item?.Trim();
            if (string.IsNullOrEmpty(url) || url.Length > SupplierServiceCatalogLimits.PhotoUrlMaxLength || !seen.Add(url))
            {
                AddOnce(invalid, SupplierServiceFields.PhotoUrls);
                continue;
            }

            result.Add(url);
        }

        if (result.Count > SupplierServiceCatalogLimits.MaxPhotos)
            AddOnce(invalid, SupplierServiceFields.PhotoUrls);
        return result;
    }

    private static string? OptionalText(string? value, int maxLength, bool allowLineBreaks, string field, List<string> invalid)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text))
            return null;

        if (text.Length > maxLength || HasControlCharacters(text, allowLineBreaks))
            AddOnce(invalid, field);
        return text;
    }

    private static bool HasControlCharacters(string text, bool allowLineBreaks) =>
        text.Any(c => char.IsControl(c) && !(allowLineBreaks && c is '\n' or '\r' or '\t'));

    private static void AddOnce(List<string> fields, string field)
    {
        if (!fields.Contains(field, StringComparer.Ordinal))
            fields.Add(field);
    }

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex SupplementCode();
}
