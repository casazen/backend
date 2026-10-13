using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

public record AttestationSignatoryDto(string Name, SignatoryRole Role, string Contact);

public record AttestationGuidanceDto(
    string Comune,
    IReadOnlyList<AttestationSignatoryDto> Organizations);

public interface IAttestationGuidanceService
{
    /// <summary>Signatories of the property's comune agreement, or <c>null</c> when the property is not visible. The caller authorizes the property first (TN-3).</summary>
    Task<AttestationGuidanceDto?> GetSignatoryOrganizationsAsync(
        Guid propertyId,
        CancellationToken cancellationToken = default);
}
