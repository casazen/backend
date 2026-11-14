using System.Globalization;
using Casazen.Core.Entities;
using Casazen.Core.Services;
using Microsoft.Extensions.Options;

namespace Casazen.Core.Options;

/// <summary>
/// The tourist tax of the Swiss cantons and who collects it by default (NF-83, NF-85, D6, E12/F01/US01/T01), bound from <c>SwissTouristTax</c>: the lines of a
/// guest document in Switzerland. The shipped defaults are ⚖ choices to confirm with an accountant (C4, C5), kept in configuration and never written in code.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Rules.</b> One rule for each canton and range of dates: the amount per person and night, with the page of the canton it was read from. The shipped
/// rule is Ticino, CHF 2.00 per person and night from 2025-01-01 for holiday apartments, no age exemption (⚖ C4). A canton without a rule gets no tourist tax
/// line of its own (a booking that carries its own amount still does).</item>
/// <item><b>Platform collected.</b> The channels whose portal collects the tax itself (⚖ C5, <c>Airbnb</c> in Ticino): the document of their bookings has no tourist tax
/// line. It is the default of a Swiss issuer that has not confirmed its own list yet (<see cref="IGuestDocumentLineBuilder.PlatformCollectedChannelsOf"/>): read, never copied
/// into the profile, so the step «Tassa di soggiorno» of the billing setup stays open until the issuer confirms its list, which is then the one the documents follow.</item>
/// <item><b>Validated at startup</b> (<see cref="SwissTouristTaxOptionsValidator"/>): a malformed canton, a rate that is not positive or has more than two decimals, a rule
/// without its source, overlapping ranges of a canton and a channel that is not a portal stop the application with a clear message.</item>
/// </list>
/// </remarks>
public sealed class SwissTouristTaxOptions
{
    /// <summary>The section the options are read from.</summary>
    public const string SectionName = "SwissTouristTax";

    /// <summary>The highest amount per person and night a rule may have: more is a typing mistake, never a tax.</summary>
    public const decimal MaxRatePerPersonPerNight = 50m;

    /// <summary>The nights of a stay that are taxed at most: the longest stay a document is made for (a year).</summary>
    public const int MaxNights = 366;

    /// <summary>The cantonal rules.</summary>
    public List<SwissTouristTaxRuleOptions> Rules { get; set; } = [];

    /// <summary>The channels whose portal collects the tourist tax itself, by default (⚖ C5); the names of <see cref="BookingSource"/>.</summary>
    public List<BookingSource> PlatformCollected { get; set; } = [];

    /// <summary>
    /// The tourist tax of a stay of <paramref name="guests"/> people that starts on <paramref name="checkIn"/> and lasts <paramref name="nights"/> nights in
    /// <paramref name="canton"/>: each night costs the amount of the rule in force on that night, for every guest. <c>null</c> when no rule of the canton covers
    /// any night of the stay (nothing is known, never a tax of zero).
    /// </summary>
    public decimal? TaxOf(string? canton, DateOnly checkIn, int nights, int guests)
    {
        if (string.IsNullOrWhiteSpace(canton) || nights <= 0 || guests <= 0)
            return null;

        var code = canton.Trim().ToUpperInvariant();
        decimal? total = null;
        for (var night = 0; night < Math.Min(nights, MaxNights); night++)
        {
            var date = checkIn.AddDays(night);
            var rule = Rules.FirstOrDefault(r => string.Equals(r.Canton?.Trim(), code, StringComparison.OrdinalIgnoreCase)
                                                  && r.ValidFrom <= date
                                                  && (r.ValidTo is null || date <= r.ValidTo));
            if (rule is not null)
                total = (total ?? 0m) + rule.RatePerPersonPerNight * guests;
        }

        return total is { } amount ? decimal.Round(amount, 2, MidpointRounding.AwayFromZero) : null;
    }
}

/// <summary>The tourist tax of one canton from a day on: the amount of a night for each person.</summary>
public sealed class SwissTouristTaxRuleOptions
{
    /// <summary>The two letters of the canton (<c>TI</c>), as <c>SwissMunicipalities</c> gives them.</summary>
    public string Canton { get; set; } = string.Empty;

    /// <summary>The amount for each person and each night, in francs.</summary>
    public decimal RatePerPersonPerNight { get; set; }

    /// <summary>The first night the rule is in force.</summary>
    public DateOnly ValidFrom { get; set; }

    /// <summary>The last night the rule is in force; <c>null</c> = open.</summary>
    public DateOnly? ValidTo { get; set; }

    /// <summary>The page of the canton the amount was read from: a rule never exists without its source.</summary>
    public string? SourceUrl { get; set; }
}

/// <summary>The checks of the Swiss tourist tax options at startup (<see cref="SwissTouristTaxOptions"/>): every problem is named with the position of its rule.</summary>
public sealed class SwissTouristTaxOptionsValidator : IValidateOptions<SwissTouristTaxOptions>
{
    public ValidateOptionsResult Validate(string? name, SwissTouristTaxOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var problems = Problems(options);
        return problems.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(problems);
    }

    /// <summary>What is wrong with the options; empty when they can be used.</summary>
    public static IReadOnlyList<string> Problems(SwissTouristTaxOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var problems = new List<string>();
        var rules = options.Rules ?? [];
        for (var i = 0; i < rules.Count; i++)
        {
            var rule = rules[i];
            var where = string.Create(CultureInfo.InvariantCulture, $"SwissTouristTax:Rules:{i} ({rule.Canton})");
            var canton = rule.Canton?.Trim() ?? string.Empty;
            if (canton.Length != 2 || !canton.All(char.IsAsciiLetter))
                problems.Add($"{where}: the canton is two letters (TI).");

            if (rule.RatePerPersonPerNight <= 0m || rule.RatePerPersonPerNight > SwissTouristTaxOptions.MaxRatePerPersonPerNight
                || decimal.Round(rule.RatePerPersonPerNight, 2) != rule.RatePerPersonPerNight)
            {
                problems.Add($"{where}: the amount per person and night is above 0, at most {SwissTouristTaxOptions.MaxRatePerPersonPerNight.ToString(CultureInfo.InvariantCulture)}, with at most two decimals.");
            }

            if (rule.ValidFrom == default)
                problems.Add($"{where}: ValidFrom is required.");
            if (rule.ValidTo is { } to && to < rule.ValidFrom)
                problems.Add($"{where}: ValidTo is before ValidFrom.");

            if (!Uri.TryCreate(rule.SourceUrl, UriKind.Absolute, out var source) || source.Scheme != Uri.UriSchemeHttps)
                problems.Add($"{where}: the source is required, an https address of the page the amount was read from.");
        }

        // A canton has one rule on a night: two ranges of one canton never overlap.
        foreach (var group in rules
                     .Select((rule, index) => (Rule: rule, Index: index))
                     .GroupBy(x => x.Rule.Canton?.Trim().ToUpperInvariant()))
        {
            var ordered = group.OrderBy(x => x.Rule.ValidFrom).ToList();
            for (var a = 0; a < ordered.Count; a++)
            {
                for (var b = a + 1; b < ordered.Count; b++)
                {
                    var first = ordered[a].Rule;
                    var second = ordered[b].Rule;
                    if (first.ValidFrom <= (second.ValidTo ?? DateOnly.MaxValue) && second.ValidFrom <= (first.ValidTo ?? DateOnly.MaxValue))
                    {
                        problems.Add(string.Create(
                            CultureInfo.InvariantCulture,
                            $"SwissTouristTax:Rules:{ordered[a].Index} and :{ordered[b].Index}: the ranges of the canton {group.Key} overlap."));
                    }
                }
            }
        }

        foreach (var channel in options.PlatformCollected ?? [])
        {
            if (!Enum.IsDefined(channel) || !FiscalCopy.IsOtaBookingSource(channel))
                problems.Add($"SwissTouristTax:PlatformCollected: {channel} is not a portal.");
        }

        return problems;
    }
}
