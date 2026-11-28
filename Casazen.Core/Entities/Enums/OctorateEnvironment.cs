namespace Casazen.Core.Entities.Enums;

/// <summary>
/// The Octorate environment a connection belongs to (ADR-023, NF-28). Sandbox and production share one host: the application
/// credentials decide, and <c>GET /rest/v1/api/configuration</c> reports which one they are (<c>environment</c> = <c>SANDBOX</c> or
/// <c>PRODUCTION</c>). CasaZen's production works with Octorate's production only; every other CasaZen environment (test, local) with
/// the sandbox. Stored as its number; 0 is no environment.
/// </summary>
public enum OctorateEnvironment
{
    Test = 1,

    Production = 2,
}
