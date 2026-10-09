using System.Net;
using System.Text;
using Casazen.Core.Entities;
using Casazen.Core.OfficialData;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.OfficialData;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
using Casazen.Tests.Unit;

namespace Casazen.Tests.Unit.OfficialData;

public class OfficialReferenceDataRefreshServiceTests
{
    [Fact]
    public async Task RefreshAsync_Disabled_DoesNotWriteFetches()
    {
        await using var db = CreateDb();
        var service = CreateService(db, new OfficialReferenceDataOptions { Enabled = false });

        var result = await service.RefreshAsync();

        Assert.Equal("disabled", result.IstatComuni.Status);
        Assert.Empty(await db.OfficialSourceFetches.ToListAsync());
    }

    [Fact]
    public async Task RefreshAsync_IstatOfficialCsv_ImportsAndRecordsSource()
    {
        await using var db = CreateDb();
        const string csvUrl = OfficialReferenceDataOptions.DefaultIstatComuniCsvUrl;
        const string catalogUrl = OfficialReferenceDataOptions.DefaultIstatCatalogUrl;
        var csv = """
            Codice Regione;Codice Comune formato alfanumerico;Denominazione (Italiana e straniera);Denominazione in italiano;Denominazione Regione;Sigla automobilistica;Codice Catastale del Comune
            03;999001;Prova;Prova;Lombardia;MI;Z901
            """;
        var handler = new StubHandler
        {
            [catalogUrl] = Html("aggiornato al 21 febbraio 2026. <a href=\"" + csvUrl + "\">csv</a>"),
            [csvUrl] = Text(csv),
        };
        var options = new OfficialReferenceDataOptions
        {
            Enabled = true,
            AlloggiatiDownloadBaseUrl = "https://example.com/blocked",
        };
        var service = CreateService(db, options, handler);

        var result = await service.RefreshAsync();

        Assert.Equal(OfficialSourceFetchStatus.Imported, result.IstatComuni.Status);
        Assert.Equal(1, await db.Comuni.CountAsync());
        var import = await db.ComuneImports.SingleAsync();
        Assert.Equal(ComuneImportOrigin.ScheduledDownload, import.Origin);
        Assert.Equal(csvUrl, import.SourceUrl);
        Assert.Equal(IstatCatalogPage.IstatAuthority, import.Authority);
        Assert.Equal("999001", (await db.Comuni.SingleAsync()).IstatCode);
    }

    [Fact]
    public async Task RefreshAsync_TouristTaxPage_LogsAttemptAndDoesNotChangeAmounts()
    {
        await using var db = CreateDb();
        db.TouristTaxRates.Add(new TouristTaxRate
        {
            City = "Milano",
            IstatCode = "015146",
            RegionCode = "LOM",
            RatePerPersonPerNight = 9.50m,
            SourceUrl = "https://www.comune.milano.it/-/turismo.-approvate-tariffe-imposta-di-soggiorno-in-vigore-dal-1-gennaio-2026",
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        const string page = "https://www.comune.milano.it/-/turismo.-approvate-tariffe-imposta-di-soggiorno-in-vigore-dal-1-gennaio-2026";
        var handler = new StubHandler { [page] = Html("<html><body>Imposta di soggiorno</body></html>") };
        var options = new OfficialReferenceDataOptions
        {
            Enabled = true,
            IstatCatalogUrl = "https://example.com/istat",
            IstatComuniCsvUrl = "https://example.com/comuni.csv",
            AlloggiatiDownloadBaseUrl = "https://example.com/blocked",
            TouristTaxSources =
            [
                new OfficialTouristTaxSourceOptions
                {
                    IstatCode = "015146",
                    Name = "Milano",
                    Authority = "Comune di Milano",
                    SourceUrl = page,
                },
            ],
        };
        var service = CreateService(db, options, handler);

        var result = await service.RefreshAsync();

        var tax = Assert.Single(result.TouristTax);
        Assert.Equal(OfficialSourceFetchStatus.ExtractFailed, tax.Status);
        var rate = await db.TouristTaxRates.AsNoTracking().SingleAsync();
        Assert.Equal(9.50m, rate.RatePerPersonPerNight);
        Assert.Equal("Comune di Milano", rate.SourceAuthority);
        Assert.NotNull(rate.SourceRetrievedAt);
        Assert.Contains(await db.OfficialSourceFetches.ToListAsync(), f => f.Status == OfficialSourceFetchStatus.ExtractFailed && f.IstatCode == "015146");
    }

    [Fact]
    public async Task RefreshAsync_AlloggiatiPublicFile_ImportsWhenShaChanged()
    {
        await using var db = CreateDb();
        const string url = "https://alloggiatiweb.poliziadistato.it/PortaleAlloggiati/ashx/Download.ashx?ID=3&N=TIPO_ALLOGGIATO";
        var handler = new StubHandler
        {
            [url] = Text("Codice,Descrizione\n16,OSPITE SINGOLO\n17,CAPO FAMIGLIA\n18,CAPO GRUPPO\n19,FAMILIARE\n20,MEMBRO GRUPPO\n"),
        };
        var options = new OfficialReferenceDataOptions
        {
            Enabled = true,
            IstatCatalogUrl = "https://example.com/istat",
            IstatComuniCsvUrl = "https://example.com/comuni.csv",
            AlloggiatiDownloadBaseUrl = "https://alloggiatiweb.poliziadistato.it/PortaleAlloggiati/ashx/Download.ashx",
        };
        var service = CreateService(db, options, handler);

        var first = await service.RefreshAsync();
        var imported = first.Alloggiati.Single(r => r.Dataset == OfficialSourceDatasets.AlloggiatiTipiAlloggiato);
        Assert.Equal(OfficialSourceFetchStatus.Imported, imported.Status);
        Assert.Equal(5, await db.AlloggiatiCodeEntries.CountAsync(e => e.Table == AlloggiatiCodeTable.TipiAlloggiato));

        var second = await service.RefreshAsync();
        Assert.Equal(
            OfficialSourceFetchStatus.Unchanged,
            second.Alloggiati.Single(r => r.Dataset == OfficialSourceDatasets.AlloggiatiTipiAlloggiato).Status);
    }

    private static OfficialReferenceDataRefreshService CreateService(
        AppDbContext db,
        OfficialReferenceDataOptions options,
        StubHandler? handler = null)
    {
        var http = new HttpClient(handler ?? new StubHandler());
        return new OfficialReferenceDataRefreshService(
            new OfficialSourceDownloader(http),
            ComuneTestData.CreateImportService(db),
            new AlloggiatiCodeTableService(db, NullLogger<AlloggiatiCodeTableService>.Instance),
            db,
            Options.Create(options),
            NullLogger<OfficialReferenceDataRefreshService>.Instance);
    }

    private static AppDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    private static HttpResponseMessage Html(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/html") };

    private static HttpResponseMessage Text(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/plain") };

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, Func<HttpResponseMessage>> _map = new(StringComparer.OrdinalIgnoreCase);

        public HttpResponseMessage this[string url]
        {
            set
            {
                var status = value.StatusCode;
                var bytes = value.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
                var type = value.Content.Headers.ContentType?.ToString() ?? "text/plain";
                _map[url] = () =>
                {
                    var response = new HttpResponseMessage(status)
                    {
                        Content = new ByteArrayContent(bytes),
                    };
                    response.Content.Headers.TryAddWithoutValidation("Content-Type", type);
                    return response;
                };
            }
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            if (_map.TryGetValue(url, out var factory))
                return Task.FromResult(factory());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
