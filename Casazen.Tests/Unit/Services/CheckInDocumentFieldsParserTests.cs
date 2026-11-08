using Casazen.Core.Regulatory;
using Casazen.Core.Services;
using Xunit;

namespace Casazen.Tests.Unit.Services;

public class CheckInDocumentFieldsParserTests
{
    private static readonly DateOnly Today = new(2026, 11, 7);

    [Fact]
    public void Parse_ItalianIdentityCard_KeepsPlausibleFieldsAndDropsAFutureBirthDate()
    {
        const string json = """
            {"firstName":"Giulia","lastName":"Bianchi","gender":"F","dateOfBirth":"1990-04-02","bornInItaly":true,"birthPlace":"Roma","birthProvince":"RM","citizenship":"Italiana","documentKind":"IdentityCard","documentNumber":"ca 12345 ab","issuePlace":"Roma"}
            """;

        var fields = CheckInDocumentFieldsParser.Parse(json, Today);

        Assert.NotNull(fields);
        Assert.Equal("Giulia", fields.FirstName);
        Assert.Equal("Bianchi", fields.LastName);
        Assert.Equal("Female", fields.Gender);
        Assert.Equal("1990-04-02", fields.DateOfBirth);
        Assert.True(fields.BornInItaly);
        Assert.Equal("RM", fields.BirthProvince);
        Assert.Equal("Italiana", fields.Citizenship);
        Assert.Equal("IdentityCard", fields.DocumentKind);
        Assert.Equal("CA12345AB", fields.DocumentNumber);
    }

    [Fact]
    public void Parse_FutureBirthDateAndUnknownProvince_AreDropped()
    {
        const string json = """
            {"firstName":"Luca","dateOfBirth":"2026-11-12","birthProvince":"ER","documentNumber":"not a number!"}
            """;

        var fields = CheckInDocumentFieldsParser.Parse("```json\n" + json + "\n```", Today);

        Assert.NotNull(fields);
        Assert.Equal("Luca", fields.FirstName);
        Assert.Null(fields.DateOfBirth);
        Assert.Null(fields.BirthProvince);
        Assert.Null(fields.DocumentNumber);
    }

    [Fact]
    public void Citizenship_Italia_IsShownAsItaliana_AndItalianoFindsTheCountry()
    {
        Assert.Equal("Italiano", ItalianCitizenshipNames.LabelFor("ITALIA"));
        Assert.Equal("ITALIA", ItalianCitizenshipNames.OfficialNormalizedKey("italiano"));
        Assert.Equal("ITALIA", ItalianCitizenshipNames.OfficialNormalizedKey("Italiana"));
        Assert.False(AlloggiatiRecordRules.IsValidProvince("ER"));
        Assert.True(AlloggiatiRecordRules.IsValidProvince("rm"));
        Assert.True(AlloggiatiRecordRules.IsValidProvince("FO"));
        Assert.True(AlloggiatiRecordRules.IsValidProvince("ps"));
        Assert.True(AlloggiatiRecordRules.IsValidProvince("FU"));
        Assert.True(AlloggiatiRecordRules.IsValidProvince("PL"));
        Assert.True(AlloggiatiRecordRules.IsValidProvince("ZA"));
    }

    [Fact]
    public void Parse_OnlyProvinceKindAndIssuePlace_IsStillARead()
    {
        const string json = """
            {"bornInItaly":true,"birthProvince":"FO","documentKind":"Patente","issuePlace":"Forli"}
            """;

        var fields = CheckInDocumentFieldsParser.Parse(json, Today);

        Assert.NotNull(fields);
        Assert.True(fields.BornInItaly);
        Assert.Equal("FO", fields.BirthProvince);
        Assert.Equal("DriversLicense", fields.DocumentKind);
        Assert.Equal("Forli", fields.IssuePlace);
        Assert.Null(fields.FirstName);
        Assert.Null(fields.DocumentNumber);
    }
}
