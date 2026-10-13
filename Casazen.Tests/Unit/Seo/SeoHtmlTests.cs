using Casazen.Web.Seo;
using Xunit;

namespace Casazen.Tests.Unit.Seo;

/// <summary>Escaping and text helpers of the crawler pages (BK-15): what a host writes never becomes markup.</summary>
public class SeoHtmlTests
{
    [Fact]
    public void Encode_MarkupAndQuotes_AreEscaped()
    {
        var encoded = SeoHtml.Encode("<script>alert(\"x\")</script> & 'y'");

        Assert.DoesNotContain("<", encoded);
        Assert.DoesNotContain("\"", encoded);
        Assert.Contains("&lt;script&gt;", encoded);
        Assert.Contains("&amp;", encoded);
    }

    [Theory]
    [InlineData("https://cdn.example.test/photo.jpg", "https://cdn.example.test/photo.jpg")]
    [InlineData("  https://cdn.example.test/a b.jpg ", "https://cdn.example.test/a%20b.jpg")]
    [InlineData("http://cdn.example.test/photo.jpg", null)]
    [InlineData("javascript:alert(1)", null)]
    [InlineData("data:image/png;base64,AAAA", null)]
    [InlineData("/uploads/photo.jpg", null)]
    [InlineData("https://user:pw@cdn.example.test/photo.jpg", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void TryHttpsUrl_OnlyAbsoluteHttpsWithoutCredentials_IsKept(string? value, string? expected)
    {
        Assert.Equal(expected, SeoHtml.TryHttpsUrl(value));
    }

    [Fact]
    public void Collapse_WhiteSpaceAndControlCharacters_BecomeSingleSpaces()
    {
        Assert.Equal("una casa sul lago", SeoHtml.Collapse("  una \n\n casa\tsul\u0007 lago  "));
    }

    [Fact]
    public void Truncate_LongText_CutsAtAWordBoundaryWithinTheLimit()
    {
        var text = string.Join(' ', Enumerable.Repeat("parola", 60));

        var truncated = SeoHtml.Truncate(text);

        Assert.True(truncated.Length <= SeoHtml.DescriptionMaxLength);
        Assert.EndsWith("parola…", truncated);
    }

    [Fact]
    public void Truncate_ShortText_IsReturnedAsOneLineWithoutEllipsis()
    {
        Assert.Equal("Breve testo.", SeoHtml.Truncate("Breve\ntesto."));
    }

    [Fact]
    public void Paragraphs_BlankLinesSplitParagraphs_AndEveryParagraphIsEscaped()
    {
        var html = SeoHtml.Paragraphs("Prima <b>riga</b>\n\nSeconda & ultima");

        Assert.Equal("<p>Prima &lt;b&gt;riga&lt;/b&gt;</p>\n<p>Seconda &amp; ultima</p>\n", html);
    }

    [Fact]
    public void Paragraphs_EmptyText_IsEmpty()
    {
        Assert.Equal(string.Empty, SeoHtml.Paragraphs("  \n\n "));
    }
}
