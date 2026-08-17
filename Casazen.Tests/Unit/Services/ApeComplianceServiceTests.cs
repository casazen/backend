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
    public async Task EnsurePropertyHasValidApeAsync_WhenPdfIsNotAnApe_ThrowsInvalidContent()
    {
        _docs.Setup(d => d.GetByPropertyIdAsync(PropertyId)).ReturnsAsync([ApeDoc("/fake.pdf")]);
        _storage.Setup(s => s.OpenReadAsync("/fake.pdf"))
            .ReturnsAsync(PdfStream("Contratto di locazione", "Nessun certificato energetico."));

        var ex = await Assert.ThrowsAsync<ApeComplianceException>(() =>
            _sut.EnsurePropertyHasValidApeAsync(PropertyId));

        Assert.Equal(ApeComplianceException.InvalidContentCode, ex.Code);
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
    public async Task EnsureUploadedFileIsOfficialApeAsync_WhenPdfIsNotAnApe_Throws()
    {
        var file = FormFile(PdfBytes("Contratto", "Locazione uso abitativo"));

        var ex = await Assert.ThrowsAsync<ApeComplianceException>(() =>
            _sut.EnsureUploadedFileIsOfficialApeAsync(file));

        Assert.Equal(ApeComplianceException.InvalidContentCode, ex.Code);
    }

    [Fact]
    public async Task EnsureUploadedFileIsOfficialApeAsync_WhenOfficialApe_Succeeds()
    {
        var file = FormFile(OfficialApeBytes());

        await _sut.EnsureUploadedFileIsOfficialApeAsync(file);
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
        "Classe energetica C. EPgl,nren 120. SIAPE. Certificatore energetico.");

    private static byte[] PdfBytes(string title, string body) => FiscalPdfWriter.Write(title, body);

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
