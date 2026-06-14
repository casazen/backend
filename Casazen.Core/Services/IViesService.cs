namespace Casazen.Core.Services;

public interface IViesService
{
    Task<bool> ValidateVatIdAsync(string countryCode, string vatId, CancellationToken cancellationToken = default);
}
