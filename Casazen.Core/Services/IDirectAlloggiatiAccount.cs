namespace Casazen.Core.Services;

public interface IDirectAlloggiatiAccount
{
    Task<bool> VerifyAsync(Guid propertyId, CancellationToken ct);
}
