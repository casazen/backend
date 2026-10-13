using Casazen.Core.OfficialData;
using Xunit;

namespace Casazen.Tests.Unit.OfficialData;

public class OfficialHostAllowlistTests
{
    [Theory]
    [InlineData("https://www.istat.it/storage/codici-unita-amministrative/Elenco-comuni-italiani.csv")]
    [InlineData("https://alloggiatiweb.poliziadistato.it/PortaleAlloggiati/ashx/Download.ashx?ID=0&N=COMUNI")]
    [InlineData("https://www.comune.milano.it/-/turismo")]
    [InlineData("https://servizi.comune.fi.it/sites/file.pdf")]
    [InlineData("https://www.comune.seveso.mb.it/it/page/imposte-e-tasse-1")]
    [InlineData("https://dati.gov.it/view-dataset")]
    public void IsAllowed_InstitutionalHttpsHosts_AreAccepted(string url)
    {
        Assert.True(OfficialHostAllowlist.TryCreateAllowed(url, out _));
    }

    [Theory]
    [InlineData("http://www.istat.it/file.csv")]
    [InlineData("https://example.com/comuni.csv")]
    [InlineData("https://wikipedia.org/wiki/Imposta_di_soggiorno")]
    [InlineData("ftp://www.comune.como.it/file.pdf")]
    [InlineData("not-a-url")]
    public void IsAllowed_NonInstitutionalOrNonHttps_AreRefused(string url)
    {
        Assert.False(OfficialHostAllowlist.TryCreateAllowed(url, out _));
    }
}
