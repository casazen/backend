namespace Casazen.Core.Services;

/// <summary>First reporting day reserved exclusively for direct ISTAT transmission, or null while another sender may be active.</summary>
public interface IRoss1000DirectRoute
{
    Task<DateOnly?> EffectiveFromAsync(Guid orgId, Guid propertyId, CancellationToken ct);
}
