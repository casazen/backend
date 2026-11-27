namespace Casazen.Core.Entities.Enums;

/// <summary>
/// The Octorate environment a connection belongs to (ADR-023, NF-28). Sandbox and production share one host: the application
/// credentials decide, and <c>GET /rest/v1/api/configuration</c> reports which one they are (<c>environment</c> = <c>SANDBOX</c> or
/// <c>PRODUCTION</c>). CasaZen's production works with Octorate's production only; every other CasaZen environment (test, local) with
/// the sandbox. Stored as its number; 0 is no environment.
/// </summary>
public enum OctorateEnvironment
{
    /// <summary>The Octorate sandbox (<c>environment</c> = <c>SANDBOX</c>), the only one of the test environment of CasaZen.</summary>
    Test = 1,

    /// <summary>Octorate production (<c>environment</c> = <c>PRODUCTION</c>), the only one of the production of CasaZen.</summary>
    Production = 2,
}
