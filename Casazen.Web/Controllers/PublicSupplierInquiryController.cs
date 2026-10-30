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
/// The request on measure a customer with no account makes from a supplier's public showcase (SP-10b), for what is not in the price list
/// or is but with variations: the customer describes it, then checks its e-mail and — only then — the request exists for the supplier to
/// answer, in a conversation inside CasaZen. The twin of <see cref="PublicSupplierBookingController"/> without a slot to hold: anonymous,
/// behind the feature flag <c>SupplierShowcaseBooking</c> (off by default: 404 while it is off), rate limited, never indexable and never
/// cached. Runbook <c>docs/runbooks/suppliers.md</c> section 34.
/// </summary>
/// <remarks>
/// <para><b>Nothing personal comes back.</b> The answers carry the id of the inquiry, when it lapses and, after the check, the code and
/// the status of the request: never a name, an address, an e-mail, a phone or a word of the customer's message. The logs carry ids and
/// codes of errors only.</para>
/// <para><b>Uniform answers.</b> An unknown, pending or suspended supplier is the 404 of the showcase; a link that is wrong, of another
/// supplier or for an inquiry that does not exist is one 404 (<c>supplier_booking_link_invalid</c>); the limits per IP, per address and the
/// cap of the requests of one address that wait are all the same 429. A robot that fills the trap field is answered like a person and
/// nothing is done (no CAPTCHA, decision D9).</para>
/// </remarks>
[ApiController]
[Route("api/public/suppliers/{slug}/inquiries")]
[AllowAnonymous]
public class PublicSupplierInquiryController(
    IPublicSupplierShowcaseService showcase,
    IShowcaseInquiryService inquiries,
    SupplierBookingEmailRateLimiter emailLimit,
    TimeProvider timeProvider,
    IOptions<ShowcaseBookingOptions> options) : ControllerBase
{
    /// <summary>
    /// Keeps what the customer wrote and sends the customer the e-mail that checks the address. 201 with the id of the inquiry and when
    /// the link lapses; the same <c>clientRequestId</c> is the same inquiry. The supplier receives nothing until the address is checked. 404
    /// <c>not_found</c> (supplier), <c>supplier_service_not_found</c>; 422 <c>supplier_booking_invalid</c> (with <c>fields</c>),
    /// <c>supplier_booking_consent_required</c> / <c>_outdated</c>, <c>supplier_booking_outside_zone</c>, <c>supplier_booking_offline</c> (the supplier paused the showcase, SP-13a); 429 <c>rate_limited</c> (per IP, per
    /// address, or three requests of the address waiting).
    /// </summary>
    [HttpPost]
    [FeatureGate(FeatureFlags.SupplierShowcaseBooking)]
    [EnableRateLimiting(RateLimitPolicies.PublicSupplierBookingCreate)]
    [SupplierBookingEmailRateLimit]
    [RequestSizeLimit(ShowcaseBookingLimits.CreateMaxBodyBytes)]
    [ProducesResponseType(typeof(PublicSupplierInquiryResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<PublicSupplierInquiryResponse>> Create(
        string slug,
        [FromBody] CreatePublicSupplierInquiryRequest request,
        CancellationToken ct)
    {
        MarkPrivate();

        var supplier = await showcase.FindActiveSupplierAsync(slug, ct);
        if (supplier is null)
            return SupplierNotFound();

        // The trap for robots: answered like a request, with an id that leads nowhere; nothing is stored, nothing is sent.
        if (!string.IsNullOrWhiteSpace(request.Website))
        {
            return StatusCode(
                StatusCodes.Status201Created,
                new PublicSupplierInquiryResponse
                {
                    Id = Guid.NewGuid(),
                    ExpiresAt = DateTime.SpecifyKind(
                        timeProvider.GetUtcNow().UtcDateTime.AddMinutes(options.Value.EmailVerificationMinutes),
                        DateTimeKind.Utc),
                });
        }

        // SP-13a: a showcase the supplier paused takes no new request on measure either, with the answer the slots and the estimate give.
        // The check of an address written before the pause is not gated: that customer is answered all the same.
        if (await showcase.IsPausedAsync(supplier, ct))
            throw ShowcaseBookingErrors.OfflineSupplier();

        try
        {
            var inquiry = await inquiries.CreateAsync(
                supplier,
                PublicSupplierInquiryMapper.ToInput(request, ClientIp.GetString(HttpContext)),
                ct);
            return StatusCode(StatusCodes.Status201Created, PublicSupplierInquiryMapper.ToDto(inquiry));
        }
        catch (ShowcaseBookingRuleException ex)
        {
            return RuleProblem(ex.Code, ex.MessageKey, ex.MessageArgs, ex.Fields);
        }
        catch (ShowcaseBookingTooManyHoldsException)
        {
            // The same answer as the limit per address, to the second: the exact time the oldest request of the address lapses (what the
            // exception carries) would tell the client when someone wrote with that address.
            return SupplierBookingEmailRateLimitFilter.RateLimitedResult(HttpContext, emailLimit.Window);
        }
    }

    /// <summary>
    /// Checks the customer's e-mail with the token of the link: only now the request exists and reaches the supplier. 200 with the code and
    /// the status of the request; a second click on the link answers the same (<c>alreadyConfirmed</c>) and does nothing again. 404
    /// <c>supplier_booking_link_invalid</c> (unknown inquiry, another supplier's, wrong token: one answer); 409
    /// <c>supplier_booking_link_expired</c> (the time has passed; the request starts again); 422 <c>supplier_booking_supplier_unavailable</c>
    /// (the supplier is not active anymore). The token is in the body, never in the URL of the API, so it is in no access log of the API.
    /// </summary>
    [HttpPost("{id:guid}/confirm-email")]
    [FeatureGate(FeatureFlags.SupplierShowcaseBooking)]
    [EnableRateLimiting(RateLimitPolicies.PublicBookingLookup)]
    [RequestSizeLimit(ShowcaseBookingLimits.ConfirmMaxBodyBytes)]
    [ProducesResponseType(typeof(PublicSupplierInquiryConfirmationResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<PublicSupplierInquiryConfirmationResponse>> ConfirmEmail(
        string slug,
        Guid id,
        [FromBody] ConfirmPublicSupplierInquiryRequest request,
        CancellationToken ct)
    {
        MarkPrivate();

        var supplier = await showcase.FindActiveSupplierAsync(slug, ct);
        if (supplier is null)
            return SupplierNotFound();

        // A wrong link is the NotFoundException of the service, answered as 404 supplier_booking_link_invalid by the error middleware.
        var confirmation = await inquiries.ConfirmEmailAsync(supplier, id, request.Token, ct);
        return Ok(PublicSupplierInquiryMapper.ToDto(confirmation));
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

    /// <summary>A 422 that names the fields at fault, as the booking does.</summary>
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
