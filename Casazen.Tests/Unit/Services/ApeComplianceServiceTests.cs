using Casazen.Core.Entities;
using Casazen.Core.Enums;
using Casazen.Core.Repositories;
using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Microsoft.AspNetCore.Http;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

public class ApeComplianceServiceTests
{
    private readonly Mock<IPropertyDocumentRepository> _docs = new();
    private readonly Mock<IImageStorageService> _storage = new();
    private readonly IApeDocumentInspector _inspector = new ApeDocumentInspector();
    private readonly ApeComplianceService _sut;
    private static readonly Guid PropertyId = Guid.NewGuid();

    public ApeComplianceServiceTests()
    {
        _sut = new ApeComplianceService(_docs.Object, _storage.Object, _inspector);
    }

    [Fact]
    public async Task EnsurePropertyHasValidApeAsync_WhenNoApeRow_ThrowsRequired()
    {
        _docs.Setup(d => d.GetByPropertyIdAsync(PropertyId))
            .ReturnsAsync([new PropertyDocument { DocumentType = DocumentType.FloorPlan, StorageUrl = "/plan.pdf" }]);

        var ex = await Assert.ThrowsAsync<ApeComplianceException>(() =>
            _sut.EnsurePropertyHasValidApeAsync(PropertyId));

        Assert.Equal(ApeComplianceException.RequiredCode, ex.Code);
    }

    [Fact]
    public async Task EnsurePropertyHasValidApeAsync_WhenTheFileIsNotReadWhole_AsksForCodeAndClass()
    {
        _docs.Setup(d => d.GetByPropertyIdAsync(PropertyId)).ReturnsAsync([ApeDoc("/fake.pdf")]);
        _storage.Setup(s => s.OpenReadAsync("/fake.pdf"))
            .ReturnsAsync(PdfStream("Contratto di locazione", "Nessun certificato energetico."));

        var ex = await Assert.ThrowsAsync<ApeComplianceException>(() =>
            _sut.EnsurePropertyHasValidApeAsync(PropertyId));

        Assert.Equal(ApeComplianceException.IdentificationRequiredCode, ex.Code);
        Assert.Equal("ApeIdentificationRequired", ex.MessageKey);
    }

    [Fact]
    public async Task EnsurePropertyHasValidApeAsync_WithCodeAndClassTyped_SucceedsWithoutReadingTheFile()
    {
        // The landlord confirmed a certificate the reader could not read whole: the typed data stand for it, nothing is read again.
        var ape = ApeDoc("/photocopy.pdf");
        ape.ApeCode = "1610800012345/17";
        ape.ApeEnergyClass = "F";
        _docs.Setup(d => d.GetByPropertyIdAsync(PropertyId)).ReturnsAsync([ape]);

        await _sut.EnsurePropertyHasValidApeAsync(PropertyId);

        _storage.Verify(s => s.OpenReadAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task EnsurePropertyHasValidApeAsync_WhenOnlyAnExpiredApe_ThrowsExpired()
    {
        _docs.Setup(d => d.GetByPropertyIdAsync(PropertyId)).ReturnsAsync([ApeDoc("/old.pdf")]);
        _storage.Setup(s => s.OpenReadAsync("/old.pdf")).ReturnsAsync(new MemoryStream(ExpiredApeBytes()));

        var ex = await Assert.ThrowsAsync<ApeComplianceException>(() =>
            _sut.EnsurePropertyHasValidApeAsync(PropertyId));

        Assert.Equal("ApeExpired", ex.MessageKey);
    }

    [Fact]
    public async Task EnsurePropertyHasValidApeAsync_WhenOfficialApePdfPresent_Succeeds()
    {
        _docs.Setup(d => d.GetByPropertyIdAsync(PropertyId)).ReturnsAsync([ApeDoc("/ape.pdf")]);
        _storage.Setup(s => s.OpenReadAsync("/ape.pdf")).ReturnsAsync(OfficialApeStream());

        await _sut.EnsurePropertyHasValidApeAsync(PropertyId);
    }

    [Fact]
    public async Task EnsurePropertyHasValidApeAsync_WhenFirstFileFakeAndSecondValid_Succeeds()
    {
        _docs.Setup(d => d.GetByPropertyIdAsync(PropertyId)).ReturnsAsync(
        [
            ApeDoc("/fake.pdf"),
            ApeDoc("/real-ape.pdf")
        ]);
        _storage.Setup(s => s.OpenReadAsync("/fake.pdf"))
            .ReturnsAsync(PdfStream("Ricevuta", "Pagamento bolletta."));
        _storage.Setup(s => s.OpenReadAsync("/real-ape.pdf")).ReturnsAsync(OfficialApeStream());

        await _sut.EnsurePropertyHasValidApeAsync(PropertyId);
    }

    [Fact]
    public async Task CheckUploadedApeAsync_WhenPdfIsNotRecognised_KeepsItForTheLandlordToConfirm()
    {
        var file = FormFile(PdfBytes("Contratto", "Locazione uso abitativo"));

        var check = await _sut.CheckUploadedApeAsync(file);

        Assert.Equal(ApeUploadCheck.NotRecognized, check.Outcome);
    }

    [Fact]
    public async Task CheckUploadedApeAsync_WhenOfficialApe_IsVerified()
    {
        var file = FormFile(OfficialApeBytes());

        var check = await _sut.CheckUploadedApeAsync(file);

        Assert.Equal(ApeUploadCheck.Verified, check.Outcome);
    }

    [Fact]
    public async Task CheckUploadedApeAsync_WhenExpired_Throws()
    {
        var file = FormFile(ExpiredApeBytes());

        var ex = await Assert.ThrowsAsync<ApeComplianceException>(() => _sut.CheckUploadedApeAsync(file));

        Assert.Equal("ApeExpired", ex.MessageKey);
    }

    [Theory]
    [InlineData("complete.jpg", true)]
    [InlineData("page-1.jpg", false)]
    [InlineData("scanned-two-pages.pdf", true)]
    public async Task Upload_RunsRealOcrAcrossAllPages(string fixture, bool valid)
    {
        var bytes = await File.ReadAllBytesAsync(Path.Combine(System.AppContext.BaseDirectory, "Fixtures", "ape", fixture));
        var file = new FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", fixture)
        { Headers = new HeaderDictionary(), ContentType = fixture.EndsWith("pdf") ? "application/pdf" : "image/jpeg" };
        var check = await _sut.CheckUploadedApeAsync(file);
        if (valid) Assert.Equal(ApeUploadCheck.Verified, check.Outcome);
        else
        {
            // Only the first page: kept, with what could not be read, for the landlord to confirm.
            Assert.Equal(ApeUploadCheck.Incomplete, check.Outcome);
            Assert.Contains("Certifier", check.UnreadFields);
            Assert.Contains("ExpiryDate", check.UnreadFields);
        }
    }

    [Fact]
    public async Task Upload_TextRenamedToJpeg_StillFails()
    {
        var bytes = "<html>not an image</html>"u8.ToArray();
        var file = new FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", "ape.jpeg") { Headers = new HeaderDictionary(), ContentType = "image/jpeg" };
        await Assert.ThrowsAsync<ApeComplianceException>(() => _sut.CheckUploadedApeAsync(file));
    }

    private static PropertyDocument ApeDoc(string url) => new()
    {
        DocumentType = DocumentType.Ape,
        FileName = Path.GetFileName(url),
        StorageUrl = url,
        UploadedBy = "owner"
    };

    private static Stream OfficialApeStream() => new MemoryStream(OfficialApeBytes());

    private static Stream PdfStream(string title, string body) => new MemoryStream(PdfBytes(title, body));

    private static byte[] OfficialApeBytes() => PdfBytes(
        "ATTESTATO DI PRESTAZIONE ENERGETICA",
        "Classe energetica C. EPgl,nren 120.\nCodice identificativo APE 123456.\nIndirizzo Via Esempio 12.\nSoggetto certificatore Mario Esempio.\nData di emissione 01/01/2025.\nValido fino al 01/01/2035.");

    private static byte[] ExpiredApeBytes() => PdfBytes(
        "ATTESTATO DI PRESTAZIONE ENERGETICA",
        "Classe energetica C. EPgl,nren 120.\nCodice identificativo APE 123456.\nIndirizzo Via Esempio 12.\nSoggetto certificatore Mario Esempio.\nData di emissione 01/01/2010.\nValido fino al 01/01/2020.");

    private static byte[] PdfBytes(string title, string body) => LiteralTextPdf.Build(title, body);

    private static IFormFile FormFile(byte[] bytes)
    {
        var stream = new MemoryStream(bytes);
        var file = new Mock<IFormFile>();
        file.Setup(f => f.FileName).Returns("upload.pdf");
        file.Setup(f => f.Length).Returns(bytes.Length);
        file.Setup(f => f.ContentType).Returns("application/pdf");
        file.Setup(f => f.OpenReadStream()).Returns(stream);
        file.Setup(f => f.CopyToAsync(It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .Returns((Stream target, CancellationToken token) =>
            {
                stream.Position = 0;
                return stream.CopyToAsync(target, token);
            });
        return file.Object;
    }
}
