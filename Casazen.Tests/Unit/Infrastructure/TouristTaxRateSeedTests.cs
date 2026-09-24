using System.Globalization;
using Casazen.Core.Entities.Enums;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Data.Seeds;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.VisualBasic.FileIO;
using Xunit;

namespace Casazen.Tests.Unit.Infrastructure;

/// <summary>
/// The tourist tax seed (CO-03, A5-06) holds exactly the rows of the RS-7 CSV that the current <c>TouristTaxRate</c>
/// model represents, with their values unchanged. If the CSV changes, a new migration is needed: the seed of an
/// applied migration must not change.
/// </summary>
public class TouristTaxRateSeedTests
{
    /// <summary>
    /// Comuni whose rule the model cannot represent even with a single official amount (RS-7, "Indicazioni per CO-03"
    /// point 3). Torino: from 01/04/2026 the nights are capped per year, not per stay.
    /// </summary>
    private static readonly Dictionary<string, string> NotRepresentableRules = new()
    {
        ["001272"] = "Torino: nights capped per calendar year",
    };

    [Fact]
    public void BuildRates_RepresentableCsvRows_AreExactlyTheSeededRows()
    {
        var rows = ReadCsv();

        var expected = rows
            .Where(r => IsRepresentable(r, rows))
            .Select(r => TouristTaxRateSeed.IdFor(r.IstatCode, ParseDate(r.ValidFrom)))
            .Order()
            .ToList();
        var seeded = TouristTaxRateSeed.BuildRates().Select(r => r.Id).Order().ToList();

        Assert.NotEmpty(expected);
        Assert.Equal(expected, seeded);
    }

    [Fact]
    public void BuildRates_EveryRow_KeepsTheCsvValuesAndSource()
    {
        var rows = ReadCsv();

        foreach (var rate in TouristTaxRateSeed.BuildRates())
        {
            var row = rows.Single(r => r.Comune == rate.City && ParseDate(r.ValidFrom) == rate.EffectiveFrom);

            Assert.Equal(decimal.Parse(row.Rate, CultureInfo.InvariantCulture), rate.RatePerPersonPerNight);
            Assert.Equal(int.Parse(row.MaxNights, CultureInfo.InvariantCulture), rate.MaxNights);
            Assert.Equal(int.Parse(row.MinAgeExempt, CultureInfo.InvariantCulture), rate.MinimumAge);
            Assert.Equal(DateTimeKind.Utc, rate.EffectiveFrom.Kind);
            Assert.Null(rate.EffectiveTo);
            Assert.Equal(row.Notes, rate.Notes);
            Assert.True(rate.Notes.Length <= 500, $"{rate.City}: notes longer than the column");
            Assert.Equal(row.SourceUrl, rate.SourceUrl);
            Assert.Equal("U", row.Verified);
            Assert.Equal(TouristTaxRateVerification.Official, rate.VerificationLevel);
            Assert.True(rate.IsActive);
            Assert.Equal(TouristTaxRateSeed.IdFor(row.IstatCode, rate.EffectiveFrom), rate.Id);
        }
    }

    [Fact]
    public void BuildRates_NotRepresentableOrWithoutRate_AreNotSeeded()
    {
        var seededCities = TouristTaxRateSeed.BuildRates().Select(r => r.City).ToHashSet();

        foreach (var comune in new[] { "Roma", "Venezia", "Bologna", "Torino", "Seveso", "Cesano Maderno" })
            Assert.DoesNotContain(comune, seededCities);
    }

    [Fact]
    public void BuildRates_Ids_AreStableAndUnique()
    {
        var first = TouristTaxRateSeed.BuildRates().Select(r => r.Id).ToList();
        var second = TouristTaxRateSeed.BuildRates().Select(r => r.Id).ToList();

        Assert.Equal(first, second);
        Assert.Equal(first.Count, first.Distinct().Count());
        Assert.DoesNotContain(Guid.Empty, first);
    }

    [Fact]
    public void AddTouristTaxRateSourceAndSeed_Script_AddsSourceColumnsAndInsertsSeededRates()
    {
        using var db = NewNpgsqlContext();
        var keys = db.GetService<IMigrationsAssembly>().Migrations.Keys.ToList();
        var migration = keys.Single(k => k.EndsWith("AddTouristTaxRateSourceAndSeed", StringComparison.Ordinal));
        var idx = keys.IndexOf(migration);

        var script = db.GetService<IMigrator>().GenerateScript(fromMigration: keys[idx - 1], toMigration: migration);

        Assert.Contains("ADD \"SourceUrl\" character varying(500)", script);
        Assert.Contains("ADD \"VerificationLevel\" character varying(20)", script);
        Assert.Contains("INSERT INTO \"TouristTaxRates\"", script);
        foreach (var rate in TouristTaxRateSeed.BuildRates())
            Assert.Contains(rate.Id.ToString(), script);
        Assert.Contains("'Official'", script);
    }

    private static bool IsRepresentable(CsvRow row, IReadOnlyList<CsvRow> rows) =>
        // A fixed amount per person per night (empty = no rate or a percentage, never 0).
        !string.IsNullOrWhiteSpace(row.Rate)
        // An official source for the amount: T is "a hint, not a source", D is our own deduction.
        && row.Verified == "U"
        && row.SourceUrl.StartsWith("https://", StringComparison.Ordinal)
        // One amount for the whole comune: several rows mean category, season or cadastral group.
        && rows.Count(r => r.IstatCode == row.IstatCode) == 1
        && !NotRepresentableRules.ContainsKey(row.IstatCode);

    private static DateTime ParseDate(string value) =>
        DateTime.SpecifyKind(DateTime.ParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture), DateTimeKind.Utc);

    private static List<CsvRow> ReadCsv()
    {
        var path = Path.Combine(
            FindRepositoryRoot(), "Casazen.Infrastructure", "Data", "Seeds", "tourist-tax", "rates.csv");
        using var parser = new TextFieldParser(path) { TextFieldType = FieldType.Delimited, HasFieldsEnclosedInQuotes = true };
        parser.SetDelimiters(",");

        var header = parser.ReadFields()!.ToList();
        int Col(string name) => header.IndexOf(name) is var i and >= 0 ? i : throw new InvalidOperationException($"Column {name} missing");

        var rows = new List<CsvRow>();
        while (!parser.EndOfData)
        {
            var f = parser.ReadFields()!;
            rows.Add(new CsvRow(
                f[Col("istat_code")],
                f[Col("comune")],
                f[Col("rate_per_person_night_eur")],
                f[Col("max_nights")],
                f[Col("min_age_exempt")],
                f[Col("valid_from")],
                f[Col("zone_or_notes")],
                f[Col("source_url")],
                f[Col("verified")]));
        }

        Assert.NotEmpty(rows);
        return rows;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Casazen.sln")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new InvalidOperationException("Casazen.sln not found above the test output folder.");
    }

    private static AppDbContext NewNpgsqlContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(
                "Host=localhost;Database=casazen_design;Username=postgres;Password=postgres",
                npgsql => npgsql.MigrationsAssembly("Casazen.Infrastructure"))
            .Options);

    private sealed record CsvRow(
        string IstatCode,
        string Comune,
        string Rate,
        string MaxNights,
        string MinAgeExempt,
        string ValidFrom,
        string Notes,
        string SourceUrl,
        string Verified);
}
