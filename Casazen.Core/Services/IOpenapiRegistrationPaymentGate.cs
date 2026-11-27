using Casazen.Core.Entities;

namespace Casazen.Core.Services;

/// <summary>
/// The filing fee must be paid before an Openapi registration proceeds. The percentage is
/// <see cref="Casazen.Core.Leases.OpenapiRegistrationFee"/> (the existing registration-tax rate).
/// </summary>
public interface IOpenapiRegistrationPaymentGate
{
    /// <summary>
    /// Throws <see cref="Casazen.Core.Exceptions.DomainConflictException"/> with
    /// <c>openapi_registration_unpaid</c> when the fee for <paramref name="lease"/> is not paid.
    /// A zero quote does not block.
    /// </summary>
    Task EnsurePaidAsync(LeaseContract lease, CancellationToken cancellationToken = default);
}
