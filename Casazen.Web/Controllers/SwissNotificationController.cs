using Casazen.Core.Authorization;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs.SwissCompliance;
using Casazen.Web.Infrastructure;
using Casazen.Web.Resources;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;

namespace Casazen.Web.Controllers;

/// <summary>
/// The assisted notification of the guests of a property in a canton other than Ticino to the cantonal authority (E31/F02/US01/T01, NF-131, decisions D1, D5, D12, casazen/backend#979):
/// the arrival day as data ready to copy onto the official portal, and the declaration that the host made the notification. CasaZen never transmits and never logs into a portal. Ticino has its own
/// routes (<see cref="SwissComplianceController"/>) and never appears here.
/// </summary>
/// <remarks>
/// <para><b>Behind <c>Features:GuestRegistrationRegimes</c></b> (off by default): off, the three actions answer 404 <c>not_found</c> before authentication and nothing is read.</para>
/// <para><b>Who may call</b> (the rules of the Ticino routes). The class asks <c>booking.read</c>; the day lists the guests by name, with the number of their document and their address, so it also asks
/// <c>guest.read</c>; declaring (or undoing) a day declares a legal communication done, so it asks <c>alloggiati.submit</c> (the owner and the property managers; not a collaborator). Then the
/// property is authorized as a row (TN-3): one of another org is a 404, one the caller's scope does not reach (a collaborator «Solo alcuni» not given it) is a 403, and nothing is read or written for
/// either. The answers that hold guest data are <c>private, no-store</c>.</para>
/// <para><b>Errors</b> are ProblemDetails with a stable <c>code</c>: 400 <c>validation_error</c> (the day is missing or is not a date); 401; 403; 404 <c>property_not_found</c>; 422
/// <c>swiss_notification_not_applicable</c> (the property is not Swiss, is in Ticino, its canton asks only for a register or its place is not known) and
/// <c>swiss_declaration_day_in_future</c> (the day has not started).</para>
/// </remarks>
[ApiController]
[Route("api/properties/{propertyId:guid}/swiss")]
[FeatureGate(FeatureFlags.GuestRegistrationRegimes)]
[Authorize(Policy = CasazenPolicies.BookingRead)]
public sealed class SwissNotificationController(
    ISwissNotificationService swissNotifications,
    IHostResourceLookup hostResources,
    IAuthorizationService authorizationService,
    IStringLocalizer<SharedResources> localizer,
    ILogger<SwissNotificationController> logger) : ControllerBase
{
    /// <summary>
    /// One arrival day of a property whose canton asks for an electronic notification (not Ticino): the guests of the stays that arrive on it as data ready to copy
    /// (<c>guests</c>), what they still lack (<c>issues</c>), where to notify (<c>portalUrl</c>), the deadline of the canton (<c>dueAt</c>, <c>deadlineKind</c> and its <c>deadlineNote</c>), how
    /// sure the source is (<c>certainty</c>), who declared the day notified and when (<c>declaredAt</c>, <c>declaredBy</c>) and the days still to notify (<c>pendingDays</c>). Without
    /// <c>date</c> it is the oldest day still to notify (of the last 14), or today when none is.
    /// </summary>
    /// <param name="propertyId">The property.</param>
    /// <param name="date">The arrival day (<c>yyyy-MM-dd</c>, Europe/Zurich); optional.</param>
    /// <param name="cancellationToken">Aborts the request.</param>
    /// <response code="200"><see cref="SwissNotificationDayResponse"/>; never cached: it holds the personal data of the guests.</response>
    /// <response code="403">The caller may not read the bookings or the guests of this property.</response>
    /// <response code="404"><c>property_not_found</c>: no such property in the caller's org; or the module is off.</response>
    /// <response code="422"><c>swiss_notification_not_applicable</c>: the property is not one the assisted notification covers.</response>
    [HttpGet("notification-day")]
    [Authorize(Policy = CasazenPolicies.GuestRead)]
    [ProducesResponseType(typeof(SwissNotificationDayResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<SwissNotificationDayResponse>> GetNotificationDay(
        Guid propertyId,
        [FromQuery] DateOnly? date,
        CancellationToken cancellationToken)
    {
        var denied = await AuthorizeAsync(propertyId, BookingOperations.Read, cancellationToken);
        if (denied is not null)
            return denied;

        var day = await swissNotifications.GetDayAsync(propertyId, date ?? TimeProvider.System.TodayInRomeAsDateOnly(), cancellationToken);
        if (date is null && day.PendingDays.Count > 0 && day.PendingDays[0].Day != day.Day)
            day = await swissNotifications.GetDayAsync(propertyId, day.PendingDays[0].Day, cancellationToken);

        Response.Headers.CacheControl = "private, no-store";
        return Ok(SwissNotificationDayResponse.From(day, localizer));
    }

    /// <summary>
    /// Declares an arrival day as notified to the cantonal authority (decision D10): the day is closed (it is no longer one of the days to notify, so the alerts and the cockpit stop listing it) and
    /// shows who declared it and when. <b>Idempotent</b>: a day declared already answers the declaration that exists (<c>created: false</c>), whoever repeats the call. The other hosts of the
    /// property are told live when a declaration is created (<c>swiss-notification-declared</c>). The answer holds no guest data.
    /// </summary>
    /// <param name="propertyId">The property.</param>
    /// <param name="request">Body <c>{ "arrivalDay": "2026-11-19" }</c> (<see cref="DeclareSwissNotificationRequest"/>).</param>
    /// <param name="cancellationToken">Aborts the request.</param>
    /// <response code="200"><see cref="SwissDeclarationResponse"/>, never cached.</response>
    /// <response code="400"><c>validation_error</c>: <c>arrivalDay</c> is missing or is not a date.</response>
    /// <response code="401"><c>unauthorized</c>: the token has no subject.</response>
    /// <response code="403">The caller may not declare the communication of this property (<c>alloggiati.submit</c>, or the property is out of their reach).</response>
    /// <response code="404"><c>property_not_found</c>: no such property in the caller's org; or the module is off.</response>
    /// <response code="422"><c>swiss_notification_not_applicable</c>; <c>swiss_declaration_day_in_future</c>: the day has not started in Switzerland.</response>
    [HttpPost("notification-declare")]
    [Authorize(Policy = CasazenPolicies.AlloggiatiSubmit)]
    [ProducesResponseType(typeof(SwissDeclarationResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<SwissDeclarationResponse>> DeclareNotification(
        Guid propertyId,
        [FromBody] DeclareSwissNotificationRequest request,
        CancellationToken cancellationToken)
    {
        var denied = await AuthorizeAsync(propertyId, AlloggiatiOperations.Submit, cancellationToken);
        if (denied is not null)
            return denied;

        if (User.GetUserId() is not { Length: > 0 } userId)
            return Unauthorized();

        var arrivalDay = request.ArrivalDay!.Value;
        var declaration = await swissNotifications.DeclareDayAsync(propertyId, arrivalDay, userId, cancellationToken);

        logger.LogInformation(
            "User {UserId} declared the Swiss notification ({RegimeCode}) of property {PropertyId} for {ArrivalDay} (created: {Created})",
            userId, declaration.RegimeCode, propertyId, arrivalDay, declaration.Created);
        Response.Headers.CacheControl = "private, no-store";
        return Ok(SwissDeclarationResponse.From(declaration));
    }

    /// <summary>
    /// Undoes the declaration of an arrival day: by any person of the same org with the same permission as declaring. The day is to notify again. <b>Idempotent</b>: 204 also when the day was not
    /// declared. The other hosts of the property are told live when a declaration was removed.
    /// </summary>
    /// <param name="propertyId">The property.</param>
    /// <param name="arrivalDay">The arrival day (<c>yyyy-MM-dd</c>).</param>
    /// <param name="cancellationToken">Aborts the request.</param>
    /// <response code="204">Nothing is declared for the day any more.</response>
    /// <response code="400"><c>validation_error</c>: <c>arrivalDay</c> is missing or is not a date.</response>
    /// <response code="401"><c>unauthorized</c>: the token has no subject.</response>
    /// <response code="403">The caller may not declare the communication of this property (<c>alloggiati.submit</c>, or the property is out of their reach).</response>
    /// <response code="404"><c>property_not_found</c>: no such property in the caller's org; or the module is off.</response>
    /// <response code="422"><c>swiss_notification_not_applicable</c>.</response>
    [HttpDelete("notification-declare")]
    [Authorize(Policy = CasazenPolicies.AlloggiatiSubmit)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult> UndeclareNotification(Guid propertyId, [FromQuery] DateOnly? arrivalDay, CancellationToken cancellationToken)
    {
        var denied = await AuthorizeAsync(propertyId, AlloggiatiOperations.Submit, cancellationToken);
        if (denied is not null)
            return denied;

        if (User.GetUserId() is not { Length: > 0 } userId)
            return Unauthorized();
        if (arrivalDay is not { } day)
            return this.ApiProblem(StatusCodes.Status400BadRequest, "validation_error", "ValidationError");

        var removed = await swissNotifications.UndeclareDayAsync(propertyId, day, userId, cancellationToken);

        logger.LogInformation(
            "User {UserId} undid the Swiss notification declaration of property {PropertyId} for {ArrivalDay} (removed: {Removed})",
            userId, propertyId, day, removed);
        return NoContent();
    }

    /// <summary>TN-3: 404 when the property is not visible (another org), 403 when it is but the caller's scope or permission does not allow it.</summary>
    private async Task<ActionResult?> AuthorizeAsync(Guid propertyId, HostOperationRequirement operation, CancellationToken cancellationToken)
    {
        var property = await hostResources.ForPropertyAsync(propertyId, cancellationToken);
        if (property is null)
            return this.ApiProblem(StatusCodes.Status404NotFound, "property_not_found", "PropertyNotFound");

        if (!await authorizationService.IsAuthorizedAsync(User, property, operation))
        {
            logger.LogWarning(
                "User {UserId} denied {Permission} on the Swiss notification of property {PropertyId}",
                User.GetUserId(), operation.PermissionKey, propertyId);
            return Forbid();
        }

        return null;
    }
}
