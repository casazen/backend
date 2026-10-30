using Casazen.Core.Repositories;
using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Microsoft.AspNetCore.Http;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// The code of an APE the file does not show legibly (the dark header of a photocopy), taken from the public register of Lombardy
/// with the comune and the index read on the file; the class read on the file must agree with the register's.
/// </summary>
public class ApeComplianceRegistryTests
{
    private static readonly ApeRegistryContext Monza = new("Monza", "LOM");
    private readonly Mock<IApeDocumentInspector> _inspector = new();
    private readonly Mock<IApeRegistryLookup> _registry = new();

    [Fact]
    public async Task CodeNotLegible_LombardApeWithItsIndex_TakesTheCodeOfTheRegister()
    {
        Reads(ApeInspectionStatus.Incomplete, ["CertificateCode"], new ApeReading(null, "F", new DateOnly(2018, 3, 15), new DateOnly(2028, 3, 15), 123.45m, true));
        _registry.Setup(r => r.FindAsync(new ApeRegistryQuery("Monza", 123.45m, new DateOnly(2018, 3, 15)), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApeRegistryEntry("12345000067819", "F", new DateOnly(2018, 3, 15)));

        var check = await Service().CheckUploadedApeAsync(PdfFile(), Monza);

        Assert.Equal(("12345000067819", "F", ApeUploadCheck.FromRegistry), (check.CertificateCode, check.EnergyClass, check.CodeSource));
        // The register gave what was missing: nothing is left "not read".
        Assert.Equal(ApeUploadCheck.Verified, check.Outcome);
        Assert.Empty(check.UnreadFields);
    }

    [Fact]
    public async Task RegisterClassDisagreesWithTheFile_ItsCodeIsNotTaken()
    {
        Reads(ApeInspectionStatus.Incomplete, ["CertificateCode"], new ApeReading(null, "F", null, null, 123.45m, true));
        _registry.Setup(r => r.FindAsync(It.IsAny<ApeRegistryQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApeRegistryEntry("12345000077777", "C", null));

        var check = await Service().CheckUploadedApeAsync(PdfFile(), Monza);

        Assert.Null(check.CertificateCode);
        Assert.Equal("F", check.EnergyClass);
        Assert.Contains("CertificateCode", check.UnreadFields);
    }

    [Fact]
    public async Task CodeReadOnTheFile_OrApeOfAnotherRegion_TheRegisterIsNotAsked()
    {
        Reads(ApeInspectionStatus.Valid, null, new ApeReading("123456789", "F", null, null, 123.45m, false));
        var onFile = await Service().CheckUploadedApeAsync(PdfFile(), Monza);
        Assert.Equal(("123456789", ApeUploadCheck.FromDocument), (onFile.CertificateCode, onFile.CodeSource));

        Reads(ApeInspectionStatus.Incomplete, ["CertificateCode"], new ApeReading(null, "D", null, null, 120m, false));
        var elsewhere = await Service().CheckUploadedApeAsync(PdfFile(), new ApeRegistryContext("Roma", "LAZ"));
        Assert.Null(elsewhere.CertificateCode);

        _registry.Verify(r => r.FindAsync(It.IsAny<ApeRegistryQuery>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CodeFromTheRegister_OfAnExpiredApe_IsStillRefused()
    {
        Reads(ApeInspectionStatus.Incomplete, ["CertificateCode"], new ApeReading(null, "G", new DateOnly(2012, 3, 1), new DateOnly(2022, 3, 1), 300m, true));
        _registry.Setup(r => r.FindAsync(It.IsAny<ApeRegistryQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApeRegistryEntry("1520100001212", "G", null));

        var refused = await Assert.ThrowsAsync<ApeComplianceException>(() => Service().CheckUploadedApeAsync(PdfFile(), Monza));

        Assert.Equal("ApeExpired", refused.MessageKey);
    }

    private void Reads(ApeInspectionStatus status, IReadOnlyList<string>? missing, ApeReading reading) =>
        _inspector.Setup(i => i.InspectAsync(It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApeInspectionResult(status, missing) { Reading = reading });

    private ApeComplianceService Service() =>
        new(Mock.Of<IPropertyDocumentRepository>(), Mock.Of<IImageStorageService>(), _inspector.Object, _registry.Object,
            new FixedTimeProvider(new DateTimeOffset(2026, 10, 23, 10, 0, 0, TimeSpan.Zero)));

    private static FormFile PdfFile() =>
        new(new MemoryStream("%PDF-1.4"u8.ToArray()), 0, 8, "file", "ape.pdf") { Headers = new HeaderDictionary(), ContentType = "application/pdf" };
}
