using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

namespace Casazen.Web.Extensions;

/// <summary>
/// Forwarded headers behind the Railway edge proxy (#273, A1-12, A3-30, A9-10). <c>app.UseForwardedHeaders()</c> is
/// the first middleware: from then on the client IP is <c>HttpContext.Connection.RemoteIpAddress</c> (see
/// <see cref="Infrastructure.ClientIp"/>) and the scheme is the one the client used.
/// </summary>
/// <remarks>
/// Configuration (section <c>ForwardedHeaders</c>, runbook <c>docs/runbooks/proxy-ip.md</c>):
/// <list type="bullet">
/// <item><c>KnownNetworks</c>: CIDR networks of the trusted proxies (array, or one string separated by commas);</item>
/// <item><c>KnownProxies</c>: single IP addresses of trusted proxies (same format);</item>
/// <item><c>ForwardLimit</c>: maximum number of <c>X-Forwarded-For</c> entries processed, right to left (default 1).</item>
/// </list>
/// With networks or proxies configured, the middleware walks <c>X-Forwarded-For</c> from the right and stops at the
/// first address that is not a trusted proxy. With none configured, the peer is trusted as the proxy and only the
/// last <c>ForwardLimit</c> entries are used: with the default of 1 that is the address the edge proxy appended
/// itself. The first (leftmost) value is never used, because the client writes it.
/// </remarks>
public static class ForwardedHeadersServiceCollectionExtensions
{
    public const string SectionName = "ForwardedHeaders";
    public const int DefaultForwardLimit = 1;

    public static IServiceCollection AddCasazenForwardedHeaders(this IServiceCollection services)
    {
        // Read from the final configuration (IConfiguration from DI), not while Program.cs is still building it.
        services.AddOptions<ForwardedHeadersOptions>()
            .Configure<IConfiguration>((options, configuration) =>
                Configure(options, configuration.GetSection(SectionName)));
        return services;
    }

    /// <summary>Applies the <c>ForwardedHeaders</c> section; throws on an invalid value so a typo fails the startup.</summary>
    public static void Configure(ForwardedHeadersOptions options, IConfiguration section)
    {
        var forwardLimit = section.GetValue<int?>("ForwardLimit") ?? DefaultForwardLimit;
        if (forwardLimit < 1)
            throw new InvalidOperationException($"{SectionName}:ForwardLimit must be at least 1 (was {forwardLimit}).");

        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        options.ForwardLimit = forwardLimit;

        // Replace the defaults (loopback only, which never matches the Railway proxy) with the configured lists.
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();
#pragma warning disable ASPDEPR005 // Obsolete list replaced by KnownIPNetworks: cleared too, so no default survives.
        options.KnownNetworks.Clear();
#pragma warning restore ASPDEPR005

        foreach (var value in ReadList(section.GetSection("KnownNetworks")))
        {
            if (!System.Net.IPNetwork.TryParse(value, out var network))
                throw new InvalidOperationException($"{SectionName}:KnownNetworks contains an invalid CIDR network: '{value}'.");
            options.KnownIPNetworks.Add(network);
        }

        foreach (var value in ReadList(section.GetSection("KnownProxies")))
        {
            if (!IPAddress.TryParse(value, out var address))
                throw new InvalidOperationException($"{SectionName}:KnownProxies contains an invalid IP address: '{value}'.");
            options.KnownProxies.Add(address);
        }
    }

    // Accepts both an array (KnownNetworks:0, KnownNetworks__0 on Railway) and a single comma-separated string.
    private static IEnumerable<string> ReadList(IConfigurationSection section)
    {
        var values = string.IsNullOrWhiteSpace(section.Value)
            ? section.GetChildren().Select(child => child.Value)
            : [section.Value];

        return values
            .SelectMany(value => (value ?? string.Empty).Split(
                [',', ';', ' '],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }
}
