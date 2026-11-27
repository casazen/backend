using Casazen.Core.Options;
using Casazen.Infrastructure.Identity;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Casazen.Tests.Unit.Identity;

/// <summary>
/// E71/F01/US03/T01 (NF-191, AC-NF191-2): the format of a document from what the REAL reader of the MRZ says (<c>DocumentCode</c>, <c>IssuingState</c>, <c>MrzType</c> of the
/// result of the parsers, specimens built here from the ones of ICAO Doc 9303 with the state of the card), never from values written by hand: the Italian card is the registered
/// format and anything else is no format. A character the OCR reads wrong in the document code (an I read as 1) is brought back with the other letters, so it does not make a card
/// of the registry an unknown one.
/// </summary>
public sealed class MrzFormatDetectionTests
{
    private static readonly DateOnly Today = new(2026, 11, 5);

    private static readonly DocumentFormatRegistry Registry = new(
        Options.Create(new IdentityVerificationOptions()), NullLogger<DocumentFormatRegistry>.Instance, System.AppContext.BaseDirectory);

    [Theory]
    [InlineData("I<", "ITA")]
    [InlineData("ID", "ITA")]
    [InlineData("CI", "ITA")]
    [InlineData("C<", "ITA")]
    public void RealParser_Td1OfTheItalianState_IsTheCie(string documentCode, string state)
    {
        var result = ParseTd1(documentCode, state);

        Assert.NotNull(result);
        Assert.True(result.CheckDigitsValid);
        Assert.Equal(documentCode, result.DocumentCode);
        Assert.Equal(state, result.IssuingState);
        Assert.Equal("TD1", result.MrzType);
        Assert.Equal("it-cie-3.0", Registry.DetectFromMrz(result.DocumentCode, result.IssuingState, result.MrzType)?.Id);
    }

    [Theory]
    [InlineData("I<", "UTO")]
    [InlineData("I<", "DEU")]
    [InlineData("IR", "ITA")]
    [InlineData("AR", "ITA")]
    public void RealParser_Td1OfAnotherStateOrOfAResidencePermit_IsNoFormat(string documentCode, string state)
    {
        var result = ParseTd1(documentCode, state);

        Assert.NotNull(result);
        Assert.Equal(documentCode, result.DocumentCode);
        Assert.Equal(state, result.IssuingState);
        Assert.Equal("TD1", result.MrzType);
        Assert.Null(Registry.DetectFromMrz(result.DocumentCode, result.IssuingState, result.MrzType));
    }

    [Fact]
    public void RealParser_Td2AndTd3OfTheItalianState_AreNoFormat_AndSayTheirType()
    {
        // The only registered format is a card of three lines: a residence permit or a passport of Italy is read as such and is not it.
        var td2 = MrzExtractionService.ParseTd2Mrz("I<ITA" + MrzSpecimens.Td2Line1[5..], MrzSpecimens.Td2Line2, Today);
        var td3 = MrzExtractionService.ParseTd3Mrz("P<ITA" + MrzSpecimens.Td3Line1[5..], MrzSpecimens.Td3Line2, Today);

        Assert.NotNull(td2);
        Assert.NotNull(td3);
        Assert.Equal("I<", td2.DocumentCode);
        Assert.Equal("ITA", td2.IssuingState);
        Assert.Equal("TD2", td2.MrzType);
        Assert.Equal("P<", td3.DocumentCode);
        Assert.Equal("ITA", td3.IssuingState);
        Assert.Equal("TD3", td3.MrzType);
        Assert.Null(Registry.DetectFromMrz(td2.DocumentCode, td2.IssuingState, td2.MrzType));
        Assert.Null(Registry.DetectFromMrz(td3.DocumentCode, td3.IssuingState, td3.MrzType));
    }

    [Theory]
    [InlineData("1<", "I<")]
    [InlineData("C1", "CI")]
    [InlineData("1D", "ID")]
    public void RealParser_ACharacterTheOcrReadAsADigit_IsBroughtBackToItsLetter_AndTheCardIsStillTheCie(string read, string expected)
    {
        var result = ParseTd1(read, "ITA");

        Assert.NotNull(result);
        Assert.Equal(expected, result.DocumentCode);
        Assert.Equal("it-cie-3.0", Registry.DetectFromMrz(result.DocumentCode, result.IssuingState, result.MrzType)?.Id);
    }

    /// <summary>The TD1 specimen of ICAO with the given document code and issuing state: neither is covered by a check digit, so the digits of the specimen still hold.</summary>
    private static Casazen.Core.Services.MrzExtractionResult? ParseTd1(string documentCode, string state) =>
        MrzExtractionService.ParseTd1Mrz(documentCode + state + MrzSpecimens.Td1Line1[5..], MrzSpecimens.Td1Line2, MrzSpecimens.Td1Line3, Today);
}
