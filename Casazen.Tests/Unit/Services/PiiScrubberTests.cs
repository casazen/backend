using Casazen.Infrastructure.External;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// Unit tests for <see cref="PiiScrubber"/> (FD-21, PO 2026-10-08).
/// Each test class covers one pattern family; the integration class verifies that multiple PII types
/// are removed in a single pass.
/// </summary>
public class PiiScrubberTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Scrub_NullOrEmpty_ReturnsEmptyString(string? text)
    {
        Assert.Equal(string.Empty, PiiScrubber.Scrub(text));
    }

    [Fact]
    public void Scrub_NoPatterns_ReturnsInputUnchanged()
    {
        const string clean = "Pulizie Roma Srl, Via Nazionale 1, Roma. Aperto dal lunedì al venerdì.";
        Assert.Equal(clean, PiiScrubber.Scrub(clean));
    }

    [Theory]
    [InlineData("mario.rossi@example.com")]
    [InlineData("m.rossi+tag@sub.domain.it")]
    [InlineData("info@pulizie-roma.co.uk")]
    [InlineData("UPPERCASE@DOMAIN.IT")]
    public void Scrub_Email_ReplacedWithPlaceholder(string email)
    {
        var result = PiiScrubber.Scrub($"Contattaci: {email} per informazioni.");
        Assert.DoesNotContain(email, result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[email]", result);
    }

    [Fact]
    public void Scrub_MultipleEmails_AllReplaced()
    {
        var text = "Scrivi a info@alfa.it o assistenza@beta.com per supporto.";
        var result = PiiScrubber.Scrub(text);
        Assert.DoesNotContain("@", result);
        Assert.Equal(2, CountOccurrences(result, "[email]"));
    }

    [Theory]
    [InlineData("RSSMRA80A01H501Z")]
    [InlineData("rssmra80a01h501z")]
    [InlineData("BNCGPP75D12F205R")]
    public void Scrub_FiscalCode_ReplacedWithPlaceholder(string cf)
    {
        var result = PiiScrubber.Scrub($"Il codice fiscale dell'ospite è {cf}.");
        Assert.DoesNotContain(cf, result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[cf]", result);
    }

    [Fact]
    public void Scrub_FiscalCode_DoesNotMatchShorterAlphanumericTokens()
    {
        // ISTAT code (6 digits), slugs and other short tokens must survive.
        const string text = "Comune 013075 - codice regione LOM - slug como-co";
        Assert.Equal(text, PiiScrubber.Scrub(text));
    }

    [Theory]
    [InlineData("+39 06 12345678")]
    [InlineData("+39 338 123 4567")]
    [InlineData("06 1234 5678")]
    [InlineData("06-12345678")]
    [InlineData("0121 12345")]
    [InlineData("338 123 4567")]
    [InlineData("3381234567")]
    [InlineData("0039 02 12345678")]
    public void Scrub_PhoneNumber_ReplacedWithPlaceholder(string phone)
    {
        var result = PiiScrubber.Scrub($"Chiamaci al {phone} per un preventivo.");
        Assert.DoesNotContain(phone, result);
        Assert.Contains("[tel]", result);
    }

    [Theory]
    [InlineData("€ 3.00")]
    [InlineData("2026")]
    [InlineData("013075")]
    [InlineData("v1.2.3")]
    public void Scrub_NonPhoneNumericTokens_NotReplaced(string token)
    {
        var text = $"Riferimento: {token} nel comune.";
        Assert.Equal(text, PiiScrubber.Scrub(text));
    }

    [Fact]
    public void Scrub_MixedPii_AllTypesReplaced()
    {
        const string snippet =
            "Pulizie Veloci Srl — Via Roma 10, Milano. " +
            "Tel: +39 02 9876 5432 | Email: info@pulizieveloci.it | " +
            "Titolare CF: VRDLCU85M12F205Z";

        var result = PiiScrubber.Scrub(snippet);

        Assert.Contains("[tel]", result);
        Assert.Contains("[email]", result);
        Assert.Contains("[cf]", result);
        Assert.Contains("Pulizie Veloci Srl", result);
        Assert.Contains("Via Roma 10, Milano", result);
    }

    [Fact]
    public void Scrub_FiscalCodeBeforeEmail_BothReplaced()
    {
        // Verify ordering: CF regex runs before email, no interaction between them
        const string text = "CF: BNCGPP75D12F205R email: mario@example.com";
        var result = PiiScrubber.Scrub(text);
        Assert.Contains("[cf]", result);
        Assert.Contains("[email]", result);
        Assert.DoesNotContain("BNCGPP75D12F205R", result);
        Assert.DoesNotContain("mario@example.com", result);
    }

    private static int CountOccurrences(string text, string pattern)
    {
        var count = 0;
        var idx = 0;
        while ((idx = text.IndexOf(pattern, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += pattern.Length;
        }
        return count;
    }
}
