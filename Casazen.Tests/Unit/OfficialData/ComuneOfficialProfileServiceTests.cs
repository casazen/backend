using System.Net;
using System.Text;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
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
using Moq;
using Xunit;
using Casazen.Tests.Unit;

namespace Casazen.Tests.Unit.OfficialData;

public class ComuneOfficialProfileServiceTests
{
    private const string CatalogUrl = OfficialReferenceDataOptions.DefaultMefNuovaAtCatalogUrl;
    private const string CsvUrl = OfficialReferenceDataOptions.DefaultMefNuovaAtCsvUrl;
    private const string PdfUrl = "https://www1.finanze.gov.it/finanze/test/cesano-allegato-a.pdf";
    private const string Cesano = "108019";

    [Fact]
    public async Task EnsureAsync_UnknownComune_RecordsRejectedAndStops()
    {
        await using var db = CreateDb();
        var ai = new Mock<IAiProvider>(MockBehavior.Strict);
        var service = CreateService(db, ai.Object, new StubHandler());

        var result = await service.EnsureAsync("000000");

        Assert.Equal(ComuneOfficialProfileStatus.Rejected, result.Status);
        Assert.Equal("comune_unknown", result.Detail);
        Assert.Equal(0, await db.ComuneOfficialProfiles.CountAsync());
        Assert.Equal(OfficialSourceFetchStatus.Rejected, (await db.OfficialSourceFetches.SingleAsync()).Status);
        ai.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task EnsureAsync_HostNotAllowed_DoesNotCallAi()
    {
        await using var db = CreateDb();
        await ComuneTestData.ImportSampleAsync(db);
        var csv = """
            Comune;Codice catastale;Tributo;Tipo atto;Numero atto;Data atto;Data pubblicazione;URL
            CESANO MADERNO;C566;Imposta di soggiorno;Giunta;220;19/11/2024;10/01/2025;https://example.com/not-allowed.pdf
            """;
        var handler = Handler(csv, pdf: null);
        var ai = new Mock<IAiProvider>(MockBehavior.Strict);
        var service = CreateService(db, ai.Object, handler);

        var result = await service.EnsureAsync(Cesano);

        Assert.Equal(ComuneOfficialProfileStatus.Rejected, result.Status);
        Assert.True(result.Detail is "pdf_url_not_allowed" or "host_not_allowed");
        Assert.Equal(0, await db.TouristTaxRates.CountAsync(r => r.IstatCode == Cesano));
        ai.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task EnsureAsync_RecentCurrentVersion_ReusesWithoutAi()
    {
        await using var db = CreateDb();
        await ComuneTestData.ImportSampleAsync(db);
        var now = new DateTimeOffset(2026, 4, 1, 8, 0, 0, TimeSpan.Zero);
        db.ComuneOfficialProfiles.Add(new ComuneOfficialProfile
        {
            IstatCode = Cesano,
            LastEnsuredAt = now.UtcDateTime,
            CreatedAt = now.UtcDateTime,
            UpdatedAt = now.UtcDateTime,
        });
        db.ComuneOfficialProfileVersions.Add(new ComuneOfficialProfileVersion
        {
            IstatCode = Cesano,
            VersionNumber = 1,
            IsCurrent = true,
            Status = ComuneOfficialProfileStatus.Unreadable,
            RetrievedAt = now.UtcDateTime.AddDays(-2),
            ValidFrom = new DateOnly(2025, 3, 1),
            ActSha256 = "abc",
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var ai = new Mock<IAiProvider>(MockBehavior.Strict);
        var service = CreateService(db, ai.Object, new StubHandler(), new FrozenClock(now));

        var result = await service.EnsureAsync(Cesano);

        Assert.True(result.Reused);
        Assert.Equal(ComuneOfficialProfileStatus.Unreadable, result.Status);
        Assert.Equal(1, await db.ComuneOfficialProfileVersions.CountAsync());
        ai.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task EnsureAsync_JsonWithoutActId_DoesNotTouchRates()
    {
        await using var db = CreateDb();
        await ComuneTestData.ImportSampleAsync(db);
        db.TouristTaxRates.Add(new TouristTaxRate
        {
            City = "Cesano Maderno",
            IstatCode = Cesano,
            RegionCode = "LOM",
            RatePerPersonPerNight = 1.00m,
            IsActive = true,
            EffectiveFrom = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var json = """
            {"status":"extracted","actId":"","sourceUrl":"https://www1.finanze.gov.it/finanze/test/cesano-allegato-a.pdf","mefPublishedOn":"2025-01-10","rates":[{"calculationMethod":"PerPersonPerNight","ratePerPersonPerNight":9.99}],"legalValue":false}
            """;
        var ai = AiThatReturns(json);
        var service = CreateService(db, ai.Object, Handler(CesanoCsv(), PdfWithText()));

        var result = await service.EnsureAsync(Cesano);

        Assert.False(result.RatesProjected);
        Assert.Equal(ComuneOfficialProfileStatus.Rejected, result.Status);
        var rate = await db.TouristTaxRates.SingleAsync(r => r.IstatCode == Cesano);
        Assert.Equal(1.00m, rate.RatePerPersonPerNight);
        Assert.Null(rate.EffectiveTo);
    }

    [Fact]
    public async Task EnsureAsync_JsonWithDifferentPdfUrl_DoesNotTouchRates()
    {
        await using var db = CreateDb();
        await ComuneTestData.ImportSampleAsync(db);
        var json = """
            {"status":"extracted","actId":"Giunta 220/2024-11-19","sourceUrl":"https://www1.finanze.gov.it/other.pdf","mefPublishedOn":"2025-01-10","rates":[{"calculationMethod":"PerPersonPerNight","ratePerPersonPerNight":2.5}],"legalValue":false}
            """;
        var ai = AiThatReturns(json);
        var service = CreateService(db, ai.Object, Handler(CesanoCsv(), PdfWithText()));

        var result = await service.EnsureAsync(Cesano);

        Assert.False(result.RatesProjected);
        Assert.Equal(0, await db.TouristTaxRates.CountAsync());
        Assert.Equal("source_url_mismatch", result.Detail);
    }

    [Fact]
    public async Task EnsureAsync_CesanoFixture_ValidFromMarch2025_AndProjectsRates()
    {
        await using var db = CreateDb();
        await ComuneTestData.ImportSampleAsync(db);
        var json = """
            {"status":"extracted","actId":"Giunta 220/2024-11-19; Consiglio 133/2024-12-19","sourceUrl":"https://www1.finanze.gov.it/finanze/test/cesano-allegato-a.pdf","mefPublishedOn":"2025-01-10","validFrom":"2024-01-01","rates":[{"accommodationCategory":"Locazioni brevi","calculationMethod":"PerPersonPerNight","ratePerPersonPerNight":2.5,"maxNights":7,"minimumAge":14}],"legalValue":false,"remittance":{"text":"Versamento entro il 16 del mese successivo."}}
            """;
        var ai = AiThatReturns(json);
        var service = CreateService(db, ai.Object, Handler(CesanoCsv(), PdfWithText()));

        var result = await service.EnsureAsync(Cesano);

        Assert.True(result.RatesProjected);
        Assert.Equal(ComuneOfficialProfileStatus.Extracted, result.Status);
        var version = await db.ComuneOfficialProfileVersions.SingleAsync();
        Assert.Equal(new DateOnly(2025, 3, 1), version.ValidFrom);
        Assert.Equal(new DateOnly(2025, 1, 10), version.MefPublishedOn);
        var rate = await db.TouristTaxRates.SingleAsync();
        Assert.Equal(2.5m, rate.RatePerPersonPerNight);
        Assert.Equal(TouristTaxRateVerification.Official, rate.VerificationLevel);
        Assert.Equal(PdfUrl, rate.SourceUrl);
        Assert.Equal(new DateTime(2025, 3, 1, 0, 0, 0, DateTimeKind.Utc), rate.EffectiveFrom);
        Assert.Contains("Giunta 220", version.ActId);
    }

    [Fact]
    public async Task EnsureAsync_UnreadablePdf_SavesAttemptWithoutAmount()
    {
        await using var db = CreateDb();
        await ComuneTestData.ImportSampleAsync(db);
        var ai = new Mock<IAiProvider>(MockBehavior.Strict);
        var service = CreateService(db, ai.Object, Handler(CesanoCsv(), PdfEmpty()));

        var result = await service.EnsureAsync(Cesano);

        Assert.Equal(ComuneOfficialProfileStatus.Unreadable, result.Status);
        Assert.Equal("ocr_unavailable", result.Detail);
        Assert.Equal(0, await db.TouristTaxRates.CountAsync());
        var version = await db.ComuneOfficialProfileVersions.SingleAsync();
        Assert.Equal(PdfUrl, version.SourceUrl);
        ai.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task RefreshAll_SameCsvSha_DoesNotCallAiOrAddVersion()
    {
        await using var db = CreateDb();
        await ComuneTestData.ImportSampleAsync(db);
        var json = """
            {"status":"extracted","actId":"Giunta 220/2024-11-19","sourceUrl":"https://www1.finanze.gov.it/finanze/test/cesano-allegato-a.pdf","mefPublishedOn":"2025-01-10","rates":[{"calculationMethod":"PerPersonPerNight","ratePerPersonPerNight":2.5}],"legalValue":false}
            """;
        var ai = AiThatReturns(json);
        var handler = Handler(CesanoCsv(), PdfWithText());
        var service = CreateService(db, ai.Object, handler);
        await service.EnsureAsync(Cesano);
        ai.Invocations.Clear();
        db.ChangeTracker.Clear();

        var monthly = CreateService(db, ai.Object, handler);
        var refresh = await monthly.RefreshAllAsync();

        Assert.Equal(OfficialSourceFetchStatus.Unchanged, refresh.IndexStatus);
        Assert.Equal(0, refresh.VersionsAdded);
        Assert.Equal(1, await db.ComuneOfficialProfileVersions.CountAsync());
        ai.Verify(
            p => p.GenerateAsync(It.IsAny<string>(), It.IsAny<AiModelTier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task RefreshAll_NewActSha_AddsVersionClosesPreviousRates()
    {
        await using var db = CreateDb();
        await ComuneTestData.ImportSampleAsync(db);
        var firstJson = """
            {"status":"extracted","actId":"Giunta 220/2024-11-19","sourceUrl":"https://www1.finanze.gov.it/finanze/test/cesano-allegato-a.pdf","mefPublishedOn":"2025-01-10","rates":[{"calculationMethod":"PerPersonPerNight","ratePerPersonPerNight":2.5}],"legalValue":false}
            """;
        var firstAi = AiThatReturns(firstJson);
        await CreateService(db, firstAi.Object, Handler(CesanoCsv(), PdfWithText())).EnsureAsync(Cesano);
        db.ChangeTracker.Clear();
        var firstRateId = (await db.TouristTaxRates.SingleAsync()).Id;

        var newCsv = CesanoCsv()
            .Replace("cesano-allegato-a.pdf", "cesano-allegato-b.pdf", StringComparison.Ordinal)
            .Replace("10/01/2025", "20/01/2026", StringComparison.Ordinal);
        var secondJson = """
            {"status":"extracted","actId":"Giunta 99/2026-01-15","sourceUrl":"https://www1.finanze.gov.it/finanze/test/cesano-allegato-b.pdf","mefPublishedOn":"2026-01-20","rates":[{"calculationMethod":"PerPersonPerNight","ratePerPersonPerNight":3.0}],"legalValue":false}
            """;
        var secondAi = AiThatReturns(secondJson);
        var refresh = await CreateService(db, secondAi.Object, Handler(newCsv, PdfWithText(), pdfUrl: "https://www1.finanze.gov.it/finanze/test/cesano-allegato-b.pdf"))
            .RefreshAllAsync();

        Assert.Equal(2, await db.ComuneOfficialProfileVersions.CountAsync());
        var previous = await db.ComuneOfficialProfileVersions.SingleAsync(v => v.VersionNumber == 1);
        var current = await db.ComuneOfficialProfileVersions.SingleAsync(v => v.IsCurrent);
        Assert.False(previous.IsCurrent);
        Assert.Equal(new DateOnly(2026, 3, 1).AddDays(-1), previous.ValidTo);
        Assert.Equal(new DateOnly(2026, 3, 1), current.ValidFrom);
        Assert.Equal(2, await db.TouristTaxRates.CountAsync());
        var closed = await db.TouristTaxRates.SingleAsync(r => r.Id == firstRateId);
        Assert.True(closed.IsActive);
        Assert.Equal(new DateTime(2026, 2, 28, 0, 0, 0, DateTimeKind.Utc), closed.EffectiveTo);
        var inserted = await db.TouristTaxRates.SingleAsync(r => r.Id != firstRateId);
        Assert.Equal(3.0m, inserted.RatePerPersonPerNight);
        Assert.Equal(1, refresh.VersionsAdded);
    }

    private static ComuneOfficialProfileService CreateService(
        AppDbContext db,
        IAiProvider ai,
        StubHandler handler,
        TimeProvider? clock = null)
    {
        var options = new OfficialReferenceDataOptions
        {
            Enabled = true,
            ComuneProfileReuseDays = 30,
        };
        return new ComuneOfficialProfileService(
            db,
            ComuneTestServices.Directory(db),
            new OfficialSourceDownloader(new HttpClient(handler)),
            ai,
            Options.Create(options),
            NullLogger<ComuneOfficialProfileService>.Instance,
            clock);
    }

    private static StubHandler Handler(string csv, byte[]? pdf, string pdfUrl = PdfUrl)
    {
        var handler = new StubHandler
        {
            [CatalogUrl] = Html(
                $"""<a href="{CsvUrl}">CSV nuova_at</a>"""),
            [CsvUrl] = Text(csv),
        };
        if (pdf is not null)
            handler[pdfUrl] = Pdf(pdf);
        return handler;
    }

    private static Mock<IAiProvider> AiThatReturns(string json)
    {
        var ai = new Mock<IAiProvider>();
        ai.Setup(p => p.GenerateAsync(It.IsAny<string>(), AiModelTier.Economy, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiGenerationResult(json, 10, 20, AiModelTier.Economy, false, true));
        return ai;
    }

    private static string CesanoCsv() =>
        File.ReadAllText(Path.Combine(System.AppContext.BaseDirectory, "Fixtures", "mef", "nuova_at-cesano.csv"));

    private static AppDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    private static HttpResponseMessage Html(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/html") };

    private static HttpResponseMessage Text(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/plain") };

    private static HttpResponseMessage Pdf(byte[] bytes)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        response.Content.Headers.TryAddWithoutValidation("Content-Type", "application/pdf");
        return response;
    }

    /// <summary>Not a PDF: the extractor reads UTF-8 when the file is not <c>%PDF</c>, so tests stay off live HTTP.</summary>
    private static byte[] PdfWithText() =>
        Encoding.UTF8.GetBytes("Allegato A — Imposta di soggiorno. Locazioni brevi. Giunta 220 del 19/11/2024.");

    private static byte[] PdfEmpty() => Encoding.ASCII.GetBytes("%PDF-1.1\n%%EOF");

    private sealed class FrozenClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

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
                    var response = new HttpResponseMessage(status) { Content = new ByteArrayContent(bytes) };
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
