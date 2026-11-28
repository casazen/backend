using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs.CheckIn;
using Casazen.Web.DTOs.TouristTax;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// The tourist tax of a stay guest by guest and the rate an org declares for a comune with no official one (NF-46, ADR-037; the web wizard
/// E03/F01/US04/T02 calls these). TN-3, the pattern of <see cref="BookingLifecycleController"/>: the tenant filter hides a booking or a property
/// of another org (404), and a row the caller may not use on its property is 404 as well. Domain errors are ProblemDetails with the codes of
/// <c>StayTouristTaxService</c> (<c>tax_not_calculable</c>, <c>tax_exemption_unknown</c>, <c>tax_guest_unknown</c>,
/// <c>tax_collector_not_applicable</c>, <c>tax_rate_official_exists</c>, <c>tax_rate_source_required</c>, <c>tax_rate_invalid</c>).
/// Since E25/F01/US01/T01 (NF-119) the collection of the tax too, behind <see cref="FeatureFlags.TouristTaxOnlinePayment"/>: the block <c>collection</c>
/// of the read, «Pagata in struttura» and the setting of the organization (codes of <see cref="TouristTaxCharges"/>).
/// </summary>
[ApiController]
[Route("api")]
public class StayTouristTaxController(
    IStayTouristTaxService stayTouristTax,
    IHostResourceLookup hostResources,
    IAuthorizationService authorizationService,
    ILogger<StayTouristTaxController> logger,
    ITouristTaxCollectionService? touristTaxCollection = null,
    IFeatureFlags? featureFlags = null,
    IOrgContextResolver? orgContextResolver = null) : ControllerBase
{
    [HttpGet("bookings/{bookingId:guid}/tourist-tax")]
    [Authorize(Policy = CasazenPolicies.BookingRead)]
    [ProducesResponseType(typeof(StayTouristTaxDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<StayTouristTaxDto>> Get(Guid bookingId, CancellationToken cancellationToken)
    {
        if (!await CanUseBookingAsync(bookingId, BookingOperations.Read, cancellationToken))
            return BookingNotFound();

        var view = await stayTouristTax.GetAsync(bookingId, cancellationToken);
        TouristTaxCollectionDto? collection = null;
        if (touristTaxCollection is not null && featureFlags?.IsEnabled(FeatureFlags.TouristTaxOnlinePayment) == true)
            collection = TouristTaxCollectionDto.From(await touristTaxCollection.GetForHostAsync(bookingId, view, cancellationToken));

        return Ok(StayTouristTaxDto.From(view, await CanReadGuestsAsync(bookingId, cancellationToken), collection));
    }

    [HttpPost("bookings/{bookingId:guid}/tourist-tax/paid-at-property")]
    [FeatureGate(FeatureFlags.TouristTaxOnlinePayment)]
    [Authorize(Policy = CasazenPolicies.PaymentWrite)]
    [ProducesResponseType(typeof(TouristTaxCollectionDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<TouristTaxCollectionDto>> RecordPaidAtProperty(Guid bookingId, CancellationToken cancellationToken)
    {
        if (touristTaxCollection is null || !await CanUseBookingAsync(bookingId, PaymentOperations.Write, cancellationToken))
            return BookingNotFound();
        if (User.GetUserId() is not { Length: > 0 } userId)
            return Unauthorized();

        var view = await touristTaxCollection.RecordPaidAtPropertyAsync(bookingId, userId, cancellationToken);
        return Ok(TouristTaxCollectionDto.From(view));
    }

    /// <summary>
    /// The setting <c>TouristTaxOnlinePayment</c> of the caller's organization (E25/F01/US01/T01): whether its guests pay the tourist tax online in the check-in,
    /// whether the page asks for it before the lock code, and whether its connected account takes payments. Org policy
    /// <see cref="CasazenPolicies.OrgBillingAdmin"/>, like the rest of the org's settings. Behind <see cref="FeatureFlags.TouristTaxOnlinePayment"/>.
    /// </summary>
    [HttpGet("orgs/me/tourist-tax-settings")]
    [FeatureGate(FeatureFlags.TouristTaxOnlinePayment)]
    [Authorize(Policy = CasazenPolicies.OrgBillingAdmin)]
    [ProducesResponseType(typeof(TouristTaxSettingsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<TouristTaxSettingsDto>> GetSettings(CancellationToken cancellationToken)
    {
        if (touristTaxCollection is null || orgContextResolver is null
            || await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken) is not { } orgId)
        {
            return OrganizationNotFound();
        }

        var settings = await touristTaxCollection.GetSettingsAsync(orgId, cancellationToken);
        return settings is null ? OrganizationNotFound() : Ok(TouristTaxSettingsDto.From(settings));
    }

    [HttpPut("orgs/me/tourist-tax-settings")]
    [FeatureGate(FeatureFlags.TouristTaxOnlinePayment)]
    [Authorize(Policy = CasazenPolicies.OrgBillingAdmin)]
    [ProducesResponseType(typeof(TouristTaxSettingsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<TouristTaxSettingsDto>> SaveSettings(
        [FromBody] TouristTaxSettingsDto request,
        CancellationToken cancellationToken)
    {
        if (touristTaxCollection is null || orgContextResolver is null
            || await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken) is not { } orgId)
        {
            return OrganizationNotFound();
        }

        if (User.GetUserId() is not { Length: > 0 } userId)
            return Unauthorized();

        var settings = await touristTaxCollection.SaveSettingsAsync(orgId, request.Enabled, request.RequiredBeforeAccessCode, userId, cancellationToken);
        return settings is null ? OrganizationNotFound() : Ok(TouristTaxSettingsDto.From(settings));
    }

    [HttpPut("bookings/{bookingId:guid}/tourist-tax/exemptions")]
    [Authorize(Policy = CasazenPolicies.BookingWrite)]
    [ProducesResponseType(typeof(StayTouristTaxDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<StayTouristTaxDto>> SetExemptions(
        Guid bookingId,
        [FromBody] SetTouristTaxExemptionsRequest request,
        CancellationToken cancellationToken)
    {
        if (!await CanUseBookingAsync(bookingId, BookingOperations.Write, cancellationToken))
            return BookingNotFound();
        if (User.GetUserId() is not { Length: > 0 } userId)
            return Unauthorized();

        var view = await stayTouristTax.SetExemptionsAsync(
            bookingId,
            request.Exemptions.Select(e => new GuestExemption(e.Position, e.Code)).ToList(),
            userId,
            cancellationToken);
        await ReconcileCollectionAsync(bookingId, cancellationToken);
        return Ok(StayTouristTaxDto.From(view, await CanReadGuestsAsync(bookingId, cancellationToken)));
    }

    /// <summary>Who collected the tax of a channel stay whose comune has no channel rule; 422 <c>tax_collector_not_applicable</c> otherwise.</summary>
    [HttpPut("bookings/{bookingId:guid}/tourist-tax/collector")]
    [Authorize(Policy = CasazenPolicies.BookingWrite)]
    [ProducesResponseType(typeof(StayTouristTaxDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<StayTouristTaxDto>> SetCollector(
        Guid bookingId,
        [FromBody] SetTouristTaxCollectorRequest request,
        CancellationToken cancellationToken)
    {
        if (!await CanUseBookingAsync(bookingId, BookingOperations.Write, cancellationToken))
            return BookingNotFound();
        if (User.GetUserId() is not { Length: > 0 } userId)
            return Unauthorized();

        var view = await stayTouristTax.SetCollectorAsync(bookingId, request.Collector, userId, cancellationToken);
        await ReconcileCollectionAsync(bookingId, cancellationToken);
        return Ok(StayTouristTaxDto.From(view, await CanReadGuestsAsync(bookingId, cancellationToken)));
    }

    [HttpPost("bookings/{bookingId:guid}/tourist-tax/confirm")]
    [Authorize(Policy = CasazenPolicies.BookingWrite)]
    [ProducesResponseType(typeof(StayTouristTaxDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<StayTouristTaxDto>> Confirm(Guid bookingId, CancellationToken cancellationToken)
    {
        if (!await CanUseBookingAsync(bookingId, BookingOperations.Write, cancellationToken))
            return BookingNotFound();
        if (User.GetUserId() is not { Length: > 0 } userId)
            return Unauthorized();

        var view = await stayTouristTax.ConfirmAsync(bookingId, userId, cancellationToken);
        await ReconcileCollectionAsync(bookingId, cancellationToken);
        return Ok(StayTouristTaxDto.From(view, await CanReadGuestsAsync(bookingId, cancellationToken)));
    }

    [HttpGet("properties/{propertyId:guid}/tourist-tax-rate")]
    [Authorize(Policy = CasazenPolicies.PropertyRead)]
    [ProducesResponseType(typeof(MunicipalityRateDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<MunicipalityRateDto>> GetMunicipalityRate(Guid propertyId, CancellationToken cancellationToken)
    {
        if (!await CanUsePropertyAsync(propertyId, PropertyOperations.Read, cancellationToken))
            return PropertyNotFound();

        return Ok(MunicipalityRateDto.From(await stayTouristTax.GetMunicipalityRateAsync(propertyId, cancellationToken)));
    }

    [HttpPut("properties/{propertyId:guid}/tourist-tax-rate")]
    [Authorize(Policy = CasazenPolicies.PropertyWrite)]
    [ProducesResponseType(typeof(MunicipalityRateDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<MunicipalityRateDto>> SaveMunicipalityRate(
        Guid propertyId,
        [FromBody] SaveMunicipalityRateRequest request,
        CancellationToken cancellationToken)
    {
        if (!await CanUsePropertyAsync(propertyId, PropertyOperations.Write, cancellationToken))
            return PropertyNotFound();
        if (User.GetUserId() is not { Length: > 0 } userId)
            return Unauthorized();

        var view = await stayTouristTax.SaveMunicipalityRateAsync(propertyId, request.ToInput(), userId, cancellationToken);
        return Ok(MunicipalityRateDto.From(view));
    }

    private async Task<bool> CanUseBookingAsync(Guid bookingId, HostOperationRequirement operation, CancellationToken cancellationToken)
    {
        var resource = await hostResources.ForBookingAsync(bookingId, cancellationToken);
        if (resource is not null && await authorizationService.IsAuthorizedAsync(User, resource, operation))
            return true;

        logger.LogWarning(
            "User {UserId} denied {Permission} on the tourist tax of booking {BookingId}", User.GetUserId(), operation.PermissionKey, bookingId);
        return false;
    }

    /// <summary>
    /// Whether the caller holds <c>guest.read</c> on the property of the booking. The reason of an exemption can reveal a health condition (art. 9
    /// GDPR): like the full document numbers it is shown only to them, and is <c>null</c> for the others (the accountant role holds
    /// <c>booking.read</c> without <c>guest.read</c>).
    /// </summary>
    private async Task<bool> CanReadGuestsAsync(Guid bookingId, CancellationToken cancellationToken)
    {
        var resource = await hostResources.ForBookingAsync(bookingId, cancellationToken);
        return resource is not null && await authorizationService.IsAuthorizedAsync(User, resource, GuestOperations.Read);
    }

    private async Task<bool> CanUsePropertyAsync(Guid propertyId, HostOperationRequirement operation, CancellationToken cancellationToken)
    {
        var resource = await hostResources.ForPropertyAsync(propertyId, cancellationToken);
        if (resource is not null && await authorizationService.IsAuthorizedAsync(User, resource, operation))
            return true;

        logger.LogWarning(
            "User {UserId} denied {Permission} on the tourist tax rate of property {PropertyId}", User.GetUserId(), operation.PermissionKey, propertyId);
        return false;
    }

    private Task ReconcileCollectionAsync(Guid bookingId, CancellationToken cancellationToken) =>
        touristTaxCollection is null ? Task.CompletedTask : touristTaxCollection.ReconcileAfterChangeAsync(bookingId, cancellationToken);

    private ObjectResult BookingNotFound() =>
        this.ApiProblem(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "BookingNotFound");

    private ObjectResult PropertyNotFound() =>
        this.ApiProblem(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "PropertyNotFound");

    private ObjectResult OrganizationNotFound() =>
        this.ApiProblem(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "OrganizationNotFound");
}
