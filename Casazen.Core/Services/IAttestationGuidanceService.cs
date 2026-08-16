using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

public record AttestationSignatoryDto(string Name, SignatoryRole Role, string Contact);

public record AttestationGuidanceDto(
    string Comune,
    IReadOnlyList<AttestationSignatoryDto> Organizations);

public interface IAttestationGuidanceService
{
    Task<AttestationGuidanceDto?> GetSignatoryOrganizationsAsync(
        Guid propertyId,
        string ownerId,
        CancellationToken cancellationToken = default);
}
