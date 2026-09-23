using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

namespace Casazen.Tests.Integration;

/// <summary>
/// The in-memory test server has no TCP connection, so <c>Connection.RemoteIpAddress</c> is not set. This filter runs
/// before the application's pipeline (and so before <c>UseForwardedHeaders</c>) and turns the <c>X-Test-Peer-Ip</c>
/// header into the TCP peer: the Railway proxy (a trusted network) or a client connecting directly.
/// </summary>
public sealed class TestPeerIpStartupFilter : IStartupFilter
{
    public const string HeaderName = "X-Test-Peer-Ip";

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use((context, nextMiddleware) =>
        {
            if (IPAddress.TryParse(context.Request.Headers[HeaderName].ToString(), out var peer))
            {
                context.Connection.RemoteIpAddress = peer;
                context.Connection.RemotePort = 40123;
            }

            return nextMiddleware(context);
        });
        next(app);
    };
}
