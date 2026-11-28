using System.Text.RegularExpressions;
using static Casazen.Core.Pricing.PriceSuggestionLimits;

namespace Casazen.Core.Pricing;

public sealed record RawPriceAnswer(
    decimal? NightlyRate,
    decimal? RangeMin,
    decimal? RangeMax,
    decimal? CleaningFee,
    decimal? WeekendSurchargePercent,
    decimal? MinNights,
    string? Rationale);

public sealed record AcceptedPriceAnswer(
    decimal NightlyRate,
    decimal RangeMin,
    decimal RangeMax,
    decimal? CleaningFee,
    decimal? WeekendSurchargePercent,
    int? MinNights,
    string? Rationale);

public static partial class PriceSuggestionRules
{
    public static decimal RoundToStep(decimal value) =>
        Math.Round(value / RoundingStep, 0, MidpointRounding.AwayFromZero) * RoundingStep;

    public static MarketPriceStats? Summarize(IEnumerable<decimal> rates, bool similarSize)
    {
        ArgumentNullException.ThrowIfNull(rates);

        var sorted = rates.Where(rate => rate > 0).Order().ToArray();
        if (sorted.Length < MinSimilarListings)
            return null;

        return new MarketPriceStats(
            sorted.Length,
            sorted[0],
            Percentile(sorted, 0.25m),
            Percentile(sorted, 0.5m),
            Percentile(sorted, 0.75m),
            sorted[^1],
            similarSize);
    }

    public static decimal PositioningFactor(PricePositioning positioning) => positioning switch
    {
        PricePositioning.Lower => 1m - PositioningShift,
        PricePositioning.Higher => 1m + PositioningShift,
        _ => 1m,
    };

    public static NightlyPriceSuggestion FromMarket(
        MarketPriceStats market,
        PricePositioning positioning,
        PriceSuggestionBasis basis,
        string reason)
    {
        ArgumentNullException.ThrowIfNull(market);
        ArgumentNullException.ThrowIfNull(basis);

        var factor = PositioningFactor(positioning);
        var rate = Bounded(RoundToStep(market.Median * factor));
        var low = Math.Min(rate, Bounded(RoundToStep(market.P25 * factor)));
        var high = Math.Max(rate, Bounded(RoundToStep(market.P75 * factor)));
        return new NightlyPriceSuggestion(false, reason, rate, low, high, null, null, null, null, basis);
    }

    /// <summary>
    /// What is believed of an answer of the AI, or <c>null</c> when none of it is. The rate must be a price (from
    /// <see cref="PriceSuggestionLimits.MinNightlyRate"/> to <see cref="PriceSuggestionLimits.MaxNightlyRate"/>) and, when there are
    /// figures of homes of a similar size, not far outside them (<see cref="PriceSuggestionLimits.BandLowFactor"/>). The range is
    /// made to contain the rate; the cleaning fee, the surcharge and the minimum stay are kept only when they are sensible, one by one.
    /// </summary>
    public static AcceptedPriceAnswer? Accept(RawPriceAnswer raw, MarketPriceStats? market)
    {
        ArgumentNullException.ThrowIfNull(raw);

        if (raw.NightlyRate is not { } rawRate || rawRate <= 0)
            return null;

        var rate = RoundToStep(rawRate);
        if (rate < MinNightlyRate || rate > MaxNightlyRate)
            return null;

        if (market is { SimilarSize: true } figures && (rate < figures.Min * BandLowFactor || rate > figures.Max * BandHighFactor))
            return null;

        var low = raw.RangeMin is { } rawLow && rawLow > 0 ? RoundToStep(rawLow) : RoundToStep(rate * 0.85m);
        var high = raw.RangeMax is { } rawHigh && rawHigh > 0 ? RoundToStep(rawHigh) : RoundToStep(rate * 1.15m);
        low = Math.Clamp(low, Math.Max(MinNightlyRate, RoundToStep(rate * 0.5m)), rate);
        high = Math.Clamp(high, rate, Math.Min(MaxNightlyRate, RoundToStep(rate * 2m)));

        decimal? cleaning = raw.CleaningFee is { } rawCleaning && RoundToStep(rawCleaning) > 0
            ? Math.Min(MaxCleaningFee, RoundToStep(rawCleaning))
            : null;

        decimal? weekend = raw.WeekendSurchargePercent is { } rawWeekend && Math.Round(rawWeekend) >= 1
            ? Math.Min(MaxWeekendSurchargePercent, Math.Round(rawWeekend))
            : null;

        int? minNights = raw.MinNights is { } rawNights && Math.Round(rawNights) >= 2
            ? (int)Math.Min(MaxMinNights, Math.Round(rawNights))
            : null;

        return new AcceptedPriceAnswer(rate, low, high, cleaning, weekend, minNights, CleanRationale(raw.Rationale));
    }

    public static string? CleanRationale(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var cleaned = WhitespaceRegex().Replace(text.Replace("**", string.Empty).Replace("`", string.Empty), " ").Trim();
        if (cleaned.Length == 0)
            return null;

        if (cleaned.Length <= RationaleMaxLength)
            return cleaned;

        var cut = cleaned[..RationaleMaxLength];
        var sentenceEnd = cut.LastIndexOfAny(['.', '!', '?']);
        return sentenceEnd >= RationaleMaxLength / 2
            ? cut[..(sentenceEnd + 1)]
            : cleaned[..(RationaleMaxLength - 1)].TrimEnd() + "…";
    }

    private static decimal Bounded(decimal rate) => Math.Clamp(rate, MinNightlyRate, MaxNightlyRate);

    private static decimal Percentile(decimal[] sorted, decimal fraction)
    {
        var position = (sorted.Length - 1) * fraction;
        var lower = (int)Math.Floor(position);
        var upper = (int)Math.Ceiling(position);
        return sorted[lower] + ((sorted[upper] - sorted[lower]) * (position - lower));
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
