using System.Net;
using System.Net.Http.Headers;
using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Microsoft.Extensions.Logging;
using RichardSzalay.MockHttp;
using Xunit;

namespace Casazen.Tests.Unit.Messages;

/// <summary>
/// E05/F02/US01/T01 (ADR-032-R5): the attachment proxy treats the address as untrusted input (it was read from a channel): only an https address of
/// a public host is fetched, a redirect is checked like the first address, and what comes back must be small enough and of one of three types.
/// </summary>
public class OtaAttachmentRulesTests
{
    [Theory]
    [InlineData("https://files.example.com/a/photo.jpg")]
    [InlineData("https://files.example.com/a/photo.jpg?token=abc&sig=def")]
    [InlineData("https://CDN.Example.COM./x.png")]
    [InlineData("https://bücher.example/x.png")]
    public void TryGetFetchUri_AcceptsAnHttpsAddressOfAHostName(string address)
    {
        Assert.True(OtaAttachmentRules.TryGetFetchUri(address, out var uri));
        Assert.Equal(Uri.UriSchemeHttps, uri.Scheme);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("photo.jpg")]
    [InlineData("/attachments/photo.jpg")]
    [InlineData("http://files.example.com/photo.jpg")]
    [InlineData("ftp://files.example.com/photo.jpg")]
    [InlineData("file:///etc/passwd")]
    [InlineData("https://127.0.0.1/photo.jpg")]
    [InlineData("https://10.0.0.5/photo.jpg")]
    [InlineData("https://169.254.169.254/latest/meta-data/")]
    [InlineData("https://[::1]/photo.jpg")]
    [InlineData("https://2130706433/photo.jpg")]
    [InlineData("https://localhost/photo.jpg")]
    [InlineData("https://api.localhost/photo.jpg")]
    [InlineData("https://intranet/photo.jpg")]
    [InlineData("https://db.internal/photo.jpg")]
    [InlineData("https://printer.local/photo.jpg")]
    [InlineData("https://user:password@files.example.com/photo.jpg")]
    [InlineData("https://files.example.com:8443/photo.jpg")]
    public void TryGetFetchUri_RefusesEverythingElse(string? address)
    {
        Assert.False(OtaAttachmentRules.TryGetFetchUri(address, out _));
    }

    [Fact]
    public void TryGetFetchUri_RefusesAnAddressLongerThanTheColumn()
    {
        Assert.False(OtaAttachmentRules.TryGetFetchUri("https://files.example.com/" + new string('a', 2100), out _));
    }

    [Theory]
    [InlineData("8.8.8.8", true)]
    [InlineData("93.184.216.34", true)]
    [InlineData("2606:4700:4700::1111", true)]
    [InlineData("127.0.0.1", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("10.1.2.3", false)]
    [InlineData("172.16.0.1", false)]
    [InlineData("172.31.255.255", false)]
    [InlineData("172.32.0.1", true)]
    [InlineData("192.168.1.1", false)]
    [InlineData("169.254.169.254", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("198.18.0.1", false)]
    [InlineData("224.0.0.1", false)]
    [InlineData("255.255.255.255", false)]
    [InlineData("::1", false)]
    [InlineData("::", false)]
    [InlineData("fe80::1", false)]
    [InlineData("fc00::1", false)]
    [InlineData("fd12:3456:789a::1", false)]
    [InlineData("ff02::1", false)]
    [InlineData("::ffff:127.0.0.1", false)]
    [InlineData("::ffff:10.0.0.1", false)]
    [InlineData("::ffff:8.8.8.8", true)]
    public void IsPublicAddress_KeepsTheInternetAndRefusesTheMachineItselfAndTheNetworksAroundIt(string address, bool expected)
    {
        Assert.Equal(expected, OtaAttachmentRules.IsPublicAddress(IPAddress.Parse(address)));
    }

    [Theory]
    [InlineData("image/jpeg", "image/jpeg")]
    [InlineData("IMAGE/PNG", "image/png")]
    [InlineData("application/octet-stream", "application/octet-stream")]
    [InlineData("image/jpeg; charset=binary", "image/jpeg")]
    [InlineData("text/html", null)]
    [InlineData("image/svg+xml", null)]
    [InlineData("application/pdf", null)]
    [InlineData("image/gif", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void AllowedContentTypeOf_KnowsTheThreeTypes(string? header, string? expected)
    {
        Assert.Equal(expected, OtaAttachmentRules.AllowedContentTypeOf(header));
    }

    [Fact]
    public void OnlyTheImagesAreRenderedByTheBrowser_AnythingElseIsADownload()
    {
        Assert.False(new OtaAttachmentContent([1], OtaAttachmentRules.Jpeg).AsDownload);
        Assert.False(new OtaAttachmentContent([1], OtaAttachmentRules.Png).AsDownload);
        Assert.True(new OtaAttachmentContent([1], OtaAttachmentRules.OctetStream).AsDownload);
    }

    [Fact]
    public void TheLimitsAre_TwentyMegabytes_ThirtySeconds()
    {
        Assert.Equal(20 * 1024 * 1024, OtaAttachmentRules.MaxBytes);
        Assert.Equal(TimeSpan.FromSeconds(30), OtaAttachmentRules.Timeout);
    }
}

public class OtaAttachmentFetcherTests
{
    private const string Address = "https://files.example.com/attachments/SECRET-TOKEN-9c1/photo.jpg?sig=SECRET-SIG";

    private static (OtaAttachmentFetcher Fetcher, MockHttpMessageHandler Http, LogSpy Log) Build()
    {
        var http = new MockHttpMessageHandler();
        var log = new LogSpy();
        return (new OtaAttachmentFetcher(new HttpClient(http), log), http, log);
    }

    private static ByteArrayContent Bytes(byte[] bytes, string contentType)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        return content;
    }

    [Fact]
    public async Task AFileOfAllowedType_IsReadWhole_WithTheTypeOfTheAnswer()
    {
        var (fetcher, http, _) = Build();
        http.When(HttpMethod.Get, Address).Respond(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = Bytes([1, 2, 3], "image/jpeg; charset=binary") });

        var file = await fetcher.FetchAsync(Address);

        Assert.NotNull(file);
        Assert.Equal([1, 2, 3], file.Content);
        Assert.Equal("image/jpeg", file.ContentType);
        Assert.False(file.AsDownload);
    }

    [Fact]
    public async Task AnyOtherFile_IsOfferedAsADownload()
    {
        var (fetcher, http, _) = Build();
        http.When(Address).Respond(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = Bytes([9], "application/octet-stream") });

        var file = await fetcher.FetchAsync(Address);

        Assert.NotNull(file);
        Assert.True(file.AsDownload);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.Gone)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task AnExpiredOrBrokenAddress_IsNoFile(HttpStatusCode status)
    {
        var (fetcher, http, _) = Build();
        http.When(Address).Respond(status);

        Assert.Null(await fetcher.FetchAsync(Address));
    }

    [Theory]
    [InlineData("text/html")]
    [InlineData("image/svg+xml")]
    [InlineData("application/pdf")]
    public async Task ATypeThatIsNotAllowed_IsNoFile(string contentType)
    {
        var (fetcher, http, _) = Build();
        http.When(Address).Respond(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = Bytes([1], contentType) });

        Assert.Null(await fetcher.FetchAsync(Address));
    }

    [Fact]
    public async Task AFileOverTwentyMegabytes_IsNoFile()
    {
        var (fetcher, http, _) = Build();
        http.When(Address).Respond(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = Bytes(new byte[OtaAttachmentRules.MaxBytes + 1], "image/png") });

        Assert.Null(await fetcher.FetchAsync(Address));
    }

    [Fact]
    public async Task AFileOfExactlyTheLimit_IsKept()
    {
        var (fetcher, http, _) = Build();
        http.When(Address).Respond(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = Bytes(new byte[OtaAttachmentRules.MaxBytes], "image/png") });

        var file = await fetcher.FetchAsync(Address);

        Assert.NotNull(file);
        Assert.Equal(OtaAttachmentRules.MaxBytes, file.Content.Length);
    }

    [Fact]
    public async Task AnEmptyFile_IsNoFile()
    {
        var (fetcher, http, _) = Build();
        http.When(Address).Respond(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = Bytes([], "image/png") });

        Assert.Null(await fetcher.FetchAsync(Address));
    }

    [Theory]
    [InlineData("http://files.example.com/photo.jpg")]
    [InlineData("https://127.0.0.1/photo.jpg")]
    [InlineData("https://169.254.169.254/latest/meta-data/")]
    [InlineData("not an address")]
    public async Task AnAddressThatIsNotAllowed_IsNeverCalled(string address)
    {
        var (fetcher, http, _) = Build();
        var anyCall = http.When("*").Respond(HttpStatusCode.OK);

        Assert.Null(await fetcher.FetchAsync(address));
        Assert.Equal(0, http.GetMatchCount(anyCall));
    }

    [Fact]
    public async Task ARedirectToAnAllowedAddress_IsFollowed()
    {
        var (fetcher, http, _) = Build();
        http.When(Address).Respond(_ =>
        {
            var redirect = new HttpResponseMessage(HttpStatusCode.Found);
            redirect.Headers.Location = new Uri("https://cdn.example.net/final/photo.jpg");
            return redirect;
        });
        http.When("https://cdn.example.net/final/photo.jpg").Respond(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = Bytes([7], "image/png") });

        var file = await fetcher.FetchAsync(Address);

        Assert.NotNull(file);
        Assert.Equal([7], file.Content);
    }

    [Theory]
    [InlineData("http://cdn.example.net/photo.jpg")]
    [InlineData("https://10.0.0.1/photo.jpg")]
    [InlineData("https://localhost/photo.jpg")]
    public async Task ARedirectToAnAddressThatIsNotAllowed_IsNoFile_AndIsNotFollowed(string target)
    {
        var (fetcher, http, _) = Build();
        http.When(Address).Respond(_ =>
        {
            var redirect = new HttpResponseMessage(HttpStatusCode.MovedPermanently);
            redirect.Headers.Location = new Uri(target);
            return redirect;
        });
        var forbidden = http.When(target).Respond(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = Bytes([1], "image/png") });

        Assert.Null(await fetcher.FetchAsync(Address));
        Assert.Equal(0, http.GetMatchCount(forbidden));
    }

    [Fact]
    public async Task EndlessRedirects_AreStopped()
    {
        var (fetcher, http, _) = Build();
        var hops = http.When("https://files.example.com/*").Respond(_ =>
        {
            var redirect = new HttpResponseMessage(HttpStatusCode.Found);
            redirect.Headers.Location = new Uri("https://files.example.com/again");
            return redirect;
        });

        Assert.Null(await fetcher.FetchAsync(Address));
        Assert.Equal(OtaAttachmentFetcher.MaxRedirects + 1, http.GetMatchCount(hops));
    }

    [Fact]
    public async Task AnAddressThatDoesNotAnswer_IsNoFile()
    {
        var (fetcher, http, _) = Build();
        http.When(Address).Throw(new HttpRequestException("connection refused"));

        Assert.Null(await fetcher.FetchAsync(Address));
    }

    [Fact]
    public async Task TheDeadline_IsNoFile_ButTheCancellationOfTheCallerStillCancels()
    {
        // The deadline of the fetch fires as a cancellation that is not the caller's: the file is not available.
        var timedOut = new OtaAttachmentFetcher(new HttpClient(new FakeHandler((_, _) => throw new TaskCanceledException("deadline"))), new LogSpy());
        Assert.Null(await timedOut.FetchAsync(Address));

        // The caller going away is not an answer about the file: it propagates.
        var waiting = new OtaAttachmentFetcher(
            new HttpClient(new FakeHandler(async (_, cancellationToken) =>
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.OK);
            })),
            new LogSpy());
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting.FetchAsync(Address, cancelled.Token));
    }

    [Fact]
    public async Task NothingOfTheAddressIsEverLogged()
    {
        var (fetcher, http, log) = Build();
        http.When(Address).Respond(HttpStatusCode.NotFound);
        await fetcher.FetchAsync(Address);
        await fetcher.FetchAsync("http://files.example.com/attachments/SECRET-TOKEN-9c1/photo.jpg?sig=SECRET-SIG");

        Assert.NotEmpty(log.Messages);
        Assert.All(log.Messages, message =>
        {
            Assert.DoesNotContain("SECRET", message, StringComparison.Ordinal);
            Assert.DoesNotContain("files.example.com", message, StringComparison.Ordinal);
        });
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }

    private sealed class LogSpy : ILogger<OtaAttachmentFetcher>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
            if (exception is not null)
                Messages.Add(exception.ToString());
        }
    }
}
