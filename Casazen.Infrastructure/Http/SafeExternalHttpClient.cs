using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.Http;

/// <summary>
/// Downloads URLs chosen by users (iCal feeds) without letting them reach the internal network (SSRF):
/// see <see cref="SafeExternalHttpClient"/>.
/// </summary>
public interface ISafeExternalHttpClient
{
    /// <summary>True when <paramref name="url"/> passes <see cref="ExternalUrlPolicy.TryParse"/> with the configured ports.</summary>
    bool TryValidateUrl(string? url, [NotNullWhen(true)] out Uri? uri);

    /// <summary>
    /// GET of <paramref name="url"/> as text. Throws <see cref="ExternalFetchException"/> when the URL, a redirect or
    /// the resolved address is not allowed, on network errors, non-success status, timeout or a body over the limit.
    /// </summary>
    Task<string> GetStringAsync(string url, CancellationToken cancellationToken = default);
}

/// <summary>DNS lookup used by <see cref="SafeExternalHttpClient"/> (replaced in tests).</summary>
public interface IExternalHostResolver
{
    Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken);
}

public sealed class SystemDnsHostResolver : IExternalHostResolver
{
    public Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken) =>
        Dns.GetHostAddressesAsync(host, cancellationToken);
}

/// <summary>
/// Anti-SSRF HTTP client (FD-16: A2-21, A4-10, A9-32):
/// <list type="bullet">
/// <item>only https URLs on an allowed port (443 by default), without credentials or internal host names;</item>
/// <item>the host is resolved in <see cref="SocketsHttpHandler.ConnectCallback"/> and the connection is refused when
/// any resolved address, or the address of the connected socket, is private, loopback, link-local (cloud metadata
/// 169.254.169.254), CGNAT, multicast, IPv6 ULA / link-local or IPv4-mapped. The check is on the address actually
/// connected, so a DNS answer that changes between checks (rebinding) cannot bypass it;</item>
/// <item>no proxy (the callback must see the real destination) and redirects followed by hand, each target checked
/// again, at most <see cref="SafeExternalHttpOptions.MaxRedirects"/>;</item>
/// <item>one short time budget for the whole download and a body read as a stream up to
/// <see cref="SafeExternalHttpOptions.MaxResponseBytes"/>.</item>
/// </list>
/// </summary>
public sealed class SafeExternalHttpClient : ISafeExternalHttpClient, IDisposable
{
    private const string UserAgent = "CasaZen-ExternalFetch/1.0";

    private readonly HttpClient _httpClient;
    private readonly SafeExternalHttpOptions _options;
    private readonly ILogger<SafeExternalHttpClient> _logger;

    public SafeExternalHttpClient(
        IOptions<SafeExternalHttpOptions> options,
        IExternalHostResolver resolver,
        ILogger<SafeExternalHttpClient> logger)
        : this(options.Value, CreateHandler(options.Value, resolver), logger)
    {
    }

    /// <summary>For tests: <paramref name="handler"/> replaces the network (redirects, large bodies).</summary>
    internal SafeExternalHttpClient(
        SafeExternalHttpOptions options,
        HttpMessageHandler handler,
        ILogger<SafeExternalHttpClient> logger)
    {
        _options = options;
        _logger = logger;
        _httpClient = new HttpClient(handler, disposeHandler: true)
        {
            // The time budget is enforced by GetStringAsync over the whole download, body included.
            Timeout = System.Threading.Timeout.InfiniteTimeSpan,
        };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
    }

    public bool TryValidateUrl(string? url, [NotNullWhen(true)] out Uri? uri) =>
        ExternalUrlPolicy.TryParse(url, _options.EffectiveAllowedPorts, out uri);

    public async Task<string> GetStringAsync(string url, CancellationToken cancellationToken = default)
    {
        if (!TryValidateUrl(url, out var current))
            throw new ExternalFetchException(ExternalFetchFailure.InvalidUrl, "The URL is not an allowed external https URL");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.EffectiveTimeout);

        try
        {
            for (var redirects = 0; ; redirects++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, current)
                {
                    Version = HttpVersion.Version11,
                    VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
                };
                using var response = await SendAsync(request, current, timeout.Token);

                if (IsRedirect(response.StatusCode))
                {
                    current = NextRedirectTarget(current, response, redirects);
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    throw new ExternalFetchException(
                        ExternalFetchFailure.Unreachable,
                        $"HTTP {(int)response.StatusCode} from {current.IdnHost}");
                }

                return await ReadBodyAsync(response, current, timeout.Token);
            }
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ExternalFetchException(
                ExternalFetchFailure.Timeout,
                $"No complete response from {current.IdnHost} within {_options.EffectiveTimeout.TotalSeconds:0}s",
                ex);
        }
        catch (ExternalFetchException ex) when (ex.Failure is ExternalFetchFailure.BlockedDestination or ExternalFetchFailure.RedirectRejected)
        {
            _logger.LogWarning("External request blocked ({Failure}): {Reason}", ex.Failure, ex.Message);
            throw;
        }
    }

    public void Dispose() => _httpClient.Dispose();

    internal static SocketsHttpHandler CreateHandler(SafeExternalHttpOptions options, IExternalHostResolver resolver)
    {
        var allowedPorts = options.EffectiveAllowedPorts;
        return new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = options.EffectiveTimeout,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            ConnectCallback = (context, cancellationToken) =>
                ConnectAsync(context.DnsEndPoint, allowedPorts, resolver, cancellationToken),
        };
    }

    /// <summary>
    /// Connection step of the handler: resolves the host, refuses blocked addresses and connects to the checked
    /// address itself, then checks the connected peer again.
    /// </summary>
    internal static async ValueTask<Stream> ConnectAsync(
        DnsEndPoint endPoint,
        IReadOnlyCollection<int> allowedPorts,
        IExternalHostResolver resolver,
        CancellationToken cancellationToken)
    {
        var host = endPoint.Host.Trim('[', ']');
        if (!allowedPorts.Contains(endPoint.Port))
            throw new ExternalFetchException(ExternalFetchFailure.BlockedDestination, $"Port {endPoint.Port} is not allowed");

        IPAddress[] addresses;
        if (IPAddress.TryParse(host, out var literal))
        {
            addresses = [literal];
        }
        else
        {
            try
            {
                addresses = await resolver.ResolveAsync(host, cancellationToken);
            }
            catch (SocketException ex)
            {
                throw new ExternalFetchException(ExternalFetchFailure.Unreachable, $"DNS lookup failed for {host}", ex);
            }
        }

        if (addresses.Length == 0)
            throw new ExternalFetchException(ExternalFetchFailure.Unreachable, $"DNS lookup returned no address for {host}");

        if (addresses.Any(ExternalUrlPolicy.IsBlockedAddress))
        {
            throw new ExternalFetchException(
                ExternalFetchFailure.BlockedDestination,
                $"{host} resolves to an address that is not public");
        }

        SocketException? lastError = null;
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, endPoint.Port), cancellationToken);

                if (socket.RemoteEndPoint is not IPEndPoint remote || ExternalUrlPolicy.IsBlockedAddress(remote.Address))
                {
                    throw new ExternalFetchException(
                        ExternalFetchFailure.BlockedDestination,
                        $"{host} connected to an address that is not public");
                }

                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException ex)
            {
                socket.Dispose();
                lastError = ex;
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        throw new ExternalFetchException(ExternalFetchFailure.Unreachable, $"Could not connect to {host}", lastError);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, Uri url, CancellationToken cancellationToken)
    {
        try
        {
            return await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            // Errors of ConnectAsync reach us wrapped in the HttpRequestException of the handler.
            for (Exception? inner = ex.InnerException; inner is not null; inner = inner.InnerException)
            {
                if (inner is ExternalFetchException refused)
                    throw new ExternalFetchException(refused.Failure, refused.Message, ex);
            }

            throw new ExternalFetchException(ExternalFetchFailure.Unreachable, $"Request to {url.IdnHost} failed ({ex.HttpRequestError})", ex);
        }
    }

    private Uri NextRedirectTarget(Uri current, HttpResponseMessage response, int redirectsSoFar)
    {
        if (redirectsSoFar >= _options.EffectiveMaxRedirects)
            throw new ExternalFetchException(ExternalFetchFailure.RedirectRejected, $"More than {_options.EffectiveMaxRedirects} redirects from {current.IdnHost}");

        var location = response.Headers.Location
            ?? throw new ExternalFetchException(ExternalFetchFailure.RedirectRejected, $"Redirect without Location from {current.IdnHost}");

        // On Unix a relative "/path" parses as an absolute file URI: resolve it against the current URL.
        var target = location.IsAbsoluteUri && !location.IsFile ? location : new Uri(current, location.OriginalString);
        if (!ExternalUrlPolicy.TryValidate(target, _options.EffectiveAllowedPorts, out var next))
            throw new ExternalFetchException(ExternalFetchFailure.RedirectRejected, $"Redirect from {current.IdnHost} to a URL that is not allowed");

        return next;
    }

    private async Task<string> ReadBodyAsync(HttpResponseMessage response, Uri url, CancellationToken cancellationToken)
    {
        var maxBytes = _options.EffectiveMaxResponseBytes;
        if (response.Content.Headers.ContentLength is { } declared && declared > maxBytes)
            throw new ExternalFetchException(ExternalFetchFailure.TooLarge, $"{url.IdnHost} declares {declared} bytes (limit {maxBytes})");

        using var body = new MemoryStream();
        var chunk = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            int read;
            while ((read = await stream.ReadAsync(chunk.AsMemory(), cancellationToken)) > 0)
            {
                if (body.Length + read > maxBytes)
                    throw new ExternalFetchException(ExternalFetchFailure.TooLarge, $"{url.IdnHost} sent more than {maxBytes} bytes");

                body.Write(chunk, 0, read);
            }
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException)
        {
            throw new ExternalFetchException(ExternalFetchFailure.Unreachable, $"Reading the response of {url.IdnHost} failed", ex);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunk);
        }

        body.Position = 0;
        using var reader = new StreamReader(body, ResolveEncoding(response), detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync(cancellationToken);
    }

    private static bool IsRedirect(HttpStatusCode status) => status is
        HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
        or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    private static Encoding ResolveEncoding(HttpResponseMessage response)
    {
        var charset = response.Content.Headers.ContentType?.CharSet?.Trim('"', ' ');
        if (string.IsNullOrEmpty(charset))
            return Encoding.UTF8;

        try
        {
            return Encoding.GetEncoding(charset);
        }
        catch (ArgumentException)
        {
            return Encoding.UTF8;
        }
    }
}
