using Casazen.Core.Repositories;
using Casazen.Core.Services;

namespace Casazen.Infrastructure.Services;

public class AttestationGuidanceService(
    ITerritorialRentAgreementRepository agreements,
    IPropertyRepository properties) : IAttestationGuidanceService
{
    public async Task<AttestationGuidanceDto?> GetSignatoryOrganizationsAsync(
        Guid propertyId,
        CancellationToken cancellationToken = default)
    {
        var property = await properties.GetByIdAsync(propertyId);
        if (property is null)
            return null;

        var agreement = await agreements.GetByComuneAsync(property.City, cancellationToken);
        var organizations = agreement?.Signatories
            .Select(s => new AttestationSignatoryDto(s.Name, s.Role, s.Contact))
            .ToList() ?? [];

        return new AttestationGuidanceDto(property.City, organizations);
    }
}
