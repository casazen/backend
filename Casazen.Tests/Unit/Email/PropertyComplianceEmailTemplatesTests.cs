using System.Globalization;
using System.Text.RegularExpressions;
using Casazen.Infrastructure.Email.Templates;
using Xunit;

namespace Casazen.Tests.Unit.Email;

/// <summary>CO-06: suspension email of a property to the host, IT/EN, every dynamic value HTML-encoded (FD-13).</summary>
public class PropertyComplianceEmailTemplatesTests
{
    private const string ActivationUrl = "https://casazen-app.test/app/short-rent/properties/1/activation";

    [Fact]
    public void PropertyComplianceSuspended_PropertyNameWithMarkup_IsHtmlEncoded()
    {
        var content = EmailTemplates.PropertyComplianceSuspended(
            EmailTemplates.DefaultCulture, "<a href=\"https://phish.example\">Villa</a>", ["cin"], ActivationUrl);

        Assert.DoesNotContain("<a href=\"https://phish.example\"", content.HtmlBody);
        Assert.Contains("&lt;a href=&quot;https://phish.example&quot;&gt;Villa&lt;/a&gt;", content.HtmlBody);
    }

    [Fact]
    public void PropertyComplianceSuspended_Italian_ListsOnlyTheMissingStepsKeepsBookingsAndLinksTheWizard()
    {
        var content = EmailTemplates.PropertyComplianceSuspended(
            EmailTemplates.DefaultCulture, "Villa Rosa", ["cin", "safety"], ActivationUrl);

        Assert.Equal("Annuncio sospeso - Villa Rosa", content.Subject);
        Assert.Contains("L'annuncio di <strong>Villa Rosa</strong> è stato sospeso", content.HtmlBody);
        Assert.Contains("codice identificativo nazionale (CIN) mancante o non valido", content.HtmlBody);
        Assert.Contains("checklist di sicurezza (D.L. 145/2023) incompleta o non confermata", content.HtmlBody);
        Assert.DoesNotContain("documenti obbligatori mancanti", content.HtmlBody);
        Assert.DoesNotContain("dati di base incompleti", content.HtmlBody);
        Assert.Contains("nessuna prenotazione è stata cancellata", content.HtmlBody);
        Assert.Contains($"href=\"{ActivationUrl}\"", content.HtmlBody);
        Assert.Contains("Apri l'attivazione", content.HtmlBody);
    }

    [Fact]
    public void PropertyComplianceSuspended_ItalianAndEnglish_CompleteDocumentsWithoutPlaceholders()
    {
        string[] allSteps = ["base-data", "cin", "documents", "safety"];
        var italian = EmailTemplates.PropertyComplianceSuspended(EmailTemplates.DefaultCulture, "Villa Rosa", allSteps, ActivationUrl);
        var english = EmailTemplates.PropertyComplianceSuspended(CultureInfo.GetCultureInfo("en"), "Villa Rosa", allSteps, ActivationUrl);

        Assert.Equal("Listing suspended - Villa Rosa", english.Subject);
        Assert.Contains("<html lang=\"en\">", english.HtmlBody);
        Assert.Contains("Bookings already confirmed remain valid", english.HtmlBody);
        Assert.Contains("national identification code (CIN) missing or not valid", english.HtmlBody);
        Assert.Equal(4, Regex.Matches(italian.HtmlBody, "<li>").Count);
        Assert.Equal(4, Regex.Matches(english.HtmlBody, "<li>").Count);
        foreach (var content in new[] { italian, english })
            Assert.DoesNotMatch(new Regex(@"\{\d+\}"), content.Subject + content.HtmlBody);
    }

    [Fact]
    public void PropertyComplianceSuspended_WithoutLink_HasNoButton()
    {
        var content = EmailTemplates.PropertyComplianceSuspended(EmailTemplates.DefaultCulture, "Villa Rosa", ["documents"]);

        Assert.DoesNotContain("<a href", content.HtmlBody);
        Assert.Contains("documenti obbligatori mancanti", content.HtmlBody);
    }
}
