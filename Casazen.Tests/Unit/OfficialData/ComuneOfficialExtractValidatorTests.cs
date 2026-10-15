using Casazen.Core.OfficialData;
using Xunit;

namespace Casazen.Tests.Unit.OfficialData;

public class ComuneOfficialExtractValidatorTests
{
    private const string PdfUrl = "https://www1.finanze.gov.it/finanze/test/cesano-allegato-a.pdf";

    [Fact]
    public void Validate_MissingActId_Rejects()
    {
        var json = """
            {"status":"extracted","actId":"","sourceUrl":"https://www1.finanze.gov.it/finanze/test/cesano-allegato-a.pdf","mefPublishedOn":"2025-01-10","rates":[{"calculationMethod":"PerPersonPerNight","ratePerPersonPerNight":2.5}],"legalValue":false}
            """;

        var result = ComuneOfficialExtractValidator.Validate(json, PdfUrl);

        Assert.False(result.Accepted);
        Assert.Equal("act_id_missing", result.Rejection);
    }

    [Fact]
    public void Validate_ActIdWithoutNumber_Rejects()
    {
        var json = """
            {"status":"extracted","actId":"Giunta","sourceUrl":"https://www1.finanze.gov.it/finanze/test/cesano-allegato-a.pdf","mefPublishedOn":"2025-01-10","rates":[{"calculationMethod":"PerPersonPerNight","ratePerPersonPerNight":2.5}],"legalValue":false}
            """;

        var result = ComuneOfficialExtractValidator.Validate(json, PdfUrl);

        Assert.False(result.Accepted);
        Assert.Equal("act_id_missing", result.Rejection);
    }

    [Fact]
    public void Validate_SourceUrlDifferentFromDownloadedPdf_Rejects()
    {
        var json = """
            {"status":"extracted","actId":"Giunta 220/2024-11-19","sourceUrl":"https://www1.finanze.gov.it/other.pdf","mefPublishedOn":"2025-01-10","rates":[{"calculationMethod":"PerPersonPerNight","ratePerPersonPerNight":2.5}],"legalValue":false}
            """;

        var result = ComuneOfficialExtractValidator.Validate(json, PdfUrl);

        Assert.False(result.Accepted);
        Assert.Equal("source_url_mismatch", result.Rejection);
    }

    [Fact]
    public void Validate_LegalValueTrue_Rejects()
    {
        var json = """
            {"status":"extracted","actId":"Giunta 220/2024-11-19","sourceUrl":"https://www1.finanze.gov.it/finanze/test/cesano-allegato-a.pdf","mefPublishedOn":"2025-01-10","rates":[{"calculationMethod":"PerPersonPerNight","ratePerPersonPerNight":2.5}],"legalValue":true}
            """;

        Assert.False(ComuneOfficialExtractValidator.Validate(json, PdfUrl).Accepted);
    }

    [Fact]
    public void Validate_ExtractedCesanoShape_Accepts()
    {
        var json = """
            {"status":"extracted","actId":"Giunta 220/2024-11-19; Consiglio 133/2024-12-19","sourceUrl":"https://www1.finanze.gov.it/finanze/test/cesano-allegato-a.pdf","mefPublishedOn":"2025-01-10","validFrom":"2025-03-01","rates":[{"accommodationCategory":"Locazioni brevi","calculationMethod":"PerPersonPerNight","ratePerPersonPerNight":2.5,"maxNights":7,"minimumAge":14}],"legalValue":false}
            """;

        var result = ComuneOfficialExtractValidator.Validate(json, PdfUrl);

        Assert.True(result.Accepted);
        Assert.NotNull(result.Extract);
        Assert.Equal(2.5m, result.Extract!.Rates[0].RatePerPersonPerNight);
    }
}
