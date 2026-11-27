using Casazen.Core.Entities;

namespace Casazen.Core.Services;

public interface IOpenapiRegistrationPaymentGate
{
    Task EnsurePaidAsync(LeaseContract lease, CancellationToken cancellationToken = default);
}
