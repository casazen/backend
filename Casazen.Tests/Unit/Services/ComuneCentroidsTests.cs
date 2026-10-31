using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Casazen.Infrastructure.Services;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// The centres of the comuni, the position that the public maps give to a property whose host gave none: the committed seed is whole,
/// nearly every comune of the official list has one, and the lookup answers by ISTAT code, old or new.
/// </summary>
public partial class ComuneCentroidsTests
{
    [Theory]
    [InlineData("013075", 45.81, 9.09)] // Como
    [InlineData("015146", 45.47, 9.19)] // Milano
    [InlineData("058091", 41.89, 12.48)] // Roma
    public void TryGet_KnownComune_GivesItsCentre(string code, double latitude, double longitude)
    {
        var found = ComuneCentroids.TryGet(code, out var centre);

        Assert.True(found);
        Assert.InRange(centre.Latitude, (decimal)latitude - 0.02m, (decimal)latitude + 0.02m);
        Assert.InRange(centre.Longitude, (decimal)longitude - 0.02m, (decimal)longitude + 0.02m);
    }

    [Fact]
    public void TryGet_CodeWithBlanksAround_IsFound()
    {
        Assert.True(ComuneCentroids.TryGet("  013075 ", out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("13075")] // no leading zero: not the code the official list writes
    [InlineData("0130755")]
    [InlineData("01307A")]
    [InlineData("999999")] // well formed, no such comune
    public void TryGet_BlankMalformedOrUnknownCode_IsNotFound(string? code)
    {
        var found = ComuneCentroids.TryGet(code, out var centre);

        Assert.False(found);
        Assert.Equal(default, centre);
    }

    [Theory]
    [InlineData("090047", "113017")] // Olbia: the code of the official list and the new one of the provinces of Sardinia
    [InlineData("090003", "112001")] // Alghero
    public void TryGet_OldAndNewCodeOfASardinianComune_GiveTheSameCentre(string oldCode, string newCode)
    {
        Assert.True(ComuneCentroids.TryGet(oldCode, out var before));
        Assert.True(ComuneCentroids.TryGet(newCode, out var after));

        Assert.Equal(before, after);
    }

    [Fact]
    public void Parse_LinesThatCannotBeRead_AreCountedAndTheOthersAreKept()
    {
        const string text = "istat_code,latitude,longitude\n"
            + "013075,45.8103,9.0861\n" // fine
            + "13075,45.8,9.0\n" // a code of 5 digits
            + "01307X,45.8,9.0\n" // a letter in the code
            + "013076,95.0,9.0\n" // a latitude that is not on the earth
            + "013077,45.8\n" // two cells
            + "013075,46.0,9.0\n" // a code that is already there: the first one stays
            + "\n"
            + "015146,45.4669,9.19\n"; // fine

        var (centres, rejected) = ComuneCentroids.Parse(new StringReader(text));

        Assert.Equal(2, centres.Count);
        Assert.Equal(5, rejected);
        Assert.Equal(45.8103m, centres["013075"].Latitude);
    }

    [Fact]
    public void Parse_WrongHeader_GivesNoCentre()
    {
        var (centres, rejected) = ComuneCentroids.Parse(new StringReader("code,lat,lon\n013075,45.8,9.0\n"));

        Assert.Empty(centres);
        Assert.Equal(1, rejected);
    }

    [Fact]
    public void Seed_Committed_HasNoLineThatCannotBeReadAndEveryCentreIsInItaly()
    {
        using var reader = new StreamReader(OpenResource(ComuneCentroids.SeedResourceName));

        var (centres, rejected) = ComuneCentroids.Parse(reader);

        Assert.Equal(0, rejected);
        Assert.True(centres.Count > 7_800, $"only {centres.Count} centres");
        foreach (var (code, centre) in centres)
        {
            // Italy with a margin, Lampedusa to Vetta d'Italia and Bardonecchia to Otranto: a latitude and a longitude swapped, or a wrong sign, are out.
            Assert.True(centre.Latitude is >= 35.3m and <= 47.2m, $"{code} has latitude {centre.Latitude}");
            Assert.True(centre.Longitude is >= 6.4m and <= 18.9m, $"{code} has longitude {centre.Longitude}");
        }
    }

    [Fact]
    public void Seed_Committed_IsTheFileItsSourceDescribes()
    {
        var metadata = JsonDocument.Parse(ReadText(ComuneCentroids.SeedMetadataResourceName)).RootElement;

        // The bytes of the file as they are committed (.gitattributes keeps them the same on every machine): an edit by hand, or a rebuild
        // without the source file written again, is seen here.
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(ReadBytes(ComuneCentroids.SeedResourceName))), metadata.GetProperty("seedSha256").GetString());
        Assert.Equal(metadata.GetProperty("rows").GetInt32(), ComuneCentroids.Count);
        Assert.Equal("CC0 1.0", metadata.GetProperty("license").GetString());
    }

    [Fact]
    public void Seed_Committed_HasACentreForNearlyEveryComuneOfTheOfficialList()
    {
        // The rows of the official file begin with: region; supra-municipal unit; historical province; progressive; the code of the comune.
        var codes = OfficialCode().Matches(ReadText(ComuneImportService.SeedResourceName)).Select(m => m.Groups[1].Value).ToList();

        var missing = codes.Where(code => !ComuneCentroids.TryGet(code, out _)).ToList();

        Assert.True(codes.Count > 7_800, $"only {codes.Count} comuni read from the official list");
        // A comune born of a merger is in the official list weeks before Wikidata has it: a few without a centre are no failure, many are a seed that
        // has to be built again (scripts/build-comuni-centroidi.mjs, docs/runbooks/comuni-istat.md) after the official list was replaced.
        Assert.True(
            missing.Count <= codes.Count / 100,
            $"{missing.Count} of {codes.Count} comuni of the official list have no centre (first: {string.Join(' ', missing.Take(10))})");
    }

    [GeneratedRegex(@"^\d{2};\d{3};\d{3};\d{3};(\d{6});", RegexOptions.Multiline)]
    private static partial Regex OfficialCode();

    private static Stream OpenResource(string name) =>
        typeof(ComuneCentroids).Assembly.GetManifestResourceStream(name)
        ?? throw new InvalidOperationException($"{name} is not part of the assembly");

    private static string ReadText(string name)
    {
        using var reader = new StreamReader(OpenResource(name));
        return reader.ReadToEnd();
    }

    private static byte[] ReadBytes(string name)
    {
        using var stream = OpenResource(name);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
