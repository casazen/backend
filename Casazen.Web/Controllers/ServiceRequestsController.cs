using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Core.Validation;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs.ServiceRequests;
using Casazen.Web.DTOs.Supplier;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Casazen.Web.Controllers;

/// <summary>
/// Service requests (TN-3). Host side, short-rent context: <c>property.read</c> / <c>property.write</c>, then the
/// property is authorized as a <see cref="HostResource"/> (org, permission, ownership). Supplier side:
/// <see cref="CasazenPolicies.Supplier"/> plus the linked supplier org. <c>GET</c> list and detail serve both sides and
/// evaluate the policy of the branch they take.
/// </summary>
/// <remarks>
/// Decision D2 (SU-07): the host side here is the short-rent one, where a request is for one stay: <c>POST</c> needs a
/// <c>bookingId</c> of the property, and the host endpoints reach only <see cref="ServiceRequestRentalContext.ShortRent"/>
/// requests. Web and app list them the same way: <c>?bookingId=</c> for a stay, <c>?propertyId=</c> for a property.
/// Long-rent requests (per property) live in <see cref="LongRentServiceRequestsController"/>.
/// </remarks>
[ApiController]
[Route("api/service-requests")]
[Authorize(Policy = CasazenPolicies.Authenticated)]
public class ServiceRequestsController(
    IServiceRequestService serviceRequestService,
    ISupplierMatchService supplierMatchService,
    IHostResourceLookup hostResources,
    IAuthorizationService authorizationService,
    IOrgContextResolver orgContextResolver,
    ISupplierOrgContextResolver supplierOrgContextResolver) : ControllerBase
{
    /// <summary>
    /// AI-assisted supplier match (D11: behind <see cref="FeatureFlags.AiSupplierDiscovery"/>, off by default, 404 while
    /// off). Rate limited per user and org; the category must be one of <see cref="ServiceCategories.All"/>; the host's
    /// notes are not accepted. The manual request (<c>POST api/service-requests</c>) does not depend on it.
    /// </summary>
    [HttpPost("match-supplier")]
    [FeatureGate(FeatureFlags.AiSupplierDiscovery)]
    [Authorize(Policy = CasazenPolicies.PropertyWrite)]
    [AiRateLimit]
    [ProducesResponseType(typeof(SupplierMatchResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<SupplierMatchResponse>> MatchSupplier(
        [FromBody] MatchSupplierRequest request,
        CancellationToken cancellationToken)
    {
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken);
        if (orgId is null) return Unauthorized();

        if (await AuthorizePropertyAsync(request.PropertyId, PropertyOperations.Write, cancellationToken) is { } denied)
            return denied;

        var result = await supplierMatchService.MatchAsync(
            orgId.Value,
            request.PropertyId,
            request.Category,
            request.Urgency,
            cancellationToken);
        return Ok(MapMatchResult(result));
    }

    /// <summary>
    /// Sends a short-rent request for a stay of the property to a supplier. 400 <c>validation_error</c> for a malformed
    /// body (empty ids, notes over 1000 characters, no category); 404 <c>property_not_found</c> /
    /// <c>supplier_not_found</c>; 422 for the rules (category code, stay of the property, supplier active and covering
    /// the comune, <c>chargeToGuest</c>), with the codes of <see cref="ServiceRequestErrorCodes"/>.
    /// </summary>
    [HttpPost]
    [Authorize(Policy = CasazenPolicies.PropertyWrite)]
    [ProducesResponseType(typeof(ServiceRequestDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<ServiceRequestDto>> Create(
        [FromBody] CreateServiceRequestRequest request,
        CancellationToken cancellationToken)
    {
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken);
        var userId = User.GetUserId();
        if (orgId is null || userId is null) return Unauthorized();

        if (await AuthorizePropertyAsync(request.PropertyId, PropertyOperations.Write, cancellationToken) is { } denied)
            return denied;

        var created = await serviceRequestService.CreateAsync(
            new CreateServiceRequestCommand(
                orgId.Value,
                userId,
                request.PropertyId,
                request.BookingId,
                request.SupplierOrgId,
                request.Category,
                request.Urgency,
                request.Notes,
                request.ChargeToGuest,
                ServiceRequestRentalContext.ShortRent,
                request.ServiceListingId,
                request.ScheduledStartUtc),
            cancellationToken);

        return CreatedAtAction(nameof(GetById), new { id = created.Id }, MapDto(created));
    }

    /// <summary>
    /// Host list of short-rent requests (<c>property.read</c>), or the supplier inbox with <c>view=supplier</c>
    /// (<see cref="CasazenPolicies.Supplier"/>). <c>bookingId</c> lists one stay's requests, <c>propertyId</c> a
    /// property's (every stay, plus the older requests not traced to a stay): a booking or property that is not in the
    /// caller's org is 404, one the caller may not read is 403.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(ServiceRequestListResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ServiceRequestListResponse>> List(
        [FromQuery] string? status,
        [FromQuery] Guid? propertyId,
        [FromQuery] Guid? bookingId,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] string? view = null,
        CancellationToken cancellationToken = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        // Enum.TryParse alone also accepts a numeric string with no declared member (e.g. "99"): filtering by it
        // would silently match nothing instead of leaving the filter unapplied like any other unknown value (PL-07).
        ServiceRequestStatus? statusFilter = null;
        if (EnumNames.TryParseDefined<ServiceRequestStatus>(status, out var parsed))
            statusFilter = parsed;

        if (string.Equals(view, "supplier", StringComparison.OrdinalIgnoreCase))
        {
            if (!await SatisfiesAsync(CasazenPolicies.Supplier))
                return Forbid();

            var supplierOrgId = await supplierOrgContextResolver.GetLinkedSupplierOrgIdAsync(cancellationToken);
            if (supplierOrgId is null) return NotFound();

            var openOnly = string.Equals(status, "open", StringComparison.OrdinalIgnoreCase);
            var (items, total) = await serviceRequestService.ListForSupplierAsync(
                supplierOrgId.Value, openOnly, page, pageSize, cancellationToken);

            // The supplier reads its requests through the supplier mapping: before the take, no property name and no notes (D9).
            return Ok(new ServiceRequestListResponse
            {
                Items = items.Select(MapDtoForSupplier),
                Total = total,
                Page = page,
                PageSize = pageSize,
            });
        }

        if (!await SatisfiesAsync(CasazenPolicies.PropertyRead))
            return Forbid();

        var scope = await GetHostScopeAsync(cancellationToken);
        if (scope is null) return Unauthorized();

        if (await AuthorizeListFiltersAsync(propertyId, bookingId, cancellationToken) is { } denied)
            return denied;

        var (hostItems, hostTotal) = await serviceRequestService.ListForHostAsync(
            scope, ServiceRequestRentalContext.ShortRent, statusFilter, propertyId, bookingId, page, pageSize, cancellationToken);

        return Ok(new ServiceRequestListResponse
        {
            Items = hostItems.Select(MapDto),
            Total = hostTotal,
            Page = page,
            PageSize = pageSize,
        });
    }

    /// <summary>
    /// A request of the caller's supplier org (<see cref="CasazenPolicies.Supplier"/>) or of the caller's host scope
    /// (<c>property.read</c>); 404 when it is in neither.
    /// </summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(ServiceRequestDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ServiceRequestDto>> GetById(Guid id, CancellationToken cancellationToken)
    {
        var isSupplier = await SatisfiesAsync(CasazenPolicies.Supplier);
        if (isSupplier)
        {
            var supplierOrgId = await supplierOrgContextResolver.GetLinkedSupplierOrgIdAsync(cancellationToken);
            if (supplierOrgId is not null)
            {
                var supplierRequest = await serviceRequestService.GetByIdForSupplierAsync(id, supplierOrgId.Value, cancellationToken);
                if (supplierRequest is not null)
                    return Ok(MapDtoForSupplier(supplierRequest));
            }
        }

        if (!await SatisfiesAsync(CasazenPolicies.PropertyRead))
            return isSupplier ? ServiceRequestNotFound() : Forbid();

        var scope = await GetHostScopeAsync(cancellationToken);
        if (scope is null) return Unauthorized();

        var hostRequest = await serviceRequestService.GetByIdForHostAsync(
            id, scope, ServiceRequestRentalContext.ShortRent, cancellationToken);
        if (hostRequest is null) return ServiceRequestNotFound();

        return Ok(MapDto(hostRequest));
    }

    /// <summary>
    /// Supplier transitions (<see cref="CasazenPolicies.Supplier"/>, linked supplier org), following
    /// <see cref="Casazen.Core.Suppliers.ServiceRequestStateMachine"/>: 404 <c>service_request_not_found</c>, 403 for a
    /// request sent to another supplier, 422 <c>service_request_invalid_transition</c> from a status that does not allow
    /// it, 409 <c>service_request_state_changed</c> when a concurrent operation changed the request first (A4-19).
    /// </summary>
    [HttpPost("{id:guid}/take")]
    [Authorize(Policy = CasazenPolicies.Supplier)]
    [ProducesResponseType(typeof(ServiceRequestDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<ServiceRequestDto>> Take(
        Guid id,
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] TakeServiceRequestRequest? request,
        CancellationToken cancellationToken)
    {
        var supplierOrgId = await supplierOrgContextResolver.GetLinkedSupplierOrgIdAsync(cancellationToken);
        var userId = User.GetUserId();
        if (supplierOrgId is null || userId is null) return NotFound();

        // No body (or an empty one) accepts the request as it is; the time and the price are what the supplier adds (SP-04).
        var command = request is null
            ? null
            : new TakeServiceRequestCommand(request.ScheduledStartUtc, request.ScheduledEndUtc, request.QuotedAmountCents);
        var updated = await serviceRequestService.TakeAsync(id, supplierOrgId.Value, userId, command, cancellationToken);
        return Ok(MapDtoForSupplier(updated));
    }

    /// <summary>
    /// The supplier starts the work on a request it took (<c>PresoInCarico → InCorso</c>, SP-04); the host is told. Same errors as
    /// <see cref="Take"/>; 422 <c>service_request_invalid_transition</c> unless the request is taken and not started.
    /// </summary>
    [HttpPost("{id:guid}/start")]
    [Authorize(Policy = CasazenPolicies.Supplier)]
    [ProducesResponseType(typeof(ServiceRequestDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<ServiceRequestDto>> Start(Guid id, CancellationToken cancellationToken)
    {
        var supplierOrgId = await supplierOrgContextResolver.GetLinkedSupplierOrgIdAsync(cancellationToken);
        if (supplierOrgId is null) return NotFound();

        var updated = await serviceRequestService.StartAsync(id, supplierOrgId.Value, cancellationToken);
        return Ok(MapDtoForSupplier(updated));
    }

    /// <summary>
    /// The supplier completes a request it took (SP-04): <c>notes</c> are what it leaves for the host (their own field,
    /// <c>completionNotes</c>: the host's notes are no longer replaced, 400 over 1000 characters), <c>finalAmountCents</c> the
    /// total it asks and <c>extras</c> the lines on top of the agreed price (at most 10). A total more than 20 % above the quote
    /// is flagged <c>price.needsCustomerConfirmation</c> (decision D7). The photos are uploaded before, with
    /// <c>POST {id}/photos</c>. Same errors as <see cref="Take"/>; 422 <c>service_request_amount_invalid</c> /
    /// <c>service_request_final_amount_invalid</c> for the amounts.
    /// </summary>
    [HttpPost("{id:guid}/complete")]
    [Authorize(Policy = CasazenPolicies.Supplier)]
    [ProducesResponseType(typeof(ServiceRequestDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<ServiceRequestDto>> Complete(
        Guid id,
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] CompleteServiceRequestRequest? request,
        CancellationToken cancellationToken)
    {
        var supplierOrgId = await supplierOrgContextResolver.GetLinkedSupplierOrgIdAsync(cancellationToken);
        if (supplierOrgId is null) return NotFound();

        var command = request is null
            ? null
            : new CompleteServiceRequestCommand(
                request.Notes,
                request.FinalAmountCents,
                request.Extras?.Select(extra => new ServiceRequestExtra(extra.Label, extra.AmountCents)).ToList());
        var updated = await serviceRequestService.CompleteAsync(id, supplierOrgId.Value, command, cancellationToken);
        return Ok(MapDtoForSupplier(updated));
    }

    /// <summary>
    /// The supplier adds photos of the work to a request it took and has not completed (multipart field <c>photos</c>, JPEG, PNG
    /// or WebP checked on their content, at most 6 per request, 10 MB each). All or none. The files are private: the host and
    /// the supplier read them with <c>GET {id}/photos/{photoId}</c>. 422 <c>service_request_photo_invalid</c> /
    /// <c>service_request_photo_limit_reached</c>; 413 for a request larger than 6 files of 10 MB.
    /// </summary>
    [HttpPost("{id:guid}/photos")]
    [Authorize(Policy = CasazenPolicies.Supplier)]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(ServiceRequestLimits.MaxUploadRequestBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = ServiceRequestLimits.MaxUploadRequestBytes)]
    [ProducesResponseType(typeof(ServiceRequestDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<ServiceRequestDto>> UploadPhotos(
        Guid id,
        [FromForm] List<IFormFile> photos,
        CancellationToken cancellationToken)
    {
        var supplierOrgId = await supplierOrgContextResolver.GetLinkedSupplierOrgIdAsync(cancellationToken);
        if (supplierOrgId is null) return NotFound();

        if (photos is not { Count: > 0 })
            return this.ApiProblem(StatusCodes.Status400BadRequest, ProblemCodes.ValidationError, "ServiceRequestPhotosRequired");

        var updated = await serviceRequestService.AddWorkPhotosAsync(id, supplierOrgId.Value, photos, cancellationToken);
        return Ok(MapDtoForSupplier(updated));
    }

    /// <summary>
    /// A photo of the work (SP-04), for the supplier it was sent to or for the host of the request (<c>property.read</c>,
    /// short-rent). Private file: only here, never cached; 404 <c>service_request_not_found</c> for a request outside the
    /// caller's scope, 404 <c>service_request_photo_not_found</c> for a photo the request does not have.
    /// </summary>
    [HttpGet("{id:guid}/photos/{photoId:guid}")]
    [ProducesResponseType(typeof(FileStreamResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetPhoto(Guid id, Guid photoId, CancellationToken cancellationToken)
    {
        ServiceRequest? request = null;
        var isSupplier = await SatisfiesAsync(CasazenPolicies.Supplier);
        if (isSupplier)
        {
            var supplierOrgId = await supplierOrgContextResolver.GetLinkedSupplierOrgIdAsync(cancellationToken);
            if (supplierOrgId is not null)
                request = await serviceRequestService.GetByIdForSupplierAsync(id, supplierOrgId.Value, cancellationToken);
        }

        if (request is null)
        {
            if (!await SatisfiesAsync(CasazenPolicies.PropertyRead))
                return isSupplier ? ServiceRequestNotFound() : Forbid();

            var scope = await GetHostScopeAsync(cancellationToken);
            if (scope is null) return Unauthorized();

            request = await serviceRequestService.GetByIdForHostAsync(id, scope, ServiceRequestRentalContext.ShortRent, cancellationToken);
        }

        return await ServiceRequestPhotoResults.ForAsync(this, serviceRequestService, request, photoId, cancellationToken);
    }

    /// <summary>
    /// The supplier refuses a new request: the reason is required, at most 500 characters (400 otherwise, A4-18). Same
    /// errors as <see cref="Take"/>.
    /// </summary>
    [HttpPost("{id:guid}/reject")]
    [Authorize(Policy = CasazenPolicies.Supplier)]
    [ProducesResponseType(typeof(ServiceRequestDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<ServiceRequestDto>> Reject(
        Guid id,
        [FromBody] RejectServiceRequestRequest request,
        CancellationToken cancellationToken)
    {
        var supplierOrgId = await supplierOrgContextResolver.GetLinkedSupplierOrgIdAsync(cancellationToken);
        if (supplierOrgId is null) return NotFound();

        var updated = await serviceRequestService.RejectAsync(
            id, supplierOrgId.Value, request.Reason, cancellationToken);
        return Ok(MapDtoForSupplier(updated));
    }

    /// <summary>
    /// Cancels a request (SP-04), with a reason (required, at most 500 characters, 400 otherwise) shown to the other party, who is
    /// told. Serves both sides and evaluates the policy of the branch it takes: the <b>supplier</b> it was sent to
    /// (<see cref="CasazenPolicies.Supplier"/>) cancels before the work started (<c>Richiesto</c>, <c>PresoInCarico</c>); the
    /// <b>host</b> (<c>property.write</c> on the property, short-rent) cancels up to and including the work in progress. 422
    /// <c>service_request_invalid_transition</c> from any other status (the work is done, rejected, paid, already cancelled); 404
    /// <c>service_request_not_found</c> outside the caller's scope; 409 <c>service_request_state_changed</c> on a concurrent change.
    /// </summary>
    [HttpPost("{id:guid}/cancel")]
    [ProducesResponseType(typeof(ServiceRequestDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<ServiceRequestDto>> Cancel(
        Guid id,
        [FromBody] CancelServiceRequestRequest request,
        CancellationToken cancellationToken)
    {
        var isSupplier = await SatisfiesAsync(CasazenPolicies.Supplier);
        if (isSupplier)
        {
            var supplierOrgId = await supplierOrgContextResolver.GetLinkedSupplierOrgIdAsync(cancellationToken);
            if (supplierOrgId is not null
                && await serviceRequestService.GetByIdForSupplierAsync(id, supplierOrgId.Value, cancellationToken) is not null)
            {
                var cancelled = await serviceRequestService.CancelAsSupplierAsync(id, supplierOrgId.Value, request.Reason, cancellationToken);
                return Ok(MapDtoForSupplier(cancelled));
            }
        }

        if (!await SatisfiesAsync(CasazenPolicies.PropertyWrite))
            return isSupplier ? ServiceRequestNotFound() : Forbid();

        return await HostActionAsync(
            id, hostOrgId => serviceRequestService.CancelAsHostAsync(id, hostOrgId, request.Reason, cancellationToken), cancellationToken);
    }

    /// <summary>
    /// The supplier proposes another time for a new request (SP-04): the proposal is saved and the host told, and the request
    /// stays <c>Richiesto</c> until the host answers (<c>POST {id}/proposal/accept</c> or <c>reject</c>). A new proposal replaces
    /// the old one. The time is checked with the supplier's agenda (409 <c>supplier_slot_unavailable</c>); 422
    /// <c>service_request_invalid_transition</c> unless the request is new, <c>service_request_time_invalid</c> when the length of
    /// the work is unknown. Same errors as <see cref="Take"/>.
    /// </summary>
    [HttpPost("{id:guid}/propose-time")]
    [Authorize(Policy = CasazenPolicies.Supplier)]
    [ProducesResponseType(typeof(ServiceRequestDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<ServiceRequestDto>> ProposeTime(
        Guid id,
        [FromBody] ProposeServiceRequestTimeRequest request,
        CancellationToken cancellationToken)
    {
        var supplierOrgId = await supplierOrgContextResolver.GetLinkedSupplierOrgIdAsync(cancellationToken);
        var userId = User.GetUserId();
        if (supplierOrgId is null || userId is null) return NotFound();

        var updated = await serviceRequestService.ProposeTimeAsync(
            id,
            supplierOrgId.Value,
            userId,
            new ProposeServiceRequestTimeCommand(request.StartUtc!.Value, request.EndUtc, request.Message),
            cancellationToken);
        return Ok(MapDtoForSupplier(updated));
    }

    /// <summary>
    /// The host accepts the time the supplier proposed (SP-04): it becomes the time of the request, which the supplier member who
    /// proposed it takes (<c>Richiesto → PresoInCarico</c>); the supplier is told. <c>property.write</c> on the property. The time
    /// is checked again with the supplier's agenda (409 <c>supplier_slot_unavailable</c>); 422 <c>service_request_no_proposal</c>
    /// when there is none to answer; 404 <c>service_request_not_found</c> outside the caller's scope.
    /// </summary>
    [HttpPost("{id:guid}/proposal/accept")]
    [Authorize(Policy = CasazenPolicies.PropertyWrite)]
    [ProducesResponseType(typeof(ServiceRequestDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public Task<ActionResult<ServiceRequestDto>> AcceptProposal(Guid id, CancellationToken cancellationToken) =>
        HostActionAsync(id, hostOrgId => serviceRequestService.AcceptProposalAsync(id, hostOrgId, cancellationToken), cancellationToken);

    /// <summary>
    /// The host turns the proposed time down (SP-04): the request stays <c>Richiesto</c> as it was and the supplier is told, who
    /// can still take it as it is, propose another time or reject it. Same errors as <see cref="AcceptProposal"/>.
    /// </summary>
    [HttpPost("{id:guid}/proposal/reject")]
    [Authorize(Policy = CasazenPolicies.PropertyWrite)]
    [ProducesResponseType(typeof(ServiceRequestDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public Task<ActionResult<ServiceRequestDto>> RejectProposal(Guid id, CancellationToken cancellationToken) =>
        HostActionAsync(id, hostOrgId => serviceRequestService.RejectProposalAsync(id, hostOrgId, cancellationToken), cancellationToken);

    /// <summary>
    /// The host reminds the supplier to answer a new request (SP-04): the supplier is told by email and push. At most one
    /// reminder every 6 hours (422 <c>service_request_remind_too_soon</c>); only while the request is new and the supplier has
    /// not proposed a time (422 <c>service_request_invalid_transition</c>). <c>property.write</c> on the property.
    /// </summary>
    [HttpPost("{id:guid}/remind")]
    [Authorize(Policy = CasazenPolicies.PropertyWrite)]
    [ProducesResponseType(typeof(ServiceRequestDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public Task<ActionResult<ServiceRequestDto>> Remind(Guid id, CancellationToken cancellationToken) =>
        HostActionAsync(id, hostOrgId => serviceRequestService.RemindAsync(id, hostOrgId, cancellationToken), cancellationToken);

    /// <summary>
    /// Runs a host action on a short-rent request of the caller's scope: 404 for a request outside it (or a long-rent one), 403
    /// when the caller may not write the request's property (<c>property.write</c> as a <see cref="HostResource"/>), then
    /// <paramref name="action"/> with the host org.
    /// </summary>
    private async Task<ActionResult<ServiceRequestDto>> HostActionAsync(
        Guid id,
        Func<Guid, Task<ServiceRequest>> action,
        CancellationToken cancellationToken)
    {
        var scope = await GetHostScopeAsync(cancellationToken);
        if (scope is null) return Unauthorized();

        var existing = await serviceRequestService.GetByIdForHostAsync(
            id, scope, ServiceRequestRentalContext.ShortRent, cancellationToken);
        if (existing is null) return ServiceRequestNotFound();

        var resource = new HostResource(existing.OrgId, existing.Property?.OwnerId);
        if (existing.Property is null ||
            !await authorizationService.IsAuthorizedAsync(User, resource, PropertyOperations.Write))
            return Forbid();

        return Ok(MapDto(await action(scope.OrgId)));
    }

    /// <summary>
    /// Host marks a completed short-rent request as paid (manual flag, no Stripe transfer): <c>property.write</c> on the
    /// request's property. A request outside the caller's host scope, or a long-rent one, is 404
    /// <c>service_request_not_found</c>; a request that is not completed is 422
    /// <c>service_request_invalid_transition</c>; 409 <c>service_request_state_changed</c> on a concurrent change.
    /// </summary>
    [HttpPost("{id:guid}/mark-paid")]
    [Authorize(Policy = CasazenPolicies.PropertyWrite)]
    [ProducesResponseType(typeof(ServiceRequestDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<ServiceRequestDto>> MarkPaid(Guid id, CancellationToken cancellationToken)
    {
        var scope = await GetHostScopeAsync(cancellationToken);
        if (scope is null) return Unauthorized();

        var existing = await serviceRequestService.GetByIdForHostAsync(
            id, scope, ServiceRequestRentalContext.ShortRent, cancellationToken);
        if (existing is null) return ServiceRequestNotFound();

        var resource = new HostResource(existing.OrgId, existing.Property?.OwnerId);
        if (existing.Property is null ||
            !await authorizationService.IsAuthorizedAsync(User, resource, PropertyOperations.Write))
            return Forbid();

        var updated = await serviceRequestService.MarkPaidAsync(id, scope.OrgId, cancellationToken);
        return Ok(MapDto(updated));
    }

    /// <summary>404 <c>service_request_not_found</c> (FD-05), also for a request outside the caller's scope.</summary>
    internal static ObjectResult ServiceRequestNotFound(ControllerBase controller) =>
        controller.ApiProblem(
            StatusCodes.Status404NotFound, ServiceRequestErrorCodes.NotFound, ServiceRequestErrorCodes.NotFoundMessageKey);

    private ObjectResult ServiceRequestNotFound() => ServiceRequestNotFound(this);

    private async Task<bool> SatisfiesAsync(string policy) =>
        (await authorizationService.AuthorizeAsync(User, policy)).Succeeded;

    private async Task<HostScope?> GetHostScopeAsync(CancellationToken cancellationToken)
    {
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken);
        return orgId is null ? null : User.GetHostScope(orgId.Value);
    }

    /// <summary>
    /// The stay and the property a host list filters on (SU-07): 404 when not in the caller's org, 403 when the caller
    /// may not read them.
    /// </summary>
    private async Task<ActionResult?> AuthorizeListFiltersAsync(
        Guid? propertyId,
        Guid? bookingId,
        CancellationToken cancellationToken)
    {
        if (bookingId is { } stayId)
        {
            var booking = await hostResources.ForBookingAsync(stayId, cancellationToken);
            if (booking is null)
                return this.ApiProblem(StatusCodes.Status404NotFound, "booking_not_found", "BookingNotFound");

            if (!await authorizationService.IsAuthorizedAsync(User, booking, PropertyOperations.Read))
                return Forbid();
        }

        return propertyId is { } id
            ? await AuthorizePropertyAsync(id, PropertyOperations.Read, cancellationToken)
            : null;
    }

    /// <summary>404 when the property is not in the caller's org, 403 when the operation is not allowed on it.</summary>
    private async Task<ActionResult?> AuthorizePropertyAsync(
        Guid propertyId,
        HostOperationRequirement operation,
        CancellationToken cancellationToken)
    {
        var property = await hostResources.ForPropertyAsync(propertyId, cancellationToken);
        if (property is null)
            return this.ApiProblem(StatusCodes.Status404NotFound, "property_not_found", "PropertyNotFound");

        return await authorizationService.IsAuthorizedAsync(User, property, operation) ? null : Forbid();
    }

    /// <summary>The request as its host sees it: everything the host wrote and the supplier did.</summary>
    internal static ServiceRequestDto MapDto(ServiceRequest r) => Map(r, forSupplier: false);

    /// <summary>
    /// The request as the supplier it was sent to sees it, in the shape of the host endpoints (the answers of its own actions and
    /// the legacy <c>GET api/service-requests/{id}</c> and <c>?view=supplier</c>): until the supplier takes the request, no name of
    /// the property and no host notes (decision D9, <see cref="SupplierJobDisclosure"/>).
    /// </summary>
    internal static ServiceRequestDto MapDtoForSupplier(ServiceRequest r) => Map(r, forSupplier: true);

    private static ServiceRequestDto Map(ServiceRequest r, bool forSupplier)
    {
        var disclosed = !forSupplier || SupplierJobDisclosure.IsDisclosed(r.Status);
        // The supplier reads the photos at the shared endpoint; a host at the one of its rental context.
        var photosPath = forSupplier || r.RentalContext == ServiceRequestRentalContext.ShortRent
            ? ServiceRequestDtoParts.ShortRentBasePath
            : ServiceRequestDtoParts.LongRentBasePath;

        return new ServiceRequestDto
        {
            Id = r.Id,
            OrgId = r.OrgId,
            BookingId = r.BookingId,
            RentalContext = r.RentalContext.ToString(),
            PropertyId = r.PropertyId,
            PropertyName = disclosed ? r.Property?.Name : null,
            SupplierOrgId = r.SupplierOrgId,
            SupplierName = r.SupplierOrg?.DisplayName ?? r.SupplierOrg?.Name,
            Category = r.Category,
            Urgency = r.Urgency.ToString(),
            Notes = disclosed && !string.IsNullOrWhiteSpace(r.Notes) ? r.Notes : null,
            Status = r.Status.ToString(),
            TakenAt = r.TakenAt,
            TakenByUserId = r.TakenByUserId,
            CompletedAt = r.CompletedAt,
            PaidAt = r.PaidAt,
            ChargeToGuest = r.ChargeToGuest,
            RejectionReason = r.RejectionReason,
            CreatedAt = r.CreatedAt,
            UpdatedAt = r.UpdatedAt,
            History = ServiceRequestHistory
                .Build(
                    new ServiceRequestMilestones(
                        r.Status,
                        r.CreatedAt,
                        r.UpdatedAt,
                        r.TakenAt,
                        r.CompletedAt,
                        r.PaidAt,
                        r.RejectionReason,
                        r.StartedAt,
                        r.CancelledAt,
                        r.CancellationReason,
                        r.CancelledBy,
                        r.RentalContext == ServiceRequestRentalContext.Showcase ? ServiceRequestActorParty.Customer : ServiceRequestActorParty.Host),
                    takenByName: null)
                .Select(h => ServiceRequestHistoryEntryDto.From(h))
                .ToList(),
            Source = SupplierRequestSources.Of(r.Source),
            ServiceListingId = r.ServiceListingId,
            ServiceName = r.ServiceNameSnapshot,
            ScheduledStart = r.ScheduledStartUtc,
            ScheduledEnd = r.ScheduledEndUtc,
            RespondBy = r.Status == ServiceRequestStatus.Richiesto ? r.ResponseDueAt : null,
            StartedAt = r.StartedAt,
            Price = ServiceRequestDtoParts.ToPriceDto(
                r.EstimatedAmountCents,
                r.QuotedAmountCents,
                r.FinalAmountCents,
                r.FinalAmountNeedsConfirmation,
                ServiceRequestJson.ReadPriceLines(r.PriceLinesJson)),
            CancelledAt = r.CancelledAt,
            CancelledBy = r.CancelledBy?.ToString(),
            CancellationReason = r.CancellationReason,
            CompletionNotes = r.CompletionNotes,
            WorkPhotos = ServiceRequestDtoParts.ToPhotoDtos(r.Id, ServiceRequestJson.ReadPhotos(r.WorkPhotosJson), photosPath),
            Proposal = ServiceRequestDtoParts.ToProposalDto(r.ProposedStartUtc, r.ProposedEndUtc, r.ProposedAt, r.ProposalMessage),
            LastRemindedAt = r.LastRemindedAt,
        };
    }

    private static SupplierMatchResponse MapMatchResult(SupplierMatchResult result) => new()
    {
        Recommended = result.Recommended is null ? null : MapCandidate(result.Recommended),
        Alternatives = result.Alternatives.Select(MapCandidate),
        ExternalSuggestions = result.ExternalSuggestions.Select(e => new ExternalSupplierSuggestionDto
        {
            Name = e.Name,
            Address = e.Address,
            Phone = e.Phone,
            Email = e.Email,
            Rating = e.Rating,
            ReviewCount = e.ReviewCount,
            GoogleMapsUrl = e.GoogleMapsUrl,
            WebsiteUrl = e.WebsiteUrl,
            Source = e.Source,
        }),
        UsedExternalFallback = result.UsedExternalFallback,
    };

    private static SupplierMatchCandidateDto MapCandidate(SupplierMatchCandidate c) => new()
    {
        OrgId = c.OrgId,
        LegalName = c.LegalName,
        Phone = c.Phone,
        Email = c.Email,
        Bio = c.Bio,
        MatchScore = c.MatchScore,
        MatchReason = c.MatchReason,
        Source = c.Source,
        ReasonGeneratedByAi = c.ReasonGeneratedByAi,
    };
}
