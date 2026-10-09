using Casazen.Core.Entities.Enums;
using Casazen.Infrastructure.OfficialData;
using Xunit;

namespace Casazen.Tests.Unit.OfficialData;

public class TouristTaxOfficialExtractorTests
{
    [Fact]
    public void Milano_HtmlApril2026Table_ReadsLocazioniBrevi()
    {
        var html = """
            Tariffe dal 1° aprile 2026
            Locazioni Brevi ex D.L. 50/2017: 9,50 €
            Tariffe dal 1° gennaio 2026
            Case e appartamenti per vacanze e locazioni brevi, ex D.L. n. 50/2017: 9,50 €
            """;

        var extraction = TouristTaxOfficialExtractor.TryExtractFromText("015146", html);

        Assert.True(extraction.Succeeded, extraction.Detail);
        var rate = Assert.Single(extraction.Rates);
        Assert.Equal(9.50m, rate.RatePerPersonPerNight);
        Assert.Null(rate.AccommodationCategory);
    }

    [Fact]
    public void Napoli_HtmlTwoColumns_ReadsMay2026LocazioniBrevi()
    {
        var html = """
            | Categoria | €/notte Dal 01/01/2026 | €/notte Dal 01/05/2026 |
            | Locazioni brevi | € 5,00 | € 6,00 |
            """;

        var extraction = TouristTaxOfficialExtractor.TryExtractFromText("063049", html);

        Assert.True(extraction.Succeeded, extraction.Detail);
        Assert.Equal(6.00m, Assert.Single(extraction.Rates).RatePerPersonPerNight);
    }

    [Fact]
    public void Torino_HtmlTable_ReadsLocazioniTuristicheEBrevi()
    {
        var html = """
            TARIFFE VIGENTI DAL 1° APRILE 2026
            | Locazioni Turistiche e Brevi | 3,80 |
            """;

        var extraction = TouristTaxOfficialExtractor.TryExtractFromText("001272", html);

        Assert.True(extraction.Succeeded, extraction.Detail);
        var rate = Assert.Single(extraction.Rates);
        Assert.Equal(3.80m, rate.RatePerPersonPerNight);
        Assert.Equal(7, rate.MaxNights);
    }

    [Fact]
    public void Como_PdfText_ReadsLocazioniBrevi()
    {
        var text = """
            Case e appartamenti per vacanze (CAV) - locazioni turistiche e locazioni brevi € 3,00
            Tariffe approvate con delibera di Giunta Comunale n. 387 del 10-11-2023 e dovute fino ad un massimo di 4 giorni di pernottamenti consecutivi
            """;

        var extraction = TouristTaxOfficialExtractor.TryExtractFromText("013075", text);

        Assert.True(extraction.Succeeded, extraction.Detail);
        var rate = Assert.Single(extraction.Rates);
        Assert.Equal(3.00m, rate.RatePerPersonPerNight);
        Assert.Equal(4, rate.MaxNights);
    }

    [Fact]
    public void Firenze_DeliberaText_ReadsLocazioniTuristiche()
    {
        var text = """
            Residenze d'epoca 7,00
            Locazioni di immobili ad uso turistico 6,00
            """;

        var extraction = TouristTaxOfficialExtractor.TryExtractFromText("048017", text);

        Assert.True(extraction.Succeeded, extraction.Detail);
        Assert.Equal(6.00m, Assert.Single(extraction.Rates).RatePerPersonPerNight);
    }

    [Fact]
    public void Roma_PdfTable_ReadsLocazioneBreveAndCav()
    {
        var text = """
            Case e Appartamenti per vacanze - Categoria 1 Euro 6,00 10
            Case e Appartamenti per vacanze - Categoria 2 Euro 5,00 10
            Immobili destinati alla locazione breve (art. 4, D.L. n. 50/2017, convertito, con L. n. 96/2017) Euro 6,00 10
            """;

        var extraction = TouristTaxOfficialExtractor.TryExtractFromText("058091", text);

        Assert.True(extraction.Succeeded, extraction.Detail);
        Assert.Equal(3, extraction.Rates.Count);
        Assert.Contains(extraction.Rates, r => r.AccommodationCategory == "Immobili destinati alla locazione breve" && r.RatePerPersonPerNight == 6.00m);
        Assert.Contains(extraction.Rates, r => r.AccommodationCategory!.Contains("categoria 1", StringComparison.OrdinalIgnoreCase) && r.RatePerPersonPerNight == 6.00m);
        Assert.Contains(extraction.Rates, r => r.AccommodationCategory!.Contains("categoria 2", StringComparison.OrdinalIgnoreCase) && r.RatePerPersonPerNight == 5.00m);
    }

    [Fact]
    public void Venezia_PdfLocazioniSection_ReadsAllGroupsIncludingGruppo3Alta()
    {
        var text = """
            LOCAZIONI TURISTICHE art. 27 bis L.R.V. 11/2013
            ALTA STAGIONE 1 febbraio - 31 dicembre TARIFFA BASE
            Tipologia Intero Ridotto 50%
            GRUPPO 1: A/1 A/8 A/9 5,00 2,50
            GRUPPO 2: A/2 A/3 A/6 A/7 A/11 4,00 2,00
            GRUPPO 3: A/4 A/5 3,00 1,50
            BASSA STAGIONE 1 gennaio - 31 gennaio TARIFFA BASE RIDOTTA DEL 30%
            GRUPPO 1: A/1 A/8 A/9 3,50 1,70
            GRUPPO 2: A/2 A/3 A/6 A/7 A/11 2,80 1,40
            GRUPPO 3: A/4 A/5 2,10 1,00
            """;

        var extraction = TouristTaxOfficialExtractor.TryExtractFromText("027042", text);

        Assert.True(extraction.Succeeded, extraction.Detail);
        Assert.Equal(6, extraction.Rates.Count);
        Assert.Contains(extraction.Rates, r =>
            r.AccommodationCategory!.StartsWith("Gruppo 3", StringComparison.Ordinal)
            && r.SeasonStart == "02-01"
            && r.RatePerPersonPerNight == 3.00m
            && r.ReducedRatePerPersonPerNight == 1.50m);
        Assert.Contains(extraction.Rates, r =>
            r.AccommodationCategory!.StartsWith("Gruppo 1", StringComparison.Ordinal)
            && r.SeasonStart == "01-01"
            && r.RatePerPersonPerNight == 3.50m
            && r.ReducedRatePerPersonPerNight == 1.70m);
    }

    [Fact]
    public void Bologna_DeliberaText_ReadsPercentAndCap()
    {
        var text = """
            ipotesi di locazione breve di cui all'art.4 comma 1 e 5 ter del D.L.50/17
            tariffa in misura percentuale del 10,50% sul costo dell'appartamento (al netto di IVA e di eventuali servizi aggiuntivi) con il limite massimo di 7 euro a persona per notte di soggiorno
            """;

        var extraction = TouristTaxOfficialExtractor.TryExtractFromText("037006", text);

        Assert.True(extraction.Succeeded, extraction.Detail);
        var rate = Assert.Single(extraction.Rates);
        Assert.Equal(TouristTaxCalculationMethod.PercentOfNightlyPrice, rate.CalculationMethod);
        Assert.Equal(10.50m, rate.PercentOfNightlyPrice);
        Assert.Equal(7.00m, rate.CapPerPersonPerNight);
    }

    [Theory]
    [InlineData("108040")]
    [InlineData("108019")]
    public void SevesoAndCesanoMaderno_HaveNoTariff(string istat)
    {
        var extraction = TouristTaxOfficialExtractor.TryExtractFromText(istat, "IMU TARI Canone unico patrimoniale");

        Assert.False(extraction.Succeeded);
        Assert.Equal("no_tourist_tax_tariff_in_document", extraction.Detail);
        Assert.Empty(extraction.Rates);
    }

    [Fact]
    public void Milano_PageWithoutAmount_DoesNotInvent()
    {
        var extraction = TouristTaxOfficialExtractor.TryExtractFromText("015146", "Imposta di soggiorno: informazioni per i gestori");

        Assert.False(extraction.Succeeded);
        Assert.Empty(extraction.Rates);
    }

    [Theory]
    [InlineData("roma.bin", "058091")]
    [InlineData("como.bin", "013075")]
    [InlineData("firenze.bin", "048017")]
    [InlineData("venezia.bin", "027042")]
    [InlineData("bologna.bin", "037006")]
    public void DownloadedOfficialPdf_WhenPresent_ExtractsAmount(string fileName, string istatCode)
    {
        var path = Path.Combine(FindRepositoryRoot(), "tmp-tourist-tax", fileName);
        if (!File.Exists(path))
            return;

        var extraction = TouristTaxOfficialExtractor.TryExtract(istatCode, File.ReadAllBytes(path));
        Assert.True(extraction.Succeeded, $"{fileName}: {extraction.Detail}");
        Assert.NotEmpty(extraction.Rates);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Casazen.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Casazen.sln not found.");
    }
}
