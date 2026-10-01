using Casazen.Core.Exceptions;
using Casazen.Core.SiteDocuments;
using Xunit;

namespace Casazen.Tests.Unit.SiteDocuments;

/// <summary>
/// <see cref="OrgSiteDocumentRules"/> (BK-14, A3-21): what an operator may publish as privacy notice or terms, and how the
/// text becomes HTML. The text is never trusted: no raw HTML, links only http/https/mailto, everything encoded.
/// </summary>
public class OrgSiteDocumentRulesTests
{
    // ── Kind ────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("privacy", OrgSiteDocumentKind.Privacy)]
    [InlineData(" Terms ", OrgSiteDocumentKind.Terms)]
    public void TryParseKind_KnownRouteName_ReturnsTheKind(string value, OrgSiteDocumentKind expected)
    {
        Assert.True(OrgSiteDocumentRules.TryParseKind(value, out var kind));
        Assert.Equal(expected, kind);
        Assert.Equal(expected, ParseRoundTrip(expected));
    }

    [Theory]
    [InlineData("termini")]
    [InlineData("dpa")]
    [InlineData("")]
    [InlineData(null)]
    public void TryParseKind_UnknownRouteName_ReturnsFalse(string? value) =>
        Assert.False(OrgSiteDocumentRules.TryParseKind(value, out _));

    private static OrgSiteDocumentKind ParseRoundTrip(OrgSiteDocumentKind kind)
    {
        Assert.True(OrgSiteDocumentRules.TryParseKind(OrgSiteDocumentRules.RouteName(kind), out var parsed));
        return parsed;
    }

    // ── NormalizeContent ────────────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n\t ")]
    [InlineData("\u0001\u0002")]
    public void NormalizeContent_EmptyText_Throws(string? content)
    {
        var ex = Assert.Throws<DomainRuleException>(() => OrgSiteDocumentRules.NormalizeContent(content));

        Assert.Equal(OrgSiteDocumentRules.ContentRequiredCode, ex.Code);
    }

    [Fact]
    public void NormalizeContent_MessyText_UnifiesLineBreaksDropsControlCharactersAndCollapsesBlankLines()
    {
        var text = OrgSiteDocumentRules.NormalizeContent("\r\n  Titolare: Mario Rossi\r\rVia Roma 1\u0007\n\n\n\n\nFine  \u2028");

        Assert.Equal("Titolare: Mario Rossi\n\nVia Roma 1\n\nFine", text);
    }

    [Fact]
    public void NormalizeContent_ExactlyTheLimit_IsAccepted()
    {
        var text = OrgSiteDocumentRules.NormalizeContent(new string('a', OrgSiteDocumentRules.ContentMaxLength));

        Assert.Equal(OrgSiteDocumentRules.ContentMaxLength, text.Length);
    }

    [Fact]
    public void NormalizeContent_OverTheLimit_ThrowsWithTheLimit()
    {
        var ex = Assert.Throws<DomainRuleException>(() =>
            OrgSiteDocumentRules.NormalizeContent(new string('a', OrgSiteDocumentRules.ContentMaxLength + 1)));

        Assert.Equal(OrgSiteDocumentRules.ContentTooLongCode, ex.Code);
        Assert.Equal(OrgSiteDocumentRules.ContentMaxLength, Assert.Single(ex.MessageArgs));
    }

    [Theory]
    [InlineData("<script>alert(1)</script>")]
    [InlineData("Testo <b>grassetto</b>")]
    [InlineData("<img src=x onerror=alert(1)>")]
    [InlineData("<a href=\"https://x.test\">x</a>")]
    [InlineData("<br/>")]
    [InlineData("<!-- commento -->")]
    [InlineData("<?php echo 1; ?>")]
    [InlineData("<div\nonclick=\"x()\">")]
    public void NormalizeContent_RawHtml_Throws(string content)
    {
        var ex = Assert.Throws<DomainRuleException>(() => OrgSiteDocumentRules.NormalizeContent(content));

        Assert.Equal(OrgSiteDocumentRules.HtmlNotAllowedCode, ex.Code);
    }

    [Theory]
    [InlineData("Il prezzo è < 100 e > 50 euro")]
    [InlineData("Scrivici a <https://example.test/privacy> per info")]
    [InlineData("1 < 2")]
    public void NormalizeContent_AngleBracketsThatAreNotTags_AreKept(string content) =>
        Assert.Equal(content, OrgSiteDocumentRules.NormalizeContent(content));

    [Theory]
    [InlineData("[clic](javascript:alert(1))")]
    [InlineData("[clic](JaVaScRiPt:alert(1))")]
    [InlineData("[clic](data:text/html;base64,AAAA)")]
    [InlineData("[clic](vbscript:x)")]
    [InlineData("[clic](www.example.test)")]
    [InlineData("[clic](/relativo)")]
    [InlineData("[clic]()")]
    [InlineData("[clic](ftp://example.test/file)")]
    [InlineData("[clic](tel:+390212345678)")]
    public void NormalizeContent_LinkWithADisallowedTarget_Throws(string content)
    {
        var ex = Assert.Throws<DomainRuleException>(() => OrgSiteDocumentRules.NormalizeContent(content));

        Assert.Equal(OrgSiteDocumentRules.LinkInvalidCode, ex.Code);
    }

    [Theory]
    [InlineData("[sito](https://example.test/privacy)")]
    [InlineData("[sito](http://example.test)")]
    [InlineData("[scrivici](mailto:privacy@example.test)")]
    public void NormalizeContent_LinkWithAnAllowedTarget_IsKept(string content) =>
        Assert.Equal(content, OrgSiteDocumentRules.NormalizeContent(content));

    // ── NormalizeExternalUrl ────────────────────────────────────────────────────

    [Theory]
    [InlineData("https://example.test/privacy", "https://example.test/privacy")]
    [InlineData("  https://Example.TEST/Privacy?lang=it#parte-2 ", "https://example.test/Privacy?lang=it#parte-2")]
    [InlineData("https://example.test", "https://example.test/")]
    public void NormalizeExternalUrl_ValidHttpsAddress_ReturnsTheNormalizedAddress(string value, string expected) =>
        Assert.Equal(expected, OrgSiteDocumentRules.NormalizeExternalUrl(value));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NormalizeExternalUrl_Empty_ThrowsRequired(string? value)
    {
        var ex = Assert.Throws<DomainRuleException>(() => OrgSiteDocumentRules.NormalizeExternalUrl(value));

        Assert.Equal(OrgSiteDocumentRules.UrlRequiredCode, ex.Code);
    }

    [Theory]
    [InlineData("http://example.test/privacy")]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,<b>x</b>")]
    [InlineData("ftp://example.test/privacy.pdf")]
    [InlineData("//example.test/privacy")]
    [InlineData("example.test/privacy")]
    [InlineData("https://user:password@example.test/privacy")]
    [InlineData("https://localhost/privacy")]
    [InlineData("https://intranet/privacy")]
    [InlineData("https://example.test/a b")]
    [InlineData("https://example.test/\u0001")]
    [InlineData("not a url")]
    public void NormalizeExternalUrl_NotAnHttpsAddress_ThrowsInvalid(string value)
    {
        var ex = Assert.Throws<DomainRuleException>(() => OrgSiteDocumentRules.NormalizeExternalUrl(value));

        Assert.Equal(OrgSiteDocumentRules.UrlInvalidCode, ex.Code);
    }

    [Fact]
    public void NormalizeExternalUrl_TooLong_ThrowsInvalid()
    {
        var value = "https://example.test/" + new string('a', OrgSiteDocumentRules.ExternalUrlMaxLength);

        var ex = Assert.Throws<DomainRuleException>(() => OrgSiteDocumentRules.NormalizeExternalUrl(value));

        Assert.Equal(OrgSiteDocumentRules.UrlInvalidCode, ex.Code);
    }

    // ── ToHtml ──────────────────────────────────────────────────────────────────

    [Fact]
    public void ToHtml_ParagraphsAndLineBreaks_BecomeParagraphsAndBr()
    {
        var html = OrgSiteDocumentRules.ToHtml("Prima riga\nSeconda riga\n\nNuovo paragrafo");

        Assert.Equal("<p>Prima riga<br>Seconda riga</p><p>Nuovo paragrafo</p>", html);
    }

    [Fact]
    public void ToHtml_Headings_AreShiftedBelowThePageTitle()
    {
        var html = OrgSiteDocumentRules.ToHtml("# Titolare\n## Finalità\n### Dettaglio\n#### Troppo profondo");

        Assert.Equal("<h2>Titolare</h2><h3>Finalità</h3><h4>Dettaglio</h4><p>#### Troppo profondo</p>", html);
    }

    [Fact]
    public void ToHtml_BulletsOfBothMarks_BecomeOneListAndEndWithTheBlock()
    {
        var html = OrgSiteDocumentRules.ToHtml("Dati raccolti:\n- nome\n* email\n\nFine");

        Assert.Equal("<p>Dati raccolti:</p><ul><li>nome</li><li>email</li></ul><p>Fine</p>", html);
    }

    [Fact]
    public void ToHtml_NumberedClauses_KeepTheirNumbersAsText()
    {
        var html = OrgSiteDocumentRules.ToHtml("3. Titolare del trattamento\n4. Finalità");

        Assert.Equal("<p>3. Titolare del trattamento<br>4. Finalità</p>", html);
    }

    [Fact]
    public void ToHtml_EmphasisAndLinks_BecomeTags()
    {
        var html = OrgSiteDocumentRules.ToHtml("Scrivi a **privacy** o *info*: [sito](https://example.test/a?x=1&y=2) o [mail](mailto:p@example.test)");

        Assert.Equal(
            "<p>Scrivi a <strong>privacy</strong> o <em>info</em>: <a href=\"https://example.test/a?x=1&amp;y=2\">sito</a> o <a href=\"mailto:p@example.test\">mail</a></p>",
            html);
    }

    [Fact]
    public void ToHtml_UnderscoresAndAsterisksInsideAnAddress_AreNotEmphasis()
    {
        var html = OrgSiteDocumentRules.ToHtml("[doc](https://example.test/a_b_c/*x*/file_name_here)");

        Assert.Equal("<p><a href=\"https://example.test/a_b_c/*x*/file_name_here\">doc</a></p>", html);
    }

    [Fact]
    public void ToHtml_UnderscoresInPlainWords_AreLeftAlone()
    {
        Assert.Equal("<p>file_name_here e snake_case_word</p>", OrgSiteDocumentRules.ToHtml("file_name_here e snake_case_word"));
    }

    [Fact]
    public void ToHtml_HtmlThatBypassedValidation_IsEncodedNeverEmitted()
    {
        // NormalizeContent refuses this input; ToHtml must still be safe on its own.
        var html = OrgSiteDocumentRules.ToHtml("<script>alert('x')</script> & \"quote\" <img src=x onerror=alert(1)>");

        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<img", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("&lt;script&gt;", html);
        Assert.Contains("&amp;", html);
        Assert.Contains("&quot;quote&quot;", html);
    }

    [Fact]
    public void ToHtml_DisallowedLinkThatBypassedValidation_IsLeftAsText()
    {
        var html = OrgSiteDocumentRules.ToHtml("[x](javascript:alert(1))");

        Assert.DoesNotContain("<a", html);
        Assert.DoesNotContain("href", html);
    }

    [Fact]
    public void ToHtml_AttributeBreakingAddress_CannotInjectAnAttribute()
    {
        var html = OrgSiteDocumentRules.ToHtml("[x](https://example.test/\"onmouseover=\"alert(1))");

        Assert.DoesNotContain("onmouseover=\"", html);
    }
}
