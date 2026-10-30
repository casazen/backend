using Casazen.Core.SiteProfile;
using Xunit;

namespace Casazen.Tests.Unit.SiteProfile;

/// <summary>
/// DB-11a, DB-D17: a video or a tour of a house is only an address, and the page embeds it. Whatever form the host pastes, what is stored
/// is the canonical embed address of one of three providers built from the identifier alone, so no stored value can point anywhere else,
/// carry a query that autoplays, or smuggle credentials; a lookalike host, a plain <c>http</c> address and a tour given as a video are refused.
/// </summary>
public class SiteEmbedUrlsTests
{
    [Theory]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ", "https://www.youtube-nocookie.com/embed/dQw4w9WgXcQ")]
    [InlineData("https://youtube.com/watch?v=dQw4w9WgXcQ&t=30s", "https://www.youtube-nocookie.com/embed/dQw4w9WgXcQ")]
    [InlineData("https://m.youtube.com/watch?feature=share&v=dQw4w9WgXcQ", "https://www.youtube-nocookie.com/embed/dQw4w9WgXcQ")]
    [InlineData("https://youtu.be/dQw4w9WgXcQ?si=abc", "https://www.youtube-nocookie.com/embed/dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/embed/dQw4w9WgXcQ?autoplay=1", "https://www.youtube-nocookie.com/embed/dQw4w9WgXcQ")]
    [InlineData("https://www.youtube-nocookie.com/embed/dQw4w9WgXcQ", "https://www.youtube-nocookie.com/embed/dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/shorts/dQw4w9WgXcQ", "https://www.youtube-nocookie.com/embed/dQw4w9WgXcQ")]
    [InlineData("  https://youtu.be/dQw4w9WgXcQ  ", "https://www.youtube-nocookie.com/embed/dQw4w9WgXcQ")]
    [InlineData("https://vimeo.com/123456789", "https://player.vimeo.com/video/123456789")]
    [InlineData("https://vimeo.com/123456789/abcdef0123", "https://player.vimeo.com/video/123456789?h=abcdef0123")]
    [InlineData("https://player.vimeo.com/video/123456789?h=abcdef0123&autoplay=1", "https://player.vimeo.com/video/123456789?h=abcdef0123")]
    public void NormalizeVideo_AFormTheProviderGives_BecomesTheCanonicalEmbed(string pasted, string expected)
    {
        var embed = SiteEmbedUrls.NormalizeVideo(pasted);

        Assert.NotNull(embed);
        Assert.Equal(expected, embed.Url);
        Assert.Equal(expected.Contains("vimeo", StringComparison.Ordinal) ? SiteEmbedProvider.Vimeo : SiteEmbedProvider.YouTube, embed.Provider);
    }

    [Theory]
    [InlineData("http://www.youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com.evil.example/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://evil.example/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://evil.example/https://www.youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://user:secret@www.youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com:8443/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/watch?v=short")]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ%22onload%3Dalert(1)")]
    [InlineData("https://www.youtube.com/playlist?list=PL123456789")]
    [InlineData("https://vimeo.com/channels/staffpicks")]
    [InlineData("https://vimeo.com/123456789/not-a-hash")]
    [InlineData("https://my.matterport.com/show/?m=SxQL3iGyoDo")]
    [InlineData("javascript:alert(1)")]
    [InlineData("//www.youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("dQw4w9WgXcQ")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void NormalizeVideo_AnythingElse_IsRefused(string? pasted) =>
        Assert.Null(SiteEmbedUrls.NormalizeVideo(pasted));

    [Theory]
    [InlineData("https://my.matterport.com/show/?m=SxQL3iGyoDo")]
    [InlineData("https://my.matterport.com/show?m=SxQL3iGyoDo&play=1&qs=1")]
    [InlineData("https://matterport.com/discover/space/SxQL3iGyoDo")]
    public void NormalizeTour_AMatterportAddress_BecomesTheCanonicalEmbed(string pasted)
    {
        var embed = SiteEmbedUrls.NormalizeTour(pasted);

        Assert.NotNull(embed);
        Assert.Equal("https://my.matterport.com/show/?m=SxQL3iGyoDo", embed.Url);
        Assert.Equal(SiteEmbedProvider.Matterport, embed.Provider);
    }

    [Theory]
    [InlineData("http://my.matterport.com/show/?m=SxQL3iGyoDo")]
    [InlineData("https://my.matterport.com.evil.example/show/?m=SxQL3iGyoDo")]
    [InlineData("https://my.matterport.com/show/?m=SxQL3iGyoDo%22x")]
    [InlineData("https://my.matterport.com/show/?m=short")]
    [InlineData("https://my.matterport.com/show/")]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("")]
    [InlineData(null)]
    public void NormalizeTour_AnythingElse_IsRefused(string? pasted) =>
        Assert.Null(SiteEmbedUrls.NormalizeTour(pasted));

    [Fact]
    public void Recognize_OnlyAnAddressOfOurOwnForm_IsPassedOn()
    {
        Assert.Equal(SiteEmbedProvider.Matterport, SiteEmbedUrls.Recognize("https://my.matterport.com/show/?m=SxQL3iGyoDo")?.Provider);
        Assert.Equal(SiteEmbedProvider.YouTube, SiteEmbedUrls.Recognize("https://www.youtube-nocookie.com/embed/dQw4w9WgXcQ")?.Provider);
        Assert.Null(SiteEmbedUrls.Recognize("https://evil.example/embed/dQw4w9WgXcQ"));
    }
}
