namespace Casazen.Core.Services;

public interface IDirectAlloggiatiRoute
{
    Task<DateOnly?> EffectiveFromAsync(Guid orgId, Guid propertyId, CancellationToken ct);
}
