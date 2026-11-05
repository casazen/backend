using Casazen.Core.Entities.Enums;
using Microsoft.Extensions.Hosting;

namespace Casazen.Infrastructure.Octorate;

/// <summary>
/// Which Octorate environment this CasaZen works with, and how Octorate names it (NF-28, ADR-023-R6). Sandbox and production share
/// one host and the credentials decide: CasaZen's production is the only one that works with Octorate's production, every other
/// CasaZen environment (Staging on Railway test, Development, Testing) with the sandbox. The connection stores the environment it was
/// made in, and <c>GET /rest/v1/api/configuration</c> must report the same one.
/// </summary>
public static class OctorateEnvironments
{
    /// <summary>What <c>GET /rest/v1/api/configuration</c> reports for the sandbox.</summary>
    public const string SandboxName = "SANDBOX";

    /// <summary>What <c>GET /rest/v1/api/configuration</c> reports for production.</summary>
    public const string ProductionName = "PRODUCTION";

    /// <summary>The Octorate environment of this CasaZen: <see cref="OctorateEnvironment.Production"/> only in Production.</summary>
    public static OctorateEnvironment Of(IHostEnvironment hostEnvironment)
    {
        ArgumentNullException.ThrowIfNull(hostEnvironment);
        return hostEnvironment.IsProduction() ? OctorateEnvironment.Production : OctorateEnvironment.Test;
    }

    /// <summary>The name Octorate gives <paramref name="environment"/> in its configuration.</summary>
    public static string ReportedNameOf(OctorateEnvironment environment) => environment switch
    {
        OctorateEnvironment.Test => SandboxName,
        OctorateEnvironment.Production => ProductionName,
        _ => throw new ArgumentOutOfRangeException(nameof(environment), environment, "Not an Octorate environment."),
    };

    /// <summary>True when Octorate reported <paramref name="reported"/> for credentials of <paramref name="expected"/>; an empty or unknown value never matches.</summary>
    public static bool Matches(OctorateEnvironment expected, string? reported) =>
        !string.IsNullOrWhiteSpace(reported)
        && string.Equals(reported.Trim(), ReportedNameOf(expected), StringComparison.OrdinalIgnoreCase);
}
