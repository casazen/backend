using Casazen.Core.Identity;
using Casazen.Infrastructure.Identity;
using Xunit;

namespace Casazen.Tests.Unit.Identity;

/// <summary>
/// E70/F02/US01/T01 (NF-188): the family of the browser of a request, from its User-Agent, is one of the six codes of <see cref="IdentityChannels"/>; every browser of
/// iOS runs on WebKit and is iOS Safari, Android counts Chrome only, the desktop splits Chromium, Safari and Firefox, and whatever is not recognised with certainty
/// (no header, a WebView, an automated browser, a bot) is <c>web:other</c>.
/// </summary>
public class IdentityChannelClassifierTests
{
    [Theory]
    [InlineData("Mozilla/5.0 (iPhone; CPU iPhone OS 17_4 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.4 Mobile/15E148 Safari/604.1", IdentityChannels.IosSafari)]
    [InlineData("Mozilla/5.0 (iPhone; CPU iPhone OS 17_4 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) CriOS/122.0.6261.89 Mobile/15E148 Safari/604.1", IdentityChannels.IosSafari)]
    [InlineData("Mozilla/5.0 (iPhone; CPU iPhone OS 17_4 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) FxiOS/123.0 Mobile/15E148 Safari/605.1.15", IdentityChannels.IosSafari)]
    [InlineData("Mozilla/5.0 (iPad; CPU OS 16_6 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/16.6 Mobile/15E148 Safari/604.1", IdentityChannels.IosSafari)]
    [InlineData("Mozilla/5.0 (Linux; Android 14; Pixel 8) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.6261.105 Mobile Safari/537.36", IdentityChannels.AndroidChrome)]
    [InlineData("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36", IdentityChannels.DesktopChromium)]
    [InlineData("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36 Edg/122.0.2365.66", IdentityChannels.DesktopChromium)]
    [InlineData("Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36 OPR/108.0.0.0", IdentityChannels.DesktopChromium)]
    [InlineData("Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36", IdentityChannels.DesktopChromium)]
    [InlineData("Mozilla/5.0 (X11; CrOS x86_64 14541.0.0) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36", IdentityChannels.DesktopChromium)]
    [InlineData("Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.3.1 Safari/605.1.15", IdentityChannels.DesktopSafari)]
    [InlineData("Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:123.0) Gecko/20100101 Firefox/123.0", IdentityChannels.DesktopFirefox)]
    [InlineData("Mozilla/5.0 (Macintosh; Intel Mac OS X 14.3; rv:123.0) Gecko/20100101 Firefox/123.0", IdentityChannels.DesktopFirefox)]
    [InlineData("Mozilla/5.0 (X11; Ubuntu; Linux x86_64; rv:123.0) Gecko/20100101 Firefox/123.0", IdentityChannels.DesktopFirefox)]
    public void Classify_TheBrowsersOfTheChannels_AreTheirFamily(string userAgent, string expected)
    {
        Assert.Equal(expected, IdentityChannelClassifier.Classify(userAgent));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("curl/8.4.0")]
    [InlineData("Mozilla/5.0 (compatible; Googlebot/2.1; +http://www.google.com/bot.html)")]
    // The browsers of Android that are not Chrome: Samsung Internet, Firefox, Edge, a WebView of an application.
    [InlineData("Mozilla/5.0 (Linux; Android 14; SM-S918B) AppleWebKit/537.36 (KHTML, like Gecko) SamsungBrowser/24.0 Chrome/117.0.0.0 Mobile Safari/537.36")]
    [InlineData("Mozilla/5.0 (Android 14; Mobile; rv:123.0) Gecko/123.0 Firefox/123.0")]
    [InlineData("Mozilla/5.0 (Linux; Android 14; Pixel 8) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Mobile Safari/537.36 EdgA/122.0.2365.86")]
    [InlineData("Mozilla/5.0 (Linux; Android 13; SM-A536B Build/TP1A.220624.014; wv) AppleWebKit/537.36 (KHTML, like Gecko) Version/4.0 Chrome/121.0.6167.178 Mobile Safari/537.36")]
    // An automated browser, the old Edge, an application built on Electron, a television.
    [InlineData("Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) HeadlessChrome/122.0.6261.57 Safari/537.36")]
    [InlineData("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/70.0.3538.102 Safari/537.36 Edge/18.19582")]
    [InlineData("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Slack/4.36.140 Chrome/120.0.6099.56 Electron/28.0.0 Safari/537.36")]
    [InlineData("Mozilla/5.0 (SMART-TV; Linux; Tizen 6.0) AppleWebKit/538.1 (KHTML, like Gecko) Version/6.0 TV Safari/538.1")]
    // A header with the words of iOS but not its engine.
    [InlineData("iPhone checker 1.0")]
    public void Classify_WhatIsNotRecognisedWithCertainty_IsOther(string? userAgent)
    {
        Assert.Equal(IdentityChannels.Other, IdentityChannelClassifier.Classify(userAgent));
    }

    [Fact]
    public void Classify_AnIPadThatAsksForTheDesktopSite_IsDesktopSafari_TheHeaderCannotTellThemApart()
    {
        const string iPadDesktopMode = "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.4 Safari/605.1.15";

        Assert.Equal(IdentityChannels.DesktopSafari, IdentityChannelClassifier.Classify(iPadDesktopMode));
    }

    [Fact]
    public void Classify_AVeryLongHeader_IsReadUpToTheLimit_AndTheAnswerIsAlwaysACodeOfTheChannels()
    {
        // The tokens past the limit are not read: a header padded to hide them is not recognised by them.
        var padded = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) " + new string('x', IdentityChannelClassifier.MaxReadLength) + " Chrome/122.0.0.0";
        Assert.Equal(IdentityChannels.Other, IdentityChannelClassifier.Classify(padded));

        foreach (var header in new[] { "Mozilla/5.0", "Android", "Windows NT", "Macintosh Safari/1", "X11 Firefox/1 Chrome/1", new string('a', 5000) })
            Assert.Contains(IdentityChannelClassifier.Classify(header), IdentityChannels.All);
    }

    [Fact]
    public void Classify_TheCaseOfTheTokens_DoesNotMatter()
    {
        Assert.Equal(
            IdentityChannels.AndroidChrome,
            IdentityChannelClassifier.Classify("MOZILLA/5.0 (LINUX; ANDROID 14; PIXEL 8) APPLEWEBKIT/537.36 (KHTML, LIKE GECKO) CHROME/122.0.0.0 MOBILE SAFARI/537.36"));
    }
}
