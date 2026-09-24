using System.Net;
using Casazen.Infrastructure.Http;
using Xunit;

namespace Casazen.Tests.Unit.Infrastructure.Http;

/// <summary>FD-16 (A2-21, A4-10, A9-32): which URLs and addresses the server may fetch for a user.</summary>
public class ExternalUrlPolicyTests
{
    private static readonly int[] Https = [443];

    [Theory]
    [InlineData("http://example.com/cal.ics")]
    [InlineData("ftp://example.com/cal.ics")]
    [InlineData("file:///etc/passwd")]
    [InlineData("https://127.0.0.1/cal.ics")]
    [InlineData("https://2130706433/cal.ics")] // 127.0.0.1 written as a number
    [InlineData("https://0x7f.1/cal.ics")] // 127.0.0.1 in hex shorthand
    [InlineData("https://10.0.0.1/cal.ics")]
    [InlineData("https://172.16.5.4/cal.ics")]
    [InlineData("https://192.168.1.1/cal.ics")]
    [InlineData("https://100.64.0.1/cal.ics")]
    [InlineData("https://169.254.169.254/latest/meta-data/")]
    [InlineData("https://0.0.0.0/cal.ics")]
    [InlineData("https://224.0.0.1/cal.ics")]
    [InlineData("https://[::1]/cal.ics")]
    [InlineData("https://[fd12:3456::1]/cal.ics")]
    [InlineData("https://[fe80::1]/cal.ics")]
    [InlineData("https://[::ffff:127.0.0.1]/cal.ics")]
    [InlineData("https://localhost/cal.ics")]
    [InlineData("https://calendar.localhost/cal.ics")]
    [InlineData("https://postgres.railway.internal/cal.ics")]
    [InlineData("https://printer.local/cal.ics")]
    [InlineData("https://intranet/cal.ics")]
    [InlineData("https://user:secret@example.com/cal.ics")]
    [InlineData("https://example.com:8443/cal.ics")]
    [InlineData("https://example.com:80/cal.ics")]
    [InlineData("/relative/cal.ics")]
    [InlineData("not a url")]
    [InlineData("")]
    [InlineData(null)]
    public void TryParse_NotAnExternalHttpsUrl_ReturnsFalse(string? url)
    {
        Assert.False(ExternalUrlPolicy.TryParse(url, Https, out _));
    }

    [Theory]
    [InlineData("https://www.airbnb.it/calendar/ical/12345.ics?s=abcdef")]
    [InlineData("https://admin.booking.com/hotel/hoteladmin/ical.html?t=token")]
    [InlineData("https://calendar.google.com/calendar/ical/x%40group.calendar.google.com/private-1/basic.ics")]
    [InlineData("https://example.com:443/cal.ics")]
    [InlineData("  https://example.com/cal.ics  ")]
    [InlineData("https://8.8.8.8/cal.ics")]
    [InlineData("https://[2606:4700:4700::1111]/cal.ics")]
    public void TryParse_PublicHttpsUrl_ReturnsTrue(string url)
    {
        Assert.True(ExternalUrlPolicy.TryParse(url, Https, out var uri));
        Assert.Equal(Uri.UriSchemeHttps, uri.Scheme);
    }

    [Fact]
    public void TryParse_PortInConfiguredList_ReturnsTrue()
    {
        Assert.True(ExternalUrlPolicy.TryParse("https://example.com:8443/cal.ics", [443, 8443], out _));
    }

    [Fact]
    public void TryParse_UrlLongerThanLimit_ReturnsFalse()
    {
        var url = "https://example.com/" + new string('a', ExternalUrlPolicy.MaxUrlLength);

        Assert.False(ExternalUrlPolicy.TryParse(url, Https, out _));
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("127.255.0.9")]
    [InlineData("10.20.30.40")]
    [InlineData("172.31.255.255")]
    [InlineData("192.168.0.1")]
    [InlineData("169.254.169.254")] // cloud metadata
    [InlineData("100.64.0.1")] // CGNAT
    [InlineData("100.127.255.254")]
    [InlineData("0.0.0.0")]
    [InlineData("224.0.0.251")] // multicast
    [InlineData("255.255.255.255")]
    [InlineData("198.18.0.1")]
    [InlineData("192.0.2.10")]
    [InlineData("::")]
    [InlineData("::1")]
    [InlineData("fd12::1")] // ULA (Railway private network)
    [InlineData("fc00::1")]
    [InlineData("fe80::1")] // link-local
    [InlineData("ff02::1")] // multicast
    [InlineData("::ffff:10.0.0.1")] // IPv4-mapped
    [InlineData("::ffff:8.8.8.8")] // IPv4-mapped, even of a public address
    [InlineData("64:ff9b::a9fe:a9fe")] // NAT64 of 169.254.169.254
    [InlineData("2002:7f00:1::1")] // 6to4 of 127.0.0.1
    [InlineData("2001:db8::1")] // documentation
    public void IsBlockedAddress_NonPublicAddress_ReturnsTrue(string address)
    {
        Assert.True(ExternalUrlPolicy.IsBlockedAddress(IPAddress.Parse(address)));
    }

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("100.63.255.255")] // just below CGNAT
    [InlineData("172.32.0.1")] // just above 172.16.0.0/12
    [InlineData("2606:4700:4700::1111")]
    [InlineData("2a00:1450:4002:80a::200e")]
    [InlineData("64:ff9b::808:808")] // NAT64 of 8.8.8.8
    public void IsBlockedAddress_PublicAddress_ReturnsFalse(string address)
    {
        Assert.False(ExternalUrlPolicy.IsBlockedAddress(IPAddress.Parse(address)));
    }
}
