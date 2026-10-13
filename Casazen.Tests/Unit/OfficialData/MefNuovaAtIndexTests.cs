using System.Security.Cryptography;
using System.Text;
using Casazen.Core.Entities;
using Casazen.Core.OfficialData;
using Xunit;

namespace Casazen.Tests.Unit.OfficialData;

public class MefNuovaAtIndexTests
{
    [Fact]
    public void ParseAndMatch_CesanoMadernoFixture_FindsAllegatoAndActId()
    {
        var csv = File.ReadAllBytes(Path.Combine(System.AppContext.BaseDirectory, "Fixtures", "mef", "nuova_at-cesano.csv"));
        var sha = Convert.ToHexString(SHA256.HashData(csv)).ToLowerInvariant();
        var snapshot = MefNuovaAtIndex.Parse(csv, sha, "https://www1.finanze.gov.it/finanze/test/elenco.csv");
        var comune = new Comune
        {
            IstatCode = "108019",
            CadastralCode = "C566",
            Name = "Cesano Maderno",
            ProvinceCode = "MB",
        };

        var match = MefNuovaAtIndex.Match(snapshot, comune);

        Assert.True(match.Succeeded);
        Assert.Equal("https://www1.finanze.gov.it/finanze/test/cesano-allegato-a.pdf", match.PdfUrl);
        Assert.Contains("Giunta 220/2024-11-19", match.ActId, StringComparison.Ordinal);
        Assert.Contains("Consiglio 133/2024-12-19", match.ActId, StringComparison.Ordinal);
        Assert.Equal(new DateOnly(2025, 1, 10), match.MefPublishedOn);
        Assert.Equal(64, match.ActSha256.Length);
    }

    [Fact]
    public void Match_AmbiguousNameWithoutCadastral_IsRejected()
    {
        var csv = Encoding.UTF8.GetBytes("""
            Comune;Provincia;Tributo;Tipo atto;Numero atto;Data atto;Data pubblicazione;URL
            Castro;BG;Imposta di soggiorno;Giunta;1;01/01/2025;10/01/2025;https://www1.finanze.gov.it/a.pdf
            Castro;LE;Imposta di soggiorno;Giunta;2;01/01/2025;10/01/2025;https://www1.finanze.gov.it/b.pdf
            """);
        var snapshot = MefNuovaAtIndex.Parse(csv, "abc", "https://www1.finanze.gov.it/elenco.csv");
        var comune = new Comune { IstatCode = "016065", Name = "Castro", ProvinceCode = "" };

        var match = MefNuovaAtIndex.Match(snapshot, comune);

        Assert.False(match.Succeeded);
        Assert.Equal("ambiguous_mef_row", match.Rejection);
    }

    [Fact]
    public void TryParse_CatalogPage_ReadsCsvPermalink()
    {
        const string html = """
            <p>Archivio atti nuova_at</p>
            <a href="https://www1.finanze.gov.it/finanze/dipartimentopolitichefiscali/fiscalitalocale/nuova_at/file/ElencoAtti.csv">CSV</a>
            """;

        Assert.True(MefNuovaAtCatalogPage.TryParse(html, out var url));
        Assert.Equal(
            "https://www1.finanze.gov.it/finanze/dipartimentopolitichefiscali/fiscalitalocale/nuova_at/file/ElencoAtti.csv",
            url.ToString());
    }
}
