using Casazen.Core.OfficialData;
using Xunit;

namespace Casazen.Tests.Unit.OfficialData;

public class IstatCatalogPageTests
{
    [Fact]
    public void TryParse_CatalogWording_ReadsDateAndPermalink()
    {
        const string html = """
            <p>Elenco dei comuni italiani, aggiornato al 21 febbraio 2026</p>
            <a href="https://www.istat.it/storage/codici-unita-amministrative/Elenco-comuni-italiani.csv">CSV</a>
            """;

        Assert.True(IstatCatalogPage.TryParse(html, out var snapshot));
        Assert.Equal(new DateOnly(2026, 2, 21), snapshot.ReferenceDate);
        Assert.Equal(
            "https://www.istat.it/storage/codici-unita-amministrative/Elenco-comuni-italiani.csv",
            snapshot.PermalinkUrl);
    }

    [Fact]
    public void TryParse_MissingDate_FailsWithoutInventing()
    {
        Assert.False(IstatCatalogPage.TryParse("<p>Elenco dei comuni italiani</p>", out _));
    }
}
