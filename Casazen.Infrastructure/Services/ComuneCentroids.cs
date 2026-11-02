using System.Collections.Frozen;
using System.Globalization;
using Casazen.Core.Entities;

namespace Casazen.Infrastructure.Services;

/// <summary>The centre of a comune, in degrees (WGS84).</summary>
public readonly record struct ComuneCentre(decimal Latitude, decimal Longitude);

/// <summary>
/// The centre of every comune of Italy, by ISTAT code: where the public maps put a property whose host never gave a position
/// (<c>0, 0</c> = "not set", see <see cref="PublicPropertyPosition"/>). Reference data of the product, not of an org: the seed
/// <c>Data/Seeds/comuni-centroidi.csv</c> (Wikidata, CC0, built by <c>scripts/build-comuni-centroidi.mjs</c>; source and how to update it
/// in <c>docs/runbooks/comuni-istat.md</c>), compiled into the assembly and read once, never fetched at run time. It is the middle of the
/// comune, not of a house: it can be kilometres from it, which is why the public sees it rounded to about a kilometre and as indicative.
/// Every code of the official list (<c>comuni-istat.csv</c>) is in it, and so are the new codes of the comuni of Sardinia (its provinces
/// changed in 2025): a property that holds the old code or the new one is found either way.
/// </summary>
public static class ComuneCentroids
{
    public const string SeedResourceName = "Casazen.Infrastructure.Data.Seeds.comuni-centroidi.csv";
    public const string SeedMetadataResourceName = "Casazen.Infrastructure.Data.Seeds.comuni-centroidi.source.json";

    internal const string SeedHeader = "istat_code,latitude,longitude";

    private const int IstatCodeLength = 6;

    private static readonly Lazy<FrozenDictionary<string, ComuneCentre>> Seed = new(ReadSeed);

    /// <summary>How many ISTAT codes the seed has a centre for.</summary>
    public static int Count => Seed.Value.Count;

    /// <summary>
    /// The centre of the comune with this ISTAT code (6 digits, leading zeros kept, blanks around it ignored). False when the code is
    /// blank, malformed or not in the seed: the property then has no position to show.
    /// </summary>
    public static bool TryGet(string? istatCode, out ComuneCentre centre)
    {
        centre = default;
        var code = istatCode?.Trim();
        return code is { Length: IstatCodeLength } && Seed.Value.TryGetValue(code, out centre);
    }

    /// <summary>The whole seed, for the tests that check it.</summary>
    internal static IReadOnlyDictionary<string, ComuneCentre> All => Seed.Value;

    /// <summary>
    /// Reads the seed: a header line, then <c>istat_code,latitude,longitude</c> per comune (invariant culture). A line that cannot be read
    /// (a wrong number of cells, a code that is not 6 digits, a number that is not one or a position outside the earth, a code that is
    /// already there) is not used and is counted: the tests of the committed file want none, and a run time that meets one goes on
    /// with the others rather than lose the maps.
    /// </summary>
    internal static (FrozenDictionary<string, ComuneCentre> Centres, int Rejected) Parse(TextReader reader)
    {
        var centres = new Dictionary<string, ComuneCentre>(StringComparer.Ordinal);
        if (!string.Equals(reader.ReadLine()?.Trim(), SeedHeader, StringComparison.Ordinal))
            return (FrozenDictionary<string, ComuneCentre>.Empty, 1);

        var rejected = 0;
        while (reader.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            var cells = line.Split(',');
            if (cells.Length != 3
                || cells[0].Length != IstatCodeLength
                || !cells[0].All(char.IsAsciiDigit)
                || !TryParseDegrees(cells[1], out var latitude)
                || !TryParseDegrees(cells[2], out var longitude)
                || !PropertyAddress.IsValidLatitude(latitude)
                || !PropertyAddress.IsValidLongitude(longitude)
                || !centres.TryAdd(cells[0], new ComuneCentre(latitude, longitude)))
            {
                rejected++;
            }
        }

        return (centres.ToFrozenDictionary(StringComparer.Ordinal), rejected);
    }

    private static bool TryParseDegrees(string text, out decimal degrees) =>
        decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out degrees);

    private static FrozenDictionary<string, ComuneCentre> ReadSeed()
    {
        // A build without the file has no centres, like a build without the ISTAT seed has no list: the maps then place a house by the name of its comune.
        using var stream = typeof(ComuneCentroids).Assembly.GetManifestResourceStream(SeedResourceName);
        if (stream is null)
            return FrozenDictionary<string, ComuneCentre>.Empty;

        using var reader = new StreamReader(stream);
        return Parse(reader).Centres;
    }
}
