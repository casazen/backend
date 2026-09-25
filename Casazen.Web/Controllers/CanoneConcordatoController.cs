using System.ComponentModel.DataAnnotations;
using Casazen.Core.Leases;
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

    /// <summary>
    /// Canone concordato range of the property (LT-10) for the unit characteristics and the lease dates: the term, and so
    /// the minimum 3 years and the 4/5/6-year uplifts, comes from <c>startDate</c>/<c>endDate</c> (end inclusive), never
    /// from a client-side year count (A7-12). The lease creation recomputes the same range on the server. <c>indicative</c>
    /// with the <c>partial_data</c> warning: the agreement data are not confirmed and the range is only a guide (A7-23).
    /// </summary>
    [HttpGet("eligibility")]
    public async Task<IActionResult> GetEligibility(
        Guid propertyId,
        [FromQuery] CanoneConcordatoRangeQuery query,
        CancellationToken cancellationToken)
    {
        if (await AuthorizePropertyAsync(propertyId, cancellationToken) is { } denied)
            return denied;

        if (LeaseTerm.Between(query.StartDate!.Value, query.EndDate!.Value) is not { } term
            || query.EndDate.Value.Date <= query.StartDate.Value.Date)
        {
            return this.ApiProblem(StatusCodes.Status422UnprocessableEntity, LeaseTermErrorCodes.EndBeforeStart, "LeaseEndBeforeStart");
        }

        var result = await eligibility.CalculateAsync(propertyId, query.ToCharacteristics(), term, cancellationToken);

        return result is null ? NotFound() : Ok(result);
    }

    /// <summary>
    /// Zones of the territorial agreement of the property's comune (A7-24): the calculator offers them as a select,
    /// never free text, so the value always matches a zone the agreement actually defines.
    /// </summary>
    [HttpGet("zones")]
    public async Task<IActionResult> GetZones(Guid propertyId, CancellationToken cancellationToken)
    {
        if (await AuthorizePropertyAsync(propertyId, cancellationToken) is { } denied)
            return denied;

        var result = await eligibility.GetZonesAsync(propertyId, cancellationToken);
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

/// <summary>
/// Query of <c>GET /api/properties/{id}/canone-concordato/eligibility</c> (LT-10): the unit characteristics and the lease
/// dates. Appurtenances in square metres; element counts as defined by the territorial agreement.
/// </summary>
public sealed class CanoneConcordatoRangeQuery
{
    [Range(0, 10_000)]
    public decimal Sqm { get; set; }

    [Range(0, 10_000)]
    public decimal GarageSqm { get; set; }

    [Range(0, 10_000)]
    public decimal BalconySqm { get; set; }

    [Range(0, 10_000)]
    public decimal OtherAppurtenanceSqm { get; set; }

    [Range(0, 100_000)]
    public decimal PrivateGreenSqm { get; set; }

    [Range(0, 100)]
    public int TypeACount { get; set; }

    [Range(0, 100)]
    public int TypeBCount { get; set; }

    [Range(0, 100)]
    public int TypeCCount { get; set; }

    [Range(0, 100)]
    public int TypeDCount { get; set; }

    /// <summary>D-elements among those the agreement lists for sub-fascia 3.</summary>
    [Range(0, 100)]
    public int QualifyingTypeDCount { get; set; }

    public bool Furnished { get; set; }

    public bool AirConditioning { get; set; }

    public bool StoveHeating { get; set; }

    [MaxLength(100)]
    public string? Zone { get; set; }

    /// <summary>Cadastral sheet; when empty the property's own sheet is used.</summary>
    [MaxLength(20)]
    public string? Foglio { get; set; }

    /// <summary>Start of the lease (date).</summary>
    [Required]
    public DateTime? StartDate { get; set; }

    /// <summary>End of the lease (date, inclusive).</summary>
    [Required]
    public DateTime? EndDate { get; set; }

    public RentBandCharacteristics ToCharacteristics() => new()
    {
        Sqm = Sqm,
        GarageSqm = GarageSqm,
        BalconySqm = BalconySqm,
        OtherAppurtenanceSqm = OtherAppurtenanceSqm,
        PrivateGreenSqm = PrivateGreenSqm,
        TypeAElementCount = TypeACount,
        TypeBElementCount = TypeBCount,
        TypeCElementCount = TypeCCount,
        TypeDElementCount = TypeDCount,
        QualifyingTypeDElementCount = QualifyingTypeDCount,
        StoveHeating = StoveHeating,
        IsFurnished = Furnished,
        AirConditioning = AirConditioning,
        ZoneName = Zone,
        CadastralSheet = Foglio,
    };
}
