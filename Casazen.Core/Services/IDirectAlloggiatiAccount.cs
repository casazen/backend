namespace Casazen.Core.Services;

public interface IDirectAlloggiatiAccount
{
    /// <summary>Checks the credentials of an authorized property without sending guests.</summary>
    Task<bool> VerifyAsync(Guid propertyId, CancellationToken ct);
}
