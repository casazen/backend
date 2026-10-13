using Casazen.Core.Features;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Web.DTOs.Supplier;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace Casazen.Web.Controllers;

/// <summary>
/// The booking of a supplier from its public showcase, by a customer with no account (SP-10, decision D34 revised): a hold on a
/// free slot, then the check of the customer's e-mail, then — only then — the request the supplier answers. Anonymous, behind the
/// feature flag <c>SupplierShowcaseBooking</c> (off by default: 404 while it is off), rate limited, never indexable and never
/// cached. Runbook <c>docs/runbooks/suppliers.md</c> section 23.
/// </summary>
/// <remarks>
/// <para><b>Nothing personal comes back.</b> The answers carry the id of the hold, when it lapses and, after the check, the code and
/// the time of the booking: never a name, an address, an e-mail or a phone. The logs carry ids and codes of errors only.</para>
/// <para><b>Uniform answers.</b> An unknown, pending or suspended supplier is the 404 of the showcase; a link that is wrong, of
/// another supplier or for a booking that does not exist is one 404 (<c>supplier_booking_link_invalid</c>); the limits per IP, per
/// address and the cap of unverified bookings of one address are all the same 429. Nothing tells whether an address or a code is
/// known. A robot that fills the trap field is answered like a person and nothing is done (no CAPTCHA, decision D9).</para>
/// </remarks>
[ApiController]
[Route("api/public/suppliers/{slug}/bookings")]
[AllowAnonymous]
public class PublicSupplierBookingController(
    IPublicSupplierShowcaseService showcase,
    IShowcaseBookingService bookings,
    SupplierBookingEmailRateLimiter emailLimit,
    TimeProvider timeProvider,
    IOptions<ShowcaseBookingOptions> options) : ControllerBase
{
    /// <summary>
    /// Holds a slot of the supplier and sends the customer the e-mail that checks the address. 201 with the id of the hold
    /// and when it lapses; the same <c>clientRequestId</c> is the same hold. The supplier receives nothing until the address is
    /// checked. 404 <c>not_found</c> (supplier), <c>supplier_service_not_found</c>; 422 <c>supplier_booking_invalid</c> (with
    /// <c>fields</c>), <c>supplier_quote_invalid</c>, <c>supplier_booking_consent_required</c> / <c>_outdated</c>,
    /// <c>supplier_booking_offline</c>, <c>supplier_booking_outside_zone</c>; 409 <c>supplier_slot_unavailable</c>; 429
    /// <c>rate_limited</c> (per IP, per address, or three bookings of the address waiting for the check).
    /// </summary>
    [HttpPost]
    [FeatureGate(FeatureFlags.SupplierShowcaseBooking)]
    [EnableRateLimiting(RateLimitPolicies.PublicSupplierBookingCreate)]
    [SupplierBookingEmailRateLimit]
    [RequestSizeLimit(ShowcaseBookingLimits.CreateMaxBodyBytes)]
    [ProducesResponseType(typeof(PublicSupplierBookingHoldResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<PublicSupplierBookingHoldResponse>> Create(
        string slug,
        [FromBody] CreatePublicSupplierBookingRequest request,
        CancellationToken ct)
    {
        MarkPrivate();

        var supplier = await showcase.FindActiveSupplierAsync(slug, ct);
        if (supplier is null)
            return SupplierNotFound();

        // The trap for robots: answered like a booking, with an id that leads nowhere; nothing is stored, nothing is sent.
        if (!string.IsNullOrWhiteSpace(request.Website))
        {
            return StatusCode(
                StatusCodes.Status201Created,
                new PublicSupplierBookingHoldResponse
                {
                    Id = Guid.NewGuid(),
                    ExpiresAt = DateTime.SpecifyKind(
                        timeProvider.GetUtcNow().UtcDateTime.AddMinutes(options.Value.EmailVerificationMinutes),
                        DateTimeKind.Utc),
                });
        }

        try
        {
            var hold = await bookings.CreateHoldAsync(
                supplier,
                PublicSupplierBookingMapper.ToInput(request, ClientIp.GetString(HttpContext)),
                ct);
            return StatusCode(StatusCodes.Status201Created, PublicSupplierBookingMapper.ToDto(hold));
        }
        catch (ShowcaseBookingRuleException ex)
        {
            return RuleProblem(ex.Code, ex.MessageKey, ex.MessageArgs, ex.Fields);
        }
        catch (SupplierQuoteRuleException ex)
        {
            return RuleProblem(ex.Code, ex.MessageKey, ex.MessageArgs, ex.Fields);
        }
        catch (ShowcaseBookingTooManyHoldsException)
        {
            // The same answer as the limit per address, to the second: the exact time the oldest booking of the address lapses
            // (what the exception carries) would tell the client when someone booked with that address.
            return SupplierBookingEmailRateLimitFilter.RateLimitedResult(HttpContext, emailLimit.Window);
        }
    }

    /// <summary>
    /// Checks the customer's e-mail with the token of the link: only now the request exists and reaches the supplier. 200 with the
    /// code and the time of the booking; a second click on the link answers the same (<c>alreadyConfirmed</c>) and does nothing
    /// again. 404 <c>supplier_booking_link_invalid</c> (unknown booking, another supplier's, wrong token: one answer); 409
    /// <c>supplier_booking_link_expired</c> (the 30 minutes have passed; the booking starts again); 422
    /// <c>supplier_booking_supplier_unavailable</c> (the supplier is not active anymore). The token is in the body, never in the
    /// URL of the API, so it is in no access log of the API.
    /// </summary>
    [HttpPost("{id:guid}/confirm-email")]
    [FeatureGate(FeatureFlags.SupplierShowcaseBooking)]
    [EnableRateLimiting(RateLimitPolicies.PublicBookingLookup)]
    [RequestSizeLimit(ShowcaseBookingLimits.ConfirmMaxBodyBytes)]
    [ProducesResponseType(typeof(PublicSupplierBookingConfirmationResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<PublicSupplierBookingConfirmationResponse>> ConfirmEmail(
        string slug,
        Guid id,
        [FromBody] ConfirmPublicSupplierBookingRequest request,
        CancellationToken ct)
    {
        MarkPrivate();

        var supplier = await showcase.FindActiveSupplierAsync(slug, ct);
        if (supplier is null)
            return SupplierNotFound();

        // A wrong link is the NotFoundException of the service, answered as 404 supplier_booking_link_invalid by the error middleware.
        var confirmation = await bookings.ConfirmEmailAsync(supplier, id, request.Token, ct);
        return Ok(PublicSupplierBookingMapper.ToDto(confirmation));
    }

    /// <summary>Not indexable, not cacheable: the answers belong to one customer.</summary>
    private void MarkPrivate()
    {
        Response.Headers["X-Robots-Tag"] = "noindex";
        Response.Headers.CacheControl = "no-store";
    }

    /// <summary>The 404 of an unknown, pending or suspended supplier: one answer for the three.</summary>
    private ObjectResult SupplierNotFound() =>
        this.ApiProblem(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "SupplierShowcaseNotFound");

    /// <summary>A 422 that names the fields at fault, as the estimate does.</summary>
    private ObjectResult RuleProblem(string code, string messageKey, IReadOnlyList<object> messageArgs, IReadOnlyList<string> fields)
    {
        var problem = ApiProblemDetails.Create(HttpContext, StatusCodes.Status422UnprocessableEntity, code, messageKey, messageArgs);
        problem.Extensions["fields"] = fields;
        return new ObjectResult(problem)
        {
            StatusCode = StatusCodes.Status422UnprocessableEntity,
            ContentTypes = { ApiProblemDetails.ContentType },
        };
    }
}
