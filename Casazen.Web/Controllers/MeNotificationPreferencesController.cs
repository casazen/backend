using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// How the caller wants to be alerted (E05/F03, NF-65, ADR-025): <c>GET</c> gives the quiet hours and, for each kind of event, the channels the
/// caller chose (or the defaults of PO-10, marked as such) with the channels that can deliver today; <c>PUT</c> saves them.
/// </summary>
/// <remarks>
/// <para>The settings are the caller's own, in every host context, so the policy is <see cref="CasazenPolicies.Authenticated"/> and the user is
/// always the token's <c>sub</c>, never a parameter or a field of the body: nobody reads or changes the settings of another person. The org is
/// the caller's host org, found by <see cref="INotificationPreferenceService"/> from the user row; a caller with none gets the defaults and
/// cannot save (404).</para>
/// <para>422 <c>notification_legal_channel_required</c>: the legal deadline would be left with no channel; 422 <c>notification_channel_unavailable</c>:
/// browser push, which has no infrastructure yet; 422 <c>notification_preferences_empty</c> and <c>notification_event_duplicated</c>. A save writes
/// everything or nothing. The answers are private and never cached.</para>
/// </remarks>
[ApiController]
[Route("api/me/notification-preferences")]
[Authorize(Policy = CasazenPolicies.Authenticated)]
[PrivateAnswer]
public class MeNotificationPreferencesController(INotificationPreferenceService preferences) : ControllerBase
{
    /// <summary>The caller's settings: the quiet hours, one entry for each kind of event and the channels that can deliver.</summary>
    /// <response code="200">The settings (the defaults for a caller who never saved).</response>
    [HttpGet]
    [ProducesResponseType(typeof(NotificationPreferencesDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<NotificationPreferencesDto>> Get(CancellationToken cancellationToken)
    {
        if (User.GetUserId() is not { Length: > 0 } userId)
            return Unauthorized();

        return Ok(NotificationPreferencesDto.From(await preferences.GetAsync(userId, cancellationToken)));
    }

    /// <summary>Saves the quiet hours and the channels of the kinds of event of the body; the others stay as they are.</summary>
    /// <response code="200">The settings after the save.</response>
    /// <response code="404">The caller has no host org.</response>
    /// <response code="422">The legal deadline left without a channel, a channel that cannot deliver, nothing to save, or a kind of event twice.</response>
    [HttpPut]
    [ProducesResponseType(typeof(NotificationPreferencesDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<NotificationPreferencesDto>> Save(
        [FromBody] SaveNotificationPreferencesRequest request,
        CancellationToken cancellationToken)
    {
        if (User.GetUserId() is not { Length: > 0 } userId)
            return Unauthorized();

        var update = new NotificationPreferencesUpdate(
            request.QuietHours is { Start: { } start, End: { } end } ? new QuietHoursUpdate(start, end) : null,
            request.Events?
                .Select(e => new NotificationEventUpdate(
                    e.EventType!.Value,
                    e.Channels!.Aggregate(NotificationChannels.None, (all, one) => all | one)))
                .ToList());

        return Ok(NotificationPreferencesDto.From(await preferences.SaveAsync(userId, update, cancellationToken)));
    }
}
