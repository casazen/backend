using System.Security.Claims;
using Casazen.Core.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

[ApiController]
[Route("api/properties/{propertyId:guid}/canone-concordato")]
[Authorize(Policy = "LongTermLandlord")]
[Authorize(Policy = "RequireContext:long-rent:lease.read")]
public class CanoneConcordatoController(
    ICanoneConcordatoEligibilityService eligibility,
    IAttestationGuidanceService attestation) : ControllerBase
{
    private string? GetOwnerId() =>
        User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");

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
        if (GetOwnerId() is not { } ownerId)
            return Unauthorized();

        var result = await eligibility.CalculateAsync(
            propertyId,
            ownerId,
            new RentBandCharacteristics(sqm, typeACount, typeBCount, typeCCount, typeDCount, furnished, years, zone, foglio),
            cancellationToken);

        return result is null ? NotFound() : Ok(result);
    }

    /// <summary>List signatory associations that can issue an attestazione di conformità. Contacts only.</summary>
    [HttpGet("attestation-guidance")]
    public async Task<IActionResult> GetAttestationGuidance(Guid propertyId, CancellationToken cancellationToken)
    {
        if (GetOwnerId() is not { } ownerId)
            return Unauthorized();

        var result = await attestation.GetSignatoryOrganizationsAsync(propertyId, ownerId, cancellationToken);
        return result is null ? NotFound() : Ok(result);
    }
}
