using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// Canone concordato helpers of the lease form. The property is authorized as a <see cref="Casazen.Core.Authorization.HostResource"/>
/// with <see cref="LeaseOperations.Read"/> (TN-3): its owner or an org-wide member of its org with <c>lease.read</c>;
/// another org's property answers 404, a property of the org the caller may not handle 403.
/// </summary>
[ApiController]
[Route("api/properties/{propertyId:guid}/canone-concordato")]
[Authorize(Policy = CasazenPolicies.LeaseRead)]
public class CanoneConcordatoController(
    ICanoneConcordatoEligibilityService eligibility,
    IAttestationGuidanceService attestation,
    IHostResourceLookup hostResources,
    IAuthorizationService authorizationService) : ControllerBase
{
    private const string PropertyNotFoundCode = "property_not_found";

    /// <summary>Calculate canone concordato eligibility and rent range for a property.</summary>
    [HttpGet("eligibility")]
    public async Task<IActionResult> GetEligibility(
        Guid propertyId,
        [FromQuery] decimal sqm,
        [FromQuery] int typeACount,
        [FromQuery] int typeBCount,
        [FromQuery] int typeCCount,
        [FromQuery] int typeDCount,
        [FromQuery] bool furnished,
        [FromQuery] int years,
        [FromQuery] string? zone,
        [FromQuery] string? foglio,
        CancellationToken cancellationToken)
    {
        if (await AuthorizePropertyAsync(propertyId, cancellationToken) is { } denied)
            return denied;

        var result = await eligibility.CalculateAsync(
            propertyId,
            new RentBandCharacteristics(sqm, typeACount, typeBCount, typeCCount, typeDCount, furnished, years, zone, foglio),
            cancellationToken);

        return result is null ? NotFound() : Ok(result);
    }

    /// <summary>List signatory associations that can issue an attestazione di conformità. Contacts only.</summary>
    [HttpGet("attestation-guidance")]
    public async Task<IActionResult> GetAttestationGuidance(Guid propertyId, CancellationToken cancellationToken)
    {
        if (await AuthorizePropertyAsync(propertyId, cancellationToken) is { } denied)
            return denied;

        var result = await attestation.GetSignatoryOrganizationsAsync(propertyId, cancellationToken);
        return result is null ? NotFound() : Ok(result);
    }

    private async Task<IActionResult?> AuthorizePropertyAsync(Guid propertyId, CancellationToken cancellationToken)
    {
        if (User.GetUserId() is null)
            return Unauthorized();

        var property = await hostResources.ForPropertyAsync(propertyId, cancellationToken);
        if (property is null)
            return this.ApiProblem(StatusCodes.Status404NotFound, PropertyNotFoundCode, "PropertyNotFound");

        return await authorizationService.IsAuthorizedAsync(User, property, LeaseOperations.Read) ? null : Forbid();
    }
}
