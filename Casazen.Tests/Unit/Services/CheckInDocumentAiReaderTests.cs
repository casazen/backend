using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.External;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

public class CheckInDocumentAiReaderTests
{
    [Theory]
    [InlineData(null, "not_document")]
    [InlineData("unavailable", "unavailable")]
    [InlineData("unreadable", "unreadable")]
    public async Task ReaderFailureAndUnrecognizedPictureHaveDifferentReasons(string? failure, string expected)
    {
        var result = await ReadAsync("", new(null, failure));
        Assert.False(result.Read);
        Assert.Equal(expected, result.Reason);
    }

    [Fact]
    public async Task NamesPrintedOnAnUnrelatedImageCannotPrefillTheForm()
    {
        var result = await ReadAsync("""{"firstName":"Alba","lastName":"Rossi"}""",
            new(new() { GivenNames = "ALBA", Surname = "ROSSI" }));
        Assert.False(result.Read);
        Assert.Equal("not_document", result.Reason);
        Assert.Null(result.Fields);
    }

    [Fact]
    public async Task VerifiedCardFieldsReachThePublicResponse()
    {
        var card = new DocumentOcrResult
        {
            GivenNames = "ALBA",
            Surname = "ROSSI",
            Sex = "F",
            DocumentKind = "IdentityCard",
            HasDocumentEvidence = true,
            DocumentNumber = "CA12345ZZ",
            DateOfBirth = new DateTime(1994, 2, 12),
            BirthPlace = "ROMA",
            BirthProvince = "RM",
            PlaceOfIssue = "MILANO",
            DateOfIssue = new DateTime(2022, 3, 11),
            ExpirationDate = new DateTime(2032, 3, 11),
            Nationality = "ITA",
        };
        var result = await ReadAsync("", new(card));
        Assert.True(result.Read);
        Assert.Equal("front", result.Side);
        Assert.Equal(new CheckInDocumentFields("ALBA", "ROSSI", "Female", "1994-02-12", true, "ROMA", "RM",
            "ITA", "IdentityCard", "CA12345ZZ", "MILANO", "2022-03-11", "2032-03-11"), result.Fields);
    }

    [Fact]
    public async Task MachineReadableBackSuppliesIdentityAndPrintedResidenceOnSameImage()
    {
        var mrz = new MrzExtractionResult
        {
            CheckDigitsValid = true,
            Surname = "ROSSI",
            GivenNames = "ALBA",
            DocumentType = "IdCard",
            IssuingState = "ITA",
            DocumentNumber = "CA12345ZZ"
        };
        var printed = new DocumentOcrResult
        {
            ResidenceStreet = "VIA ROMA 8",
            ResidencePostalCode = "00100",
            ResidenceCityName = "ROMA",
            ResidenceProvince = "RM"
        };
        var result = await ReadAsync("", new(printed), mrz);
        Assert.True(result.Read);
        Assert.Equal("back", result.Side);
        Assert.Equal("CA12345ZZ", result.Fields!.DocumentNumber);
        Assert.Equal("VIA ROMA 8", result.Fields.ResidenceStreet);
        Assert.Equal("00100", result.Fields.ResidencePostalCode);
        Assert.Equal("ROMA", result.Fields.ResidenceCityName);
        Assert.Equal("RM", result.Fields.ResidenceProvince);
        Assert.True(result.Fields.ResidenceInItaly);
        Assert.Null(result.Fields.BirthPlace);
    }

    private static async Task<CheckInDocumentRead> ReadAsync(string answer, DocumentOcrObservation observation, MrzExtractionResult? machine = null)
    {
        var ai = new Mock<IAiProvider>();
        ai.Setup(provider => provider.GenerateAsync(It.IsAny<AiRequest>(), AiModelTier.Economy, "checkin-document", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiGenerationResult(answer, 0, 0, AiModelTier.Economy, false, answer.Length != 0, answer.Length != 0 ? 1 : 0));
        var visual = new Mock<IDocumentOcrService>();
        visual.Setup(reader => reader.ObserveAsync(It.IsAny<Stream>(), It.IsAny<CancellationToken>())).ReturnsAsync(observation);
        var mrz = new Mock<IMrzExtractionService>();
        mrz.Setup(reader => reader.ExtractAsync(It.IsAny<Stream>(), It.IsAny<CancellationToken>())).ReturnsAsync(machine);
        var reader = new CheckInDocumentAiReader(ai.Object, TimeProvider.System, NullLogger<CheckInDocumentAiReader>.Instance,
            mrz.Object, visual.Object);
        using var photo = new MemoryStream(TestImages.Png(32, 24, seed: 2));
        return await reader.ReadAsync(photo);
    }
}
