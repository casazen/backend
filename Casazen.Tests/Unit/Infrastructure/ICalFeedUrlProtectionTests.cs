using Casazen.Infrastructure.Data.Encryption;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.DataProtection;
using Xunit;

namespace Casazen.Tests.Unit.Infrastructure;

/// <summary>PC-11 (A2-20): how an iCal import URL is stored (encrypted) and shown (masked).</summary>
public class ICalFeedUrlProtectionTests
{
    private const string Url = "https://www.airbnb.it/calendar/ical/4815162342.ics?s=0a1b2c3d4e5f60718293a4b5c6d7e8f9";

    [Theory]
    [InlineData(Url, "www.airbnb.it/…e8f9")]
    [InlineData("https://admin.booking.com/hotel/hoteladmin/ical.html?t=5e6f7a8b-9c0d", "admin.booking.com/…9c0d")]
    [InlineData("https://calendar.google.com/calendar/ical/abc%40group.calendar.google.com/private-xyz/basic.ics", "calendar.google.com/….ics")]
    [InlineData("https://example.com/c.ics", "example.com/…")]
    [InlineData("https://example.com/", "example.com/…")]
    public void Mask_ImportUrl_ShowsOnlyHostAndLastCharacters(string url, string expected)
    {
        var masked = ICalFeedUrlMask.Mask(url);

        Assert.Equal(expected, masked);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a url")]
    public void Mask_NoUsableUrl_ReturnsNull(string? url) => Assert.Null(ICalFeedUrlMask.Mask(url));

    [Fact]
    public void Mask_AirbnbUrl_NeverContainsTheToken() =>
        Assert.DoesNotContain("0a1b2c3d4e5f6071", ICalFeedUrlMask.Mask(Url));

    [Fact]
    public void Converter_ImportUrl_IsStoredAsProtectedPayloadAndReadBack()
    {
        var converter = NewConverter();

        var stored = (string)converter.ConvertToProvider(Url)!;

        Assert.StartsWith("CfDJ8", stored);
        Assert.DoesNotContain("airbnb", stored, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(Url, converter.ConvertFromProvider(stored));
    }

    // A URL saved in clear before PC-11 stays readable until the startup step encrypts it.
    [Theory]
    [InlineData(Url)]
    [InlineData("HTTP://legacy.example.com/cal.ics")]
    public void Converter_LegacyUrlInClear_IsReadAsItIs(string legacy) =>
        Assert.Equal(legacy, NewConverter().ConvertFromProvider(legacy));

    [Fact]
    public void Converter_OtherTextThatIsNotAPayload_IsNotAcceptedAsClearText() =>
        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(
            () => NewConverter().ConvertFromProvider("tampered-value"));

    private static EncryptedStringConverter NewConverter() =>
        new(new EphemeralDataProtectionProvider(), PropertyICalFeedUrlEncryption.Purpose, PropertyICalFeedUrlEncryption.IsLegacyPlaintext);
}
