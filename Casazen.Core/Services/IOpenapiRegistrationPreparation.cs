using Casazen.Core.Entities;
using Casazen.Core.Leases;

namespace Casazen.Core.Services;

public interface IOpenapiRegistrationPreparation
{
    Task<OpenapiPreparationResult> GetAsync(Guid leaseId, CancellationToken ct = default);
    Task<OpenapiPreparationResult> SaveAsync(Guid leaseId, OpenapiRegistrationData data, CancellationToken ct = default);
    Task EnsureReadyAsync(LeaseContract lease, CancellationToken ct = default);
    Task<IReadOnlyDictionary<string, string>> BuildSearchAsync(LeaseContract lease, ReadOnlyMemory<byte> signedContract, CancellationToken ct = default);
}
