using System.Text;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Regulatory;
using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// SE-01 (A8-06): only a real, well-formed answer of a configured AI provider becomes publishable SEO text; the stub,
/// a missing key, an empty or a "loose" answer (markdown, chat reply, one line) are explicit "not generated" states.
/// </summary>
public class SeoGeneratedContentTests
{
    /// <summary>An answer with the sections, paragraphs and length the checks require.</summary>
    public static string ValidHtml(string marker = "Como")
    {
        var html = new StringBuilder("<h2>CIN (Codice Identificativo Nazionale)</h2>");
        for (var i = 0; i < 6; i++)
            html.Append($"<p>{marker}: informazioni generali sugli adempimenti degli affitti brevi, paragrafo {i} della guida.</p>");
        html.Append("<h2>Fonti ufficiali</h2><ul><li><a href=\"https://alloggiatiweb.poliziadistato.it\">Alloggiati Web</a></li></ul>");
        return html.ToString();
    }

    private static AiGenerationResult Answer(string content, bool providerConfigured = true) =>
        new(content, 10, 10, AiModelTier.Economy, FromCache: false, providerConfigured);

    [Fact]
    public void Evaluate_ValidHtml_IsGeneratedAndSanitized()
    {
        var (status, body) = SeoGeneratedContent.Evaluate(Answer(ValidHtml() + "<script>alert(1)</script>"));

        Assert.Equal(SeoContentStatus.Generated, status);
        Assert.DoesNotContain("<script", body);
        Assert.Contains("rel=\"noopener noreferrer\"", body);
    }

    [Fact]
    public void Evaluate_HtmlInsideOneCodeFence_IsUnwrapped()
    {
        var (status, body) = SeoGeneratedContent.Evaluate(Answer("```html\n" + ValidHtml() + "\n```"));

        Assert.Equal(SeoContentStatus.Generated, status);
        Assert.StartsWith("<h2>", body);
    }

    [Fact]
    public void Evaluate_ProviderNotConfigured_IsNotGeneratedWithoutBody()
    {
        // The stub text would pass for a paragraph: the flag, not the content, decides.
        var (status, body) = SeoGeneratedContent.Evaluate(Answer(ValidHtml(), providerConfigured: false));

        Assert.Equal(SeoContentStatus.AiProviderNotConfigured, status);
        Assert.Equal(string.Empty, body);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("<article><p> </p></article>")]
    public void Evaluate_EmptyAnswer_IsEmptyOutput(string content)
    {
        var (status, body) = SeoGeneratedContent.Evaluate(Answer(content));

        Assert.Equal(SeoContentStatus.EmptyOutput, status);
        Assert.Equal(string.Empty, body);
    }

    [Theory]
    [InlineData("Sembra che tu abbia fornito dei dati su Como. Vuoi che scriva una guida?")]
    [InlineData("<p>Como: contenuto generato per affitti brevi, CIN e tassa di soggiorno.</p>")]
    public void Evaluate_ChatReplyOrOneLine_IsInvalidOutput(string content)
    {
        var (status, _) = SeoGeneratedContent.Evaluate(Answer(content));

        Assert.Equal(SeoContentStatus.InvalidOutput, status);
    }

    [Fact]
    public void Evaluate_MarkdownAnswer_IsInvalidOutputKeptForTheAdmin()
    {
        var markdown = "## CIN\n\n**Obbligatorio** per gli affitti brevi.\n\n" + ValidHtml();

        var (status, body) = SeoGeneratedContent.Evaluate(Answer(markdown));

        Assert.Equal(SeoContentStatus.InvalidOutput, status);
        Assert.NotEmpty(body);
    }

    [Fact]
    public void Evaluate_WithoutSections_IsInvalidOutput()
    {
        var paragraphs = string.Concat(Enumerable.Repeat("<p>Testo lungo sugli affitti brevi a Como senza alcuna sezione.</p>", 20));

        var (status, _) = SeoGeneratedContent.Evaluate(Answer(paragraphs));

        Assert.Equal(SeoContentStatus.InvalidOutput, status);
    }
}

/// <summary>SE-01 (A8-06): the SEO prompt has instructions and names the only facts and sources the text may use.</summary>
public class SeoContentPromptTests
{
    private static readonly ComuneInfo Como = ComuneTestData.ComoInfo;

    [Fact]
    public void Build_ComplianceGuide_HasLanguageFormatStructureAndSourceRules()
    {
        var prompt = SeoContentPrompt.Build(Como, SeoPageType.ComplianceGuide, []);

        Assert.Contains("italiano", prompt);
        Assert.Contains("frammento HTML", prompt);
        Assert.Contains("senza markdown", prompt);
        Assert.Contains("<h2>CIN (Codice Identificativo Nazionale)</h2>", prompt);
        Assert.Contains("<h2>Comunicazione degli ospiti alla Questura (Alloggiati Web)</h2>", prompt);
        Assert.Contains("<h2>Fonti ufficiali</h2>", prompt);
        Assert.Contains("cita e collega solo le FONTI", prompt);
        Assert.Contains("Non dare consulenza legale", prompt);
        Assert.Contains("https://alloggiatiweb.poliziadistato.it", prompt);
        Assert.Contains("CasaZen non ha una tariffa verificata per Como", prompt);
    }

    [Fact]
    public void Build_LombardyGuide_AddsTheRegionalSectionFactsAndOfficialSources()
    {
        var prompt = SeoContentPrompt.Build(Como, SeoPageType.ComplianceGuide, []);

        Assert.Contains("<h2>Regole della Lombardia: CIR e flussi turistici (Ross1000)</h2>", prompt);
        Assert.Contains("Il CIR è propedeutico al CIN", prompt);
        Assert.Contains("entro il giorno 5 del mese successivo", prompt);
        Assert.Contains("https://www.flussituristici.servizirl.it/", prompt);
        Assert.Contains("https://www.regione.lombardia.it/cultura-turismo-e-sport/imprese-e-professioni-turistiche/codice-identificativo-di-riferimento-cir", prompt);
        // Province of Como (ISTAT 013): its own page on Ross1000 and the CIR.
        Assert.Contains("https://www.provincia.como.it/", prompt);
        Assert.Contains("tra 400 e 750 parole", prompt);
        // The regional section comes after the Questura and before the tourist tax.
        Assert.True(
            prompt.IndexOf("Alloggiati Web)</h2>", StringComparison.Ordinal)
            < prompt.IndexOf("Regole della Lombardia", StringComparison.Ordinal));
        Assert.True(
            prompt.IndexOf("Regole della Lombardia", StringComparison.Ordinal)
            < prompt.IndexOf("<h2>Imposta di soggiorno a Como</h2>", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("015146", "Milano", "https://www.cittametropolitana.mi.it/")]
    [InlineData("097084", "Varenna", "https://www.provincia.lecco.it/")]
    public void Build_LombardyGuide_CitesThePageOfItsProvince(string code, string name, string provinceUrl)
    {
        var comune = new ComuneInfo(code, name, "LOM", "lombardia", name.ToLowerInvariant());

        var prompt = SeoContentPrompt.Build(comune, SeoPageType.ComplianceGuide, []);

        Assert.Contains(provinceUrl, prompt);
        Assert.DoesNotContain("https://www.provincia.como.it/", prompt);
    }

    [Fact]
    public void Build_LombardyGuideOfAProvinceWithoutAVerifiedPage_CitesTheRegionalSourcesOnly()
    {
        var varese = new ComuneInfo("012133", "Varese", "LOM", "lombardia", "varese");

        var prompt = SeoContentPrompt.Build(varese, SeoPageType.ComplianceGuide, []);

        Assert.Contains("Regole della Lombardia", prompt);
        Assert.Contains("https://www.flussituristici.servizirl.it/", prompt);
        Assert.DoesNotContain("provincia.como.it", prompt);
        Assert.DoesNotContain("cittametropolitana.mi.it", prompt);
        Assert.DoesNotContain("provincia.lecco.it", prompt);
    }

    [Fact]
    public void Build_LombardyTouristTaxPage_HasNoRegionalSection()
    {
        var prompt = SeoContentPrompt.Build(Como, SeoPageType.TouristTaxCalc, []);

        Assert.DoesNotContain("Lombardia", prompt);
        Assert.DoesNotContain("Ross1000", prompt);
        Assert.Contains("tra 300 e 600 parole", prompt);
    }

    [Fact]
    public void Build_GuideOutsideLombardy_HasNoRegionalSection()
    {
        var prompt = SeoContentPrompt.Build(ComuneTestData.PalermoInfo, SeoPageType.ComplianceGuide, []);

        Assert.DoesNotContain("Lombardia", prompt);
        Assert.DoesNotContain("CIR", prompt);
        Assert.Contains("tra 300 e 600 parole", prompt);
    }

    [Fact]
    public void VersionFor_OnlyTheLombardyGuides_HaveTheNewVersion()
    {
        // A page whose prompt did not change keeps its version: it is not regenerated, nor reviewed again, for nothing.
        Assert.Equal(SeoContentPrompt.LombardyGuideVersion, SeoContentPrompt.VersionFor(Como, SeoPageType.ComplianceGuide));
        Assert.Equal(SeoContentPrompt.Version, SeoContentPrompt.VersionFor(Como, SeoPageType.TouristTaxCalc));
        Assert.Equal(SeoContentPrompt.Version, SeoContentPrompt.VersionFor(ComuneTestData.PalermoInfo, SeoPageType.ComplianceGuide));
        Assert.NotEqual(SeoContentPrompt.Version, SeoContentPrompt.LombardyGuideVersion);
    }

    [Fact]
    public void Build_AllowedTags_AreExactlyTheSanitizerAllowlist()
    {
        var prompt = SeoContentPrompt.Build(Como, SeoPageType.ComplianceGuide, []);

        // FD-15 allowlist (SeoHtmlSanitizer / src/lib/sanitize-html.ts): the prompt never asks for a tag that is stripped.
        foreach (var tag in new[] { "h2", "h3", "p", "ul", "ol", "li", "strong", "em", "table", "thead", "tbody", "tr", "th", "td", "br" })
            Assert.Contains($"<{tag}>", prompt);
        Assert.Contains("<a href=", prompt);
        Assert.Contains("niente <h1>", prompt);
    }

    [Fact]
    public void Build_TouristTaxWithRate_ListsTheRateAndOnlyItsHttpsSource()
    {
        var rate = new TouristTaxRate
        {
            City = "Como",
            RatePerPersonPerNight = 3m,
            MaxNights = 4,
            MinimumAge = 14,
            EffectiveFrom = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            SourceUrl = "https://www.comune.como.it/imposta-di-soggiorno",
        };
        var withoutHttps = new TouristTaxRate { City = "Como", RatePerPersonPerNight = 2m, SourceUrl = "http://example.test/rate" };

        var prompt = SeoContentPrompt.Build(Como, SeoPageType.TouristTaxCalc, [rate, withoutHttps]);

        Assert.Contains("€ 3,00 a persona per notte", prompt);
        Assert.Contains("si paga per le prime 4 notti", prompt);
        Assert.Contains("esenti gli ospiti sotto i 14 anni", prompt);
        Assert.Contains("https://www.comune.como.it/imposta-di-soggiorno", prompt);
        Assert.DoesNotContain("http://example.test/rate", prompt);
        Assert.DoesNotContain("normattiva", prompt);
    }

    [Fact]
    public void EvaluateManual_PlainText_BecomesPublishableHtml()
    {
        var paragraph = new string('a', 320);
        var body = SeoGeneratedContent.EvaluateManual($"{paragraph}\n\n{paragraph}", "Guida di Como");

        Assert.Contains("<h2>Guida di Como</h2>", body);
        Assert.Contains("<p>", body);
        Assert.True(SeoGeneratedContent.VisibleText(body).Length >= SeoGeneratedContent.MinimumTextLength);
    }

    [Fact]
    public void EvaluateManual_ShortText_IsRefused()
    {
        var error = Assert.Throws<Casazen.Core.Exceptions.DomainRuleException>(() =>
            SeoGeneratedContent.EvaluateManual("<h2>CIN</h2><p>Troppo corto.</p>", "Como"));

        Assert.Equal(SeoReviewErrorCodes.DraftInvalid, error.Code);
    }
}
