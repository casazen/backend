using System.Globalization;
using System.Text.RegularExpressions;
using Casazen.Infrastructure.Email.Templates;
using Casazen.Infrastructure.Services;
using Xunit;

namespace Casazen.Tests.Unit.Email;

/// <summary>
/// E05/F02/US02/T01: the email a host sends to the guest of a booking of a channel with no chat. It is in Italian and in English, every dynamic value is
/// HTML-encoded (the text of the host included, with its line breaks kept), the answer goes to the organization, and the language follows the guest's country.
/// </summary>
public class HostGuestMessageEmailTemplateTests
{
    private const string Payload = "<a href=\"https://phish.example\">Paga qui</a>";
    private const string EncodedPayload = "&lt;a href=&quot;https://phish.example&quot;&gt;Paga qui&lt;/a&gt;";

    private static readonly DateTime CheckIn = new(2026, 11, 6, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime CheckOut = new(2026, 11, 9, 0, 0, 0, DateTimeKind.Utc);

    public static TheoryData<string> Cultures => new() { "it-IT", "en" };

    [Theory]
    [MemberData(nameof(Cultures))]
    public void EveryDynamicValue_IsHtmlEncoded_TheTextOfTheHostIncluded(string culture)
    {
        var content = EmailTemplates.HostGuestMessage(CultureInfo.GetCultureInfo(culture), Payload, Payload, CheckIn, CheckOut, Payload, "info@casa.example");

        Assert.DoesNotContain("<a href=\"https://phish.example\"", content.HtmlBody, StringComparison.Ordinal);
        Assert.Contains(EncodedPayload, content.HtmlBody, StringComparison.Ordinal);
        // The name of the host, the name of the property and the text of the host each go through the builder.
        Assert.True(Regex.Matches(content.HtmlBody, Regex.Escape(EncodedPayload)).Count >= 3);
    }

    [Theory]
    [MemberData(nameof(Cultures))]
    public void TheEmail_IsACompleteDocumentInEachLanguage_WithNoPlaceholderLeft(string culture)
    {
        var content = EmailTemplates.HostGuestMessage(CultureInfo.GetCultureInfo(culture), "Villa Rosa", "Trullo Bianco", CheckIn, CheckOut, "Buongiorno", "info@casa.example");

        Assert.StartsWith("<!DOCTYPE html>", content.HtmlBody, StringComparison.Ordinal);
        Assert.Contains($"<html lang=\"{CultureInfo.GetCultureInfo(culture).TwoLetterISOLanguageName}\">", content.HtmlBody, StringComparison.Ordinal);
        Assert.DoesNotMatch(new Regex(@"\{\d+\}"), content.Subject + content.HtmlBody);
        Assert.Contains("Villa Rosa", content.Subject, StringComparison.Ordinal);
        Assert.DoesNotContain("Trullo", content.Subject, StringComparison.Ordinal);
        Assert.Contains("Trullo Bianco", content.HtmlBody, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSubjectAndTheBody_DifferBetweenTheTwoLanguages()
    {
        var italian = EmailTemplates.HostGuestMessage(CultureInfo.GetCultureInfo("it-IT"), "Villa Rosa", "Trullo", CheckIn, CheckOut, "Ciao", null);
        var english = EmailTemplates.HostGuestMessage(CultureInfo.GetCultureInfo("en"), "Villa Rosa", "Trullo", CheckIn, CheckOut, "Ciao", null);

        Assert.NotEqual(italian.Subject, english.Subject);
        Assert.Contains("per il tuo soggiorno", italian.Subject, StringComparison.Ordinal);
        Assert.Contains("about your stay", english.Subject, StringComparison.Ordinal);
    }

    [Fact]
    public void TheLineBreaksOfTheHost_AreKept_AsLineBreaksOfTheEmail()
    {
        var content = EmailTemplates.HostGuestMessage(
            CultureInfo.GetCultureInfo("en"), "Villa Rosa", "Trullo", CheckIn, CheckOut, "Prima riga\r\nSeconda riga", null);

        Assert.Contains("Prima riga<br />Seconda riga", content.HtmlBody, StringComparison.Ordinal);
    }

    [Fact]
    public void TheAnswer_GoesToTheOrganization_AndWithoutItTheEmailSaysToContactTheProperty()
    {
        var withReply = EmailTemplates.HostGuestMessage(CultureInfo.GetCultureInfo("en"), "Villa Rosa", "Trullo", CheckIn, CheckOut, "Ciao", " info@casa.example ");
        var withoutReply = EmailTemplates.HostGuestMessage(CultureInfo.GetCultureInfo("en"), "Villa Rosa", "Trullo", CheckIn, CheckOut, "Ciao", "  ");

        Assert.Equal("info@casa.example", withReply.ReplyTo);
        Assert.Contains("reply to this email", withReply.HtmlBody, StringComparison.Ordinal);
        Assert.DoesNotContain("info@casa.example", withReply.HtmlBody, StringComparison.Ordinal);
        Assert.Null(withoutReply.ReplyTo);
        Assert.Contains("contact the property directly", withoutReply.HtmlBody, StringComparison.Ordinal);
    }

    [Fact]
    public void TheName_IsInTheRegistryOfTheTemplateNames()
    {
        Assert.Equal("host-guest-message", EmailTemplates.Names.HostGuestMessage);
    }

    [Theory]
    [InlineData(null, "it")]
    [InlineData("", "it")]
    [InlineData("IT", "it")]
    [InlineData("ita", "it")]
    [InlineData(" Italia ", "it")]
    [InlineData("Italy", "it")]
    [InlineData("JP", "en")]
    [InlineData("Germany", "en")]
    public void TheLanguage_IsItalianForAGuestOfItalyOrOfNoKnownCountry_EnglishForEveryoneElse(string? country, string expected)
    {
        Assert.Equal(expected, MessageReplyService.EmailCultureOf(country).TwoLetterISOLanguageName);
    }
}
