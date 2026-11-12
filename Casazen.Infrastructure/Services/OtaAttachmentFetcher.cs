using System.Net;
using System.Net.Sockets;
using Casazen.Core.Services;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IOtaAttachmentFetcher"/>
/// <remarks>
/// <para><b>The address is untrusted.</b> The channel manager read it from a channel, so before the call it must be an https address of a host name
/// (<see cref="OtaAttachmentRules.TryGetFetchUri"/>), and when the connection is made the name must resolve to public addresses only
/// (<see cref="CreateHandler"/>: a name that points at the machine itself, at the private network or at the metadata service of the cloud is refused
/// where the socket is opened, so a DNS answer cannot be changed between the check and the call). A redirect is followed at most three times and each
/// target is checked like the first address.</para>
/// <para><b>Bounded.</b> One deadline of 30 seconds for the headers and the body together, at most 20 MB read (the declared length is checked first),
/// and only the three types of <see cref="OtaAttachmentRules.AllowedContentTypes"/> are accepted. No credential of the channel manager is sent.</para>
/// <para><b>Nothing of the address is logged</b> (it may carry a token): the logs say that the file was refused, what status came back or what kind of
/// error it was.</para>
/// </remarks>
public sealed class OtaAttachmentFetcher(HttpClient http, ILogger<OtaAttachmentFetcher> logger) : IOtaAttachmentFetcher
{
    /// <summary>How many redirects of the first address are followed.</summary>
    public const int MaxRedirects = 3;

    private const int BufferSize = 81920;

    public async Task<OtaAttachmentContent?> FetchAsync(string address, CancellationToken cancellationToken = default)
    {
        if (!OtaAttachmentRules.TryGetFetchUri(address, out var uri))
        {
            logger.LogInformation("An attachment address was refused: it is not an https address of a public host");
            return null;
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(OtaAttachmentRules.Timeout);
        try
        {
            for (var hop = 0; hop <= MaxRedirects; hop++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);

                if (IsRedirect(response.StatusCode))
                {
                    if (response.Headers.Location is not { } location
                        || !OtaAttachmentRules.TryGetFetchUri(new Uri(uri, location).AbsoluteUri, out var next))
                    {
                        logger.LogInformation("An attachment redirected to an address that is not allowed");
                        return null;
                    }

                    uri = next;
                    continue;
                }

                return await ReadAsync(response, deadline.Token);
            }

            logger.LogInformation("An attachment redirected more than {Max} times", MaxRedirects);
            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException or InvalidOperationException or UriFormatException)
        {
            // The deadline, an address that did not answer, a name that resolves to a private address, a TLS error: the file is not available. Only
            // the kind of error is logged, never the exception itself, whose message may name the host.
            logger.LogWarning("An attachment could not be fetched ({Reason})", ex.GetType().Name);
            return null;
        }
    }

    /// <summary>
    /// The handler of the client: no automatic redirect (each hop is checked by <see cref="FetchAsync"/>), no cookies, a connect timeout, and a
    /// connection that goes only to public addresses.
    /// </summary>
    public static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        ConnectCallback = ConnectToPublicAddressAsync,
    };

    /// <summary>
    /// The public addresses of <paramref name="host"/>: what its name resolves to, without the loopback, private, link-local and other special
    /// addresses. <see cref="HttpRequestException"/> when none is left, so a name that points at the machine itself or at the private network is
    /// refused where the socket would be opened.
    /// </summary>
    internal static async Task<IPAddress[]> ResolvePublicAddressesAsync(string host, CancellationToken cancellationToken)
    {
        IPAddress[] addresses;
        try
        {
            addresses = await Dns.GetHostAddressesAsync(host, cancellationToken);
        }
        catch (Exception ex) when (ex is ArgumentException or SocketException)
        {
            // An unspecified address (0.0.0.0, ::) is refused by the resolver itself; a name that does not resolve is the same to a caller.
            throw new HttpRequestException("The host could not be resolved to an address.", ex);
        }

        var allowed = addresses.Where(OtaAttachmentRules.IsPublicAddress).ToArray();
        if (allowed.Length == 0)
            throw new HttpRequestException("The host has no public address.");

        return allowed;
    }

    private async Task<OtaAttachmentContent?> ReadAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            // 403, 404 and 410 are how an expired address answers; whatever the status, there is no file.
            logger.LogInformation("An attachment answered with status {Status}", (int)response.StatusCode);
            return null;
        }

        var contentType = OtaAttachmentRules.AllowedContentTypeOf(response.Content.Headers.ContentType?.MediaType);
        if (contentType is null)
        {
            logger.LogInformation("An attachment is of a type that is not allowed");
            return null;
        }

        if (response.Content.Headers.ContentLength > OtaAttachmentRules.MaxBytes)
        {
            logger.LogInformation("An attachment is larger than {Max} bytes", OtaAttachmentRules.MaxBytes);
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[BufferSize];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > OtaAttachmentRules.MaxBytes)
            {
                logger.LogInformation("An attachment is larger than {Max} bytes", OtaAttachmentRules.MaxBytes);
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.Length == 0 ? null : new OtaAttachmentContent(buffer.ToArray(), contentType);
    }

    private static bool IsRedirect(HttpStatusCode status) =>
        status is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    /// <summary>
    /// Opens the socket of a connection to a public address of the host and to no other: the name is resolved here, the private and special
    /// addresses are dropped, and the connection goes to what is left. The TLS handshake is made by the handler on the stream this returns, for the
    /// host name of the request.
    /// </summary>
    private static async ValueTask<Stream> ConnectToPublicAddressAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var allowed = await ResolvePublicAddressesAsync(context.DnsEndPoint.Host, cancellationToken);

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(allowed, context.DnsEndPoint.Port, cancellationToken);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
