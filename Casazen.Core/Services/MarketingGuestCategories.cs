using System.Text.Json;
using Casazen.Core.Entities;

namespace Casazen.Core.Services;

public sealed record MarketingCategoryChoices
{
    public string[] Interests { get; init; } = [];
    public string[] DismissedInterests { get; init; } = [];
    public string[]? TripType { get; init; }
    public string[] SuggestedInterests { get; init; } = [];
}

public static class MarketingGuestCategories
{
    public static MarketingCategoryChoices Choices(GuestCrmProfile profile) => string.IsNullOrWhiteSpace(profile.MarketingCategoryOverridesJson)
        ? new() : JsonSerializer.Deserialize<MarketingCategoryChoices>(profile.MarketingCategoryOverridesJson) ?? new();

    public static IEnumerable<string> EffectiveTags(GuestCrmProfile profile) =>
        CrmProfileRules.ParseTags(profile.TagsJson).Concat(Choices(profile).Interests).Distinct(StringComparer.OrdinalIgnoreCase);

    public static string Loyalty(int stays, DateTime? lastStay, DateTime today, int loyalStays, int lostMonths) =>
        stays == 0 ? "upcoming" : lastStay?.Date <= today.Date.AddMonths(-lostMonths) ? "lost"
        : stays >= loyalStays ? "loyal" : stays > 1 ? "returning" : "first";

    public static string Season(int month) => month switch
    {
        12 or 1 or 2 => "winter",
        >= 3 and <= 5 => "spring",
        >= 6 and <= 8 => "summer",
        _ => "autumn",
    };
}
