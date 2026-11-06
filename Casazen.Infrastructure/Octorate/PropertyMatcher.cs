using System.Globalization;
using System.Text;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Options;

namespace Casazen.Infrastructure.Octorate;

/// <summary>
/// Scores a listing of the channel manager against the CasaZen properties of the org (E02/F01/US02/T03, LST-02) and turns the scores
/// into a proposal with the thresholds of <see cref="OctorateMappingOptions"/>: a single clear winner is proposed, close or tied
/// candidates are ambiguous, and with no candidate the listing is offered for import. A proposal is never a decision: a person confirms.
/// </summary>
/// <remarks>
/// <para>The score (0 to 100) weighs what both sides know: the normalized address (case, accents, punctuation, street abbreviations
/// such as <c>v.</c>, <c>p.za</c>, <c>c.so</c>; a different house number halves it) 35, the distance between the coordinates 20, the
/// name 15, the postal code 10, the city 10, the guests 10. A criterion one side does not know is left out and the others are scaled
/// up to 100, so a missing value never counts against a match nor for it.</para>
/// <para>Pure: no database, no Octorate, no clock.</para>
/// </remarks>
public static class PropertyMatcher
{
    private const double AddressWeight = 35;
    private const double DistanceWeight = 20;
    private const double NameWeight = 15;
    private const double PostalCodeWeight = 10;
    private const double CityWeight = 10;
    private const double CapacityWeight = 10;

    /// <summary>The most candidates an ambiguous row lists.</summary>
    public const int MaxCandidates = 5;

    private static readonly Dictionary<string, string> Abbreviations = new(StringComparer.Ordinal)
    {
        ["v"] = "via",
        ["v."] = "via",
        ["vle"] = "viale",
        ["v.le"] = "viale",
        ["p.za"] = "piazza",
        ["pza"] = "piazza",
        ["p.zza"] = "piazza",
        ["pzza"] = "piazza",
        ["p.le"] = "piazzale",
        ["ple"] = "piazzale",
        ["c.so"] = "corso",
        ["cso"] = "corso",
        ["l.go"] = "largo",
        ["lgo"] = "largo",
        ["str."] = "strada",
        ["loc."] = "localita",
        ["loc"] = "localita",
        ["fraz."] = "frazione",
        ["fraz"] = "frazione",
        ["vic."] = "vicolo",
        ["st."] = "street",
        ["st"] = "street",
        ["rd."] = "road",
        ["rd"] = "road",
        ["ave."] = "avenue",
        ["ave"] = "avenue",
    };

    /// <summary>Words that only mark the house number (<c>n. 12</c>, <c>nr 12</c>, <c>civico 12</c>) and are dropped.</summary>
    private static readonly HashSet<string> NumberMarkers = new(StringComparer.Ordinal) { "n", "n.", "nr", "nr.", "n°", "no", "no.", "num", "civico" };

    /// <summary>The proposal for one listing: its status, the proposed property, the candidates and the best score.</summary>
    public static MatchProposal Propose(MatchListing listing, IReadOnlyList<MatchProperty> properties, OctorateMappingOptions thresholds)
    {
        ArgumentNullException.ThrowIfNull(listing);
        ArgumentNullException.ThrowIfNull(properties);
        ArgumentNullException.ThrowIfNull(thresholds);

        var ranked = properties
            .Select(property => (property.Id, Score: Score(listing, property)))
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Id)
            .ToList();
        if (ranked.Count == 0)
            return new MatchProposal(OctorateMappingStatus.Proposed, null, [], 0);

        var best = ranked[0];
        var second = ranked.Count > 1 ? ranked[1].Score : (int?)null;
        if (best.Score >= thresholds.ProposeMinScore && (second is null || best.Score - second.Value >= thresholds.ProposeMinMargin))
            return new MatchProposal(OctorateMappingStatus.Proposed, best.Id, [], best.Score);

        if (best.Score >= thresholds.CandidateMinScore)
        {
            var candidates = ranked
                .Where(candidate => candidate.Score >= thresholds.CandidateMinScore)
                .Take(MaxCandidates)
                .Select(candidate => candidate.Id)
                .ToList();
            return new MatchProposal(OctorateMappingStatus.Ambiguous, null, candidates, best.Score);
        }

        // No candidate: the listing is offered for import.
        return new MatchProposal(OctorateMappingStatus.Proposed, null, [], best.Score);
    }

    /// <summary>The score of <paramref name="property"/> for <paramref name="listing"/>, 0 to 100.</summary>
    public static int Score(MatchListing listing, MatchProperty property)
    {
        ArgumentNullException.ThrowIfNull(listing);
        ArgumentNullException.ThrowIfNull(property);

        double earned = 0;
        double available = 0;

        void Add(double weight, double? similarity)
        {
            if (similarity is not { } value)
                return;

            available += weight;
            earned += weight * Math.Clamp(value, 0, 1);
        }

        Add(AddressWeight, AddressSimilarity(listing.Address, property.Address));
        Add(DistanceWeight, DistanceSimilarity(listing.Latitude, listing.Longitude, property.Latitude, property.Longitude));
        Add(NameWeight, TextSimilarity(listing.Name, property.Name));
        Add(PostalCodeWeight, Equality(Compact(listing.PostalCode), Compact(property.PostalCode)));
        Add(CityWeight, Equality(Normalize(listing.City), Normalize(property.City)));
        Add(CapacityWeight, CapacitySimilarity(listing.MaxGuests, property.MaxGuests));

        return available <= 0 ? 0 : (int)Math.Round(earned / available * 100, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// Lowercase, without accents and punctuation, the street abbreviations written out and the house-number markers dropped, the words
    /// separated by one space.
    /// </summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var withoutAccents = new StringBuilder(text.Length);
        foreach (var character in text.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
                withoutAccents.Append(character);
        }

        var words = new List<string>();
        foreach (var raw in withoutAccents.ToString().Split([' ', ',', ';', '/', '\t'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (NumberMarkers.Contains(raw))
                continue;

            var word = Abbreviations.TryGetValue(raw, out var full) ? full : raw;
            var letters = new string(word.Where(char.IsLetterOrDigit).ToArray());
            if (letters.Length > 0)
                words.Add(letters);
        }

        return string.Join(' ', words);
    }

    private static double? AddressSimilarity(string? left, string? right)
    {
        var a = Normalize(left);
        var b = Normalize(right);
        if (a.Length == 0 || b.Length == 0)
            return null;

        var similarity = Dice(a, b);
        var numbersA = a.Split(' ').Where(word => word.Any(char.IsDigit)).ToHashSet(StringComparer.Ordinal);
        var numbersB = b.Split(' ').Where(word => word.Any(char.IsDigit)).ToHashSet(StringComparer.Ordinal);
        if (numbersA.Count > 0 && numbersB.Count > 0 && !numbersA.Overlaps(numbersB))
            similarity /= 2;

        return similarity;
    }

    private static double? TextSimilarity(string? left, string? right)
    {
        var a = Normalize(left);
        var b = Normalize(right);
        if (a.Length == 0 || b.Length == 0)
            return null;

        if (a == b)
            return 1;

        var contained = a.Contains(b, StringComparison.Ordinal) || b.Contains(a, StringComparison.Ordinal);
        return Math.Max(Dice(a, b), contained ? 0.9 : 0);
    }

    /// <summary>Dice coefficient of the word sets: 2 |A ∩ B| / (|A| + |B|).</summary>
    private static double Dice(string a, string b)
    {
        var wordsA = a.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        var wordsB = b.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        if (wordsA.Count == 0 || wordsB.Count == 0)
            return 0;

        var common = wordsA.Count(wordsB.Contains);
        return 2.0 * common / (wordsA.Count + wordsB.Count);
    }

    private static double? Equality(string a, string b) =>
        a.Length == 0 || b.Length == 0 ? null : string.Equals(a, b, StringComparison.Ordinal) ? 1 : 0;

    private static string Compact(string? text) =>
        text is null ? string.Empty : new string(text.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();

    private static double? CapacitySimilarity(int? left, int? right)
    {
        if (left is not > 0 || right is not > 0)
            return null;

        return Math.Abs(left.Value - right.Value) switch
        {
            0 => 1,
            1 => 0.5,
            _ => 0,
        };
    }

    /// <summary>Within 50 m: the same place; 200 m: very likely; 1 km: maybe; farther: another place.</summary>
    private static double? DistanceSimilarity(decimal? latitudeA, decimal? longitudeA, decimal? latitudeB, decimal? longitudeB)
    {
        if (!IsKnown(latitudeA, longitudeA) || !IsKnown(latitudeB, longitudeB))
            return null;

        var meters = HaversineMeters((double)latitudeA!.Value, (double)longitudeA!.Value, (double)latitudeB!.Value, (double)longitudeB!.Value);
        return meters switch
        {
            <= 50 => 1,
            <= 200 => 0.75,
            <= 1000 => 0.25,
            _ => 0,
        };
    }

    private static bool IsKnown(decimal? latitude, decimal? longitude) =>
        latitude is { } lat && longitude is { } lon && !(lat == 0 && lon == 0) && lat is >= -90 and <= 90 && lon is >= -180 and <= 180;

    private static double HaversineMeters(double lat1, double lon1, double lat2, double lon2)
    {
        const double earthRadius = 6_371_000;
        static double Radians(double degrees) => degrees * Math.PI / 180;
        var dLat = Radians(lat2 - lat1);
        var dLon = Radians(lon2 - lon1);
        var h = (Math.Sin(dLat / 2) * Math.Sin(dLat / 2))
                + (Math.Cos(Radians(lat1)) * Math.Cos(Radians(lat2)) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2));
        return 2 * earthRadius * Math.Asin(Math.Min(1, Math.Sqrt(h)));
    }
}

/// <summary>What the matcher knows of a listing of the channel manager.</summary>
public sealed record MatchListing(
    string? Name,
    string? Address,
    string? City,
    string? PostalCode,
    decimal? Latitude,
    decimal? Longitude,
    int? MaxGuests);

/// <summary>What the matcher knows of a CasaZen property.</summary>
public sealed record MatchProperty(
    Guid Id,
    string? Name,
    string? Address,
    string? City,
    string? PostalCode,
    decimal? Latitude,
    decimal? Longitude,
    int? MaxGuests);

/// <summary>The proposal for a listing.</summary>
/// <param name="Status">Proposed (with <paramref name="PropertyId"/>, or without: offered for import) or Ambiguous.</param>
/// <param name="PropertyId">The single clear winner, when there is one.</param>
/// <param name="CandidateIds">The candidates of an ambiguous listing, best first.</param>
/// <param name="BestScore">The score of the best property, 0 when the org has none.</param>
public sealed record MatchProposal(OctorateMappingStatus Status, Guid? PropertyId, IReadOnlyList<Guid> CandidateIds, int BestScore);
