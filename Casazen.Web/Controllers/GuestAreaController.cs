using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Casazen.Web.Controllers;

/// <summary>
/// The guest area of a booking site (DB-06a, decision DB-D21): the guest who booked with no account proves a booking once with its
/// booking code and the email it was made with (or, with the flag <c>WhiteLabelHost</c> on, the last four digits of the phone, NF-143),
/// gets a signed token with a short life (two hours by default, never more than 24) and reads the booking with it. The features that live
/// in the area (messages, reviews…) add their actions here, behind the policy <see cref="CasazenPolicies.GuestSession"/>, and find the
/// guest's booking with <see cref="IGuestSessionService.FindBookingAsync"/>. Never indexable and never cached. Runbook
/// <c>docs/runbooks/direct-booking.md</c> § 8.4.
/// </summary>
/// <remarks>
/// <para><b>The code and the proof (address or digits) are in the body, never in a URL.</b> Only the booking code is in the path of the
/// read, and the token in the header; the address and the digits are in no URL, no log and no token (the token holds the hash of the
/// address of the booking).</para>
/// <para><b>One 404 for everything that does not identify a booking</b> — <c>guest_booking_not_found</c>, same status, same body: a site
/// that does not exist, a code that does not exist or is not a code, the code of another site's booking, an email that is not the
/// one of the booking, wrong digits of the phone or a booking without a phone, and, with a token, a booking of another guest. The limits
/// are one 429 for all: per IP (<see cref="RateLimitPolicies.PublicGuestSession"/>), per address (<see cref="GuestBookingEmailRateLimiter"/>,
/// shared with "Le mie prenotazioni"), per code (<see cref="GuestSessionCodeRateLimiter"/>) and, for the digits of the phone only, the lock
/// of a code after repeated failures (<see cref="GuestPhoneLookupLockout"/>).</para>
/// </remarks>
[ApiController]
[Route("api/public/guest")]
[Authorize(Policy = CasazenPolicies.GuestSession)]
[PrivateAnswer]
public class GuestAreaController(IGuestSessionService sessions, IFeatureFlags featureFlags) : ControllerBase
{
    /// <summary>
    /// Opens a guest session for the booking of this site with the booking code of the confirmation email and the email the booking was
    /// made with or, with the flag <c>WhiteLabelHost</c> on, the last four digits of the guest's phone (<c>phoneLast4</c>, NF-143): the same
    /// session either way. 200 with <c>accessToken</c>, <c>expiresAt</c> and <c>bookingCode</c>; 404 <c>guest_booking_not_found</c> whatever
    /// does not match; 400 <c>validation_error</c> for a body without site or code, or without exactly one proof (with the flag off a body
    /// without the address, the digits notwithstanding); 429 <c>rate_limited</c> (per IP, per address, per code, and a code locked after
    /// repeated failures of the digits, with <c>Retry-After</c>). The old lookup (<c>POST api/public/bookings/lookup</c>) stays as it is.
    /// </summary>
    [HttpPost("session")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicies.PublicGuestSession)]
    [GuestBookingEmailRateLimit]
    [GuestSessionCodeRateLimit]
    [GuestPhoneLookupLockout]
    [RequestSizeLimit(4096)]
    [ProducesResponseType(typeof(GuestSessionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<GuestSessionResponse>> OpenSession(
        [FromBody] GuestSessionRequest request,
        CancellationToken cancellationToken)
    {
        // The digits are read only with the flag on (the body was checked so: exactly one proof); off, it is the e-mail proof of always.
        var opened = request.UsesPhoneProof(featureFlags)
            ? await sessions.OpenAsync(
                new GuestBookingPhoneCredentials(request.OrgSlug, request.BookingCode, request.PhoneLast4 ?? string.Empty),
                cancellationToken)
            : await sessions.OpenAsync(
                new GuestBookingCredentials(request.OrgSlug, request.BookingCode, request.Email ?? string.Empty),
                cancellationToken);
        return Ok(GuestSessionResponse.From(opened));
    }

    /// <summary>
    /// The booking <paramref name="code"/> of the guest of the session, as "Le mie prenotazioni" shows it (state, stay, amounts, online
    /// check-in, the host's contact; no personal data). 401 without a valid token (missing, altered or expired: the guest proves the
    /// booking again); 404 <c>guest_booking_not_found</c> when the code is not a booking of this guest on this site; 429.
    /// </summary>
    [HttpGet("bookings/{code}")]
    [EnableRateLimiting(RateLimitPolicies.PublicBookingLookup)]
    [ProducesResponseType(typeof(GuestBookingLookupResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<GuestBookingLookupResponse>> GetBooking(string code, CancellationToken cancellationToken)
    {
        var ticket = User.GetGuestSessionTicket();
        if (ticket is null)
            return Unauthorized();

        return Ok(GuestBookingLookupResponse.From(await sessions.GetBookingAsync(ticket, code, cancellationToken)));
    }
}
