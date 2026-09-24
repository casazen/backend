using System.Net;
using System.Net.Sockets;
using System.Text;
using Casazen.Infrastructure.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Casazen.Tests.Unit.Infrastructure.Http;

/// <summary>
/// FD-16 (A2-21, A4-10, A9-32): the anti-SSRF client refuses private destinations (literal, resolved or reached by
/// redirect), stops oversized bodies and never follows more than the allowed redirects. No test touches the network:
/// DNS is a fake resolver and a refused address is never connected.
/// </summary>
public class SafeExternalHttpClientTests
{
    // ─── Real SocketsHttpHandler: DNS resolution and connect-time checks ──────

    [Theory]
    [InlineData("https://127.0.0.1/cal.ics")]
    [InlineData("https://10.1.2.3/cal.ics")]
    [InlineData("https://169.254.169.254/latest/meta-data/iam/security-credentials/")]
    [InlineData("https://[::1]/cal.ics")]
    [InlineData("http://example.com/cal.ics")]
    public async Task GetStringAsync_PrivateLiteralOrNonHttpsUrl_IsRefusedWithoutDnsOrConnection(string url)
    {
        var resolver = new FakeResolver();
        using var client = CreateNetworkClient(resolver);

        var ex = await Assert.ThrowsAsync<ExternalFetchException>(() => client.GetStringAsync(url));

        Assert.Equal(ExternalFetchFailure.InvalidUrl, ex.Failure);
        Assert.Empty(resolver.Lookups);
    }

    [Theory]
    [InlineData("10.0.0.5")]
    [InlineData("127.0.0.1")]
    [InlineData("169.254.169.254")]
    [InlineData("100.64.12.1")]
    [InlineData("::1")]
    [InlineData("fd12:3456::7")]
    [InlineData("::ffff:192.168.1.10")]
    public async Task GetStringAsync_HostnameResolvingToPrivateAddress_IsRefusedAtConnect(string resolvedAddress)
    {
        var resolver = new FakeResolver { ["feeds.attacker.example"] = [IPAddress.Parse(resolvedAddress)] };
        using var client = CreateNetworkClient(resolver);

        var ex = await Assert.ThrowsAsync<ExternalFetchException>(
            () => client.GetStringAsync("https://feeds.attacker.example/cal.ics"));

        Assert.Equal(ExternalFetchFailure.BlockedDestination, ex.Failure);
        // The only DNS lookup is the one of the connect callback: the checked address is the connected one.
        Assert.Equal(new[] { "feeds.attacker.example" }, resolver.Lookups);
    }

    [Fact]
    public async Task GetStringAsync_HostnameWithOnePrivateAmongPublicAddresses_IsRefused()
    {
        var resolver = new FakeResolver
        {
            ["mixed.attacker.example"] = [IPAddress.Parse("93.184.215.14"), IPAddress.Parse("10.0.0.5")],
        };
        using var client = CreateNetworkClient(resolver);

        var ex = await Assert.ThrowsAsync<ExternalFetchException>(
            () => client.GetStringAsync("https://mixed.attacker.example/cal.ics"));

        Assert.Equal(ExternalFetchFailure.BlockedDestination, ex.Failure);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("169.254.169.254")]
    [InlineData("[::1]")]
    [InlineData("::1")]
    public async Task ConnectAsync_LiteralPrivateHost_IsRefused(string host)
    {
        var resolver = new FakeResolver();

        var ex = await Assert.ThrowsAsync<ExternalFetchException>(
            () => SafeExternalHttpClient.ConnectAsync(new DnsEndPoint(host, 443), [443], resolver, CancellationToken.None).AsTask());

        Assert.Equal(ExternalFetchFailure.BlockedDestination, ex.Failure);
        Assert.Empty(resolver.Lookups);
    }

    [Fact]
    public async Task ConnectAsync_PortNotAllowed_IsRefusedBeforeDns()
    {
        var resolver = new FakeResolver();

        var ex = await Assert.ThrowsAsync<ExternalFetchException>(
            () => SafeExternalHttpClient.ConnectAsync(new DnsEndPoint("example.com", 22), [443], resolver, CancellationToken.None).AsTask());

        Assert.Equal(ExternalFetchFailure.BlockedDestination, ex.Failure);
        Assert.Empty(resolver.Lookups);
    }

    [Fact]
    public async Task ConnectAsync_DnsFailure_IsUnreachable()
    {
        var resolver = new FakeResolver(); // unknown host → SocketException, like NXDOMAIN

        var ex = await Assert.ThrowsAsync<ExternalFetchException>(
            () => SafeExternalHttpClient.ConnectAsync(new DnsEndPoint("missing.example", 443), [443], resolver, CancellationToken.None).AsTask());

        Assert.Equal(ExternalFetchFailure.Unreachable, ex.Failure);
    }

    // ─── Redirects ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("https://169.254.169.254/latest/meta-data/")]
    [InlineData("https://127.0.0.1/admin")]
    [InlineData("https://10.0.0.8/cal.ics")]
    [InlineData("https://[::1]/cal.ics")]
    [InlineData("https://localhost/cal.ics")]
    [InlineData("http://feeds.example.com/cal.ics")]
    public async Task GetStringAsync_RedirectToPrivateOrNonHttpsUrl_IsRefusedWithoutFollowing(string location)
    {
        var handler = new StubHandler(_ => Redirect(location));
        using var client = CreateStubClient(handler);

        var ex = await Assert.ThrowsAsync<ExternalFetchException>(
            () => client.GetStringAsync("https://feeds.example.com/cal.ics"));

        Assert.Equal(ExternalFetchFailure.RedirectRejected, ex.Failure);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task GetStringAsync_RedirectToHostnameResolvingToPrivateAddress_IsRefusedAtConnect()
    {
        // The first hop is served by a stub; the redirect target goes through the real handler and its
        // connect callback, where the fake DNS answers with a private address.
        var resolver = new FakeResolver { ["internal.attacker.example"] = [IPAddress.Parse("10.0.0.7")] };
        var options = new SafeExternalHttpOptions();
        var handler = new FirstHopStubHandler(
            "feeds.example.com",
            Redirect("https://internal.attacker.example/latest/meta-data/"),
            SafeExternalHttpClient.CreateHandler(options, resolver));
        using var client = new SafeExternalHttpClient(options, handler, NullLogger<SafeExternalHttpClient>.Instance);

        var ex = await Assert.ThrowsAsync<ExternalFetchException>(
            () => client.GetStringAsync("https://feeds.example.com/cal.ics"));

        Assert.Equal(ExternalFetchFailure.BlockedDestination, ex.Failure);
        Assert.Equal(new[] { "internal.attacker.example" }, resolver.Lookups);
    }

    [Fact]
    public async Task GetStringAsync_MoreRedirectsThanAllowed_IsRefused()
    {
        var handler = new StubHandler(request => Redirect($"https://feeds.example.com/hop{Guid.NewGuid():N}"));
        using var client = CreateStubClient(handler, new SafeExternalHttpOptions { MaxRedirects = 3 });

        var ex = await Assert.ThrowsAsync<ExternalFetchException>(
            () => client.GetStringAsync("https://feeds.example.com/cal.ics"));

        Assert.Equal(ExternalFetchFailure.RedirectRejected, ex.Failure);
        Assert.Equal(4, handler.Requests.Count); // the original request and 3 redirects
    }

    [Fact]
    public async Task GetStringAsync_RelativeRedirectToAllowedUrl_IsFollowed()
    {
        var handler = new StubHandler(request => request.RequestUri!.AbsolutePath == "/cal.ics"
            ? Redirect("/v2/cal.ics")
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("BEGIN:VCALENDAR") });
        using var client = CreateStubClient(handler);

        var body = await client.GetStringAsync("https://feeds.example.com/cal.ics");

        Assert.Equal("BEGIN:VCALENDAR", body);
        Assert.Equal("https://feeds.example.com/v2/cal.ics", handler.Requests[1].AbsoluteUri);
    }

    // ─── Size, status, time ───────────────────────────────────────────────────

    [Fact]
    public async Task GetStringAsync_DeclaredLengthOverLimit_IsRefusedBeforeReading()
    {
        var body = new CountingStream(totalBytes: 4096);
        var handler = new StubHandler(_ =>
        {
            var content = new StreamContent(body);
            content.Headers.ContentLength = 4096;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });
        using var client = CreateStubClient(handler, new SafeExternalHttpOptions { MaxResponseBytes = 1024 });

        var ex = await Assert.ThrowsAsync<ExternalFetchException>(
            () => client.GetStringAsync("https://feeds.example.com/cal.ics"));

        Assert.Equal(ExternalFetchFailure.TooLarge, ex.Failure);
        Assert.Equal(0, body.BytesRead);
    }

    [Fact]
    public async Task GetStringAsync_StreamedBodyOverLimit_IsInterrupted()
    {
        // No Content-Length and an (almost) endless body: the read must stop right after the limit.
        var body = new CountingStream(totalBytes: 50L * 1024 * 1024);
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) });
        using var client = CreateStubClient(handler, new SafeExternalHttpOptions { MaxResponseBytes = 64 * 1024 });

        var ex = await Assert.ThrowsAsync<ExternalFetchException>(
            () => client.GetStringAsync("https://feeds.example.com/cal.ics"));

        Assert.Equal(ExternalFetchFailure.TooLarge, ex.Failure);
        Assert.InRange(body.BytesRead, 64 * 1024, 128 * 1024);
    }

    [Fact]
    public async Task GetStringAsync_BodyWithinLimit_ReturnsText()
    {
        const string ics = "BEGIN:VCALENDAR\r\nSUMMARY:Città\r\nEND:VCALENDAR";
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(ics, Encoding.UTF8, "text/calendar"),
        });
        using var client = CreateStubClient(handler);

        var body = await client.GetStringAsync("https://feeds.example.com/cal.ics");

        Assert.Equal(ics, body);
    }

    [Fact]
    public async Task GetStringAsync_NonSuccessStatus_IsUnreachable()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        using var client = CreateStubClient(handler);

        var ex = await Assert.ThrowsAsync<ExternalFetchException>(
            () => client.GetStringAsync("https://feeds.example.com/cal.ics"));

        Assert.Equal(ExternalFetchFailure.Unreachable, ex.Failure);
    }

    [Fact]
    public async Task GetStringAsync_SlowServer_TimesOut()
    {
        var handler = new StubHandler(async (_, ct) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var client = CreateStubClient(handler, new SafeExternalHttpOptions { TimeoutSeconds = 1 });

        var ex = await Assert.ThrowsAsync<ExternalFetchException>(
            () => client.GetStringAsync("https://feeds.example.com/cal.ics"));

        Assert.Equal(ExternalFetchFailure.Timeout, ex.Failure);
    }

    [Fact]
    public async Task GetStringAsync_CallerCancels_ThrowsOperationCanceled()
    {
        var handler = new StubHandler(async (_, ct) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var client = CreateStubClient(handler);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.GetStringAsync("https://feeds.example.com/cal.ics", cts.Token));
    }

    [Fact]
    public void TryValidateUrl_UsesConfiguredPorts()
    {
        using var client = CreateStubClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)),
            new SafeExternalHttpOptions { AllowedPorts = [443, 8443] });

        Assert.True(client.TryValidateUrl("https://example.com:8443/cal.ics", out _));
        Assert.False(client.TryValidateUrl("https://example.com:9443/cal.ics", out _));
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    private static SafeExternalHttpClient CreateNetworkClient(IExternalHostResolver resolver) =>
        new(Options.Create(new SafeExternalHttpOptions()), resolver, NullLogger<SafeExternalHttpClient>.Instance);

    private static SafeExternalHttpClient CreateStubClient(HttpMessageHandler handler, SafeExternalHttpOptions? options = null) =>
        new(options ?? new SafeExternalHttpOptions(), handler, NullLogger<SafeExternalHttpClient>.Instance);

    private static HttpResponseMessage Redirect(string location)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Found);
        response.Headers.TryAddWithoutValidation("Location", location);
        return response;
    }

    private sealed class FakeResolver : IExternalHostResolver
    {
        private readonly Dictionary<string, IPAddress[]> _records = new(StringComparer.OrdinalIgnoreCase);

        public List<string> Lookups { get; } = [];

        public IPAddress[] this[string host]
        {
            set => _records[host] = value;
        }

        public Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken)
        {
            Lookups.Add(host);
            return _records.TryGetValue(host, out var addresses)
                ? Task.FromResult(addresses)
                : Task.FromException<IPAddress[]>(new SocketException((int)SocketError.HostNotFound));
        }
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _respond;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
            : this((request, _) => Task.FromResult(respond(request)))
        {
        }

        public StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) => _respond = respond;

        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return _respond(request, cancellationToken);
        }
    }

    /// <summary>Answers requests to <c>firstHost</c> with a canned response and sends the others to the real handler.</summary>
    private sealed class FirstHopStubHandler(string firstHost, HttpResponseMessage firstResponse, HttpMessageHandler network)
        : DelegatingHandler(network)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            request.RequestUri!.Host == firstHost
                ? Task.FromResult(firstResponse)
                : base.SendAsync(request, cancellationToken);
    }

    /// <summary>Non-seekable stream of <c>totalBytes</c> bytes that counts what was read.</summary>
    private sealed class CountingStream(long totalBytes) : Stream
    {
        public long BytesRead { get; private set; }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => BytesRead; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = (int)Math.Min(count, totalBytes - BytesRead);
            if (n <= 0)
                return 0;

            buffer.AsSpan(offset, n).Fill((byte)'A');
            BytesRead += n;
            return n;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
