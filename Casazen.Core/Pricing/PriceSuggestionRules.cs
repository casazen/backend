using System.Text.RegularExpressions;
using static Casazen.Core.Pricing.PriceSuggestionLimits;

namespace Casazen.Core.Pricing;

/// <summary>What the AI said, as numbers and text, before anything is believed: every field may be missing or wrong.</summary>
public sealed record RawPriceAnswer(
    decimal? NightlyRate,
    decimal? RangeMin,
    decimal? RangeMax,
    decimal? CleaningFee,
    decimal? WeekendSurchargePercent,
    decimal? MinNights,
    string? Rationale);

/// <summary>An answer of the AI that passed <see cref="PriceSuggestionRules.Accept"/>: rounded, inside the limits, coherent.</summary>
public sealed record AcceptedPriceAnswer(
    decimal NightlyRate,
    decimal RangeMin,
    decimal RangeMax,
    decimal? CleaningFee,
    decimal? WeekendSurchargePercent,
    int? MinNights,
    string? Rationale);

/// <summary>
/// The rules of the nightly price suggestion (WIZ-AI-PRICE), all pure: the figures of the similar homes, what is believed of an
/// answer of the AI, how it is rounded, and the price that comes from the figures alone when the AI is not there. The AI proposes;
/// nothing here writes to a property, the host reads the numbers and decides.
/// </summary>
public static partial class PriceSuggestionRules
{
    /// <summary>The nearest multiple of <see cref="PriceSuggestionLimits.RoundingStep"/>.</summary>
    public static decimal RoundToStep(decimal value) =>
        Math.Round(value / RoundingStep, 0, MidpointRounding.AwayFromZero) * RoundingStep;

    /// <summary>
    /// The figures of the nightly rates of <paramref name="rates"/> (the homes that count, a rate of 0 is no rate); <c>null</c> with
    /// fewer than <see cref="PriceSuggestionLimits.MinSimilarListings"/> homes, so no home is ever readable in them.
    /// </summary>
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

    /// <summary>The factor the positioning applies to the usual price of comparable homes.</summary>
    public static decimal PositioningFactor(PricePositioning positioning) => positioning switch
    {
        PricePositioning.Lower => 1m - PositioningShift,
        PricePositioning.Higher => 1m + PositioningShift,
        _ => 1m,
    };

    /// <summary>
    /// The price worked out from the figures of the similar homes alone (no AI): the median moved by the positioning, between the
    /// quartiles moved the same way. Nothing else is suggested: without the AI there is no ground for a cleaning fee or a surcharge.
    /// </summary>
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

    /// <summary>
    /// The explanation as a plain line: no markdown, no line breaks, at most <see cref="PriceSuggestionLimits.RationaleMaxLength"/>
    /// characters (cut at the end of a sentence when there is one); <c>null</c> when there is nothing left.
    /// </summary>
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

    /// <summary>The value at <paramref name="fraction"/> of the sorted rates, interpolating between the two neighbours.</summary>
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
