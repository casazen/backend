namespace Casazen.Core.Entities.Enums;

/// <summary>How a host org publishes their public booking site (ADR-001).</summary>
public enum PublicHostMode
{
    CasazenSubdomain = 0,
    CasazenPath = 1,
    CustomDomain = 2,
}
