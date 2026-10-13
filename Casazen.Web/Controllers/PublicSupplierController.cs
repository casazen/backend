using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Core.Utilities;
using Casazen.Web.DTOs.Supplier;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Casazen.Web.Controllers;

/// <summary>
/// Public showcase of an active supplier, behind <c>/fornitori/{slug}</c> of the web app (SU-13, A4-16), and, since SP-09, its
/// published services, free slots and price estimate. Anonymous, rate limited, never indexable, no cookie, and nothing about a
/// person or about the supplier's private calendar: only what the supplier published. Runbook
/// <c>docs/runbooks/suppliers.md</c> sections 18 and 22.
/// </summary>
/// <remarks>
/// <para><b>One 404.</b> An unknown slug and a supplier that is not active (pending or suspended) answer the same on every
/// endpoint; an unknown, draft, paused or deleted service, and a service of another supplier, answer the same too.</para>
/// <para><b>The flag.</b> The four endpoints added by SP-09 are behind <c>Features:SupplierShowcaseBooking</c> (off by
/// default): with it off they answer 404 like a route that does not exist. The page itself
/// (<c>GET api/public/suppliers/{slug}</c>) keeps answering, and with the flag off it reads exactly as it did before SP-09.</para>
/// </remarks>
[ApiController]
[Route("api/public/suppliers")]
[AllowAnonymous]
[EnableRateLimiting(RateLimitPolicies.PublicRead)]
public class PublicSupplierController(
    IPublicSupplierShowcaseService showcase,
    IComuneDirectory comuneDirectory,
    ISupplierService supplierService,
    IFeatureFlags featureFlags) : ControllerBase
{
    /// <summary>
    /// The showcase of the active supplier with this slug (looked up lowercase). 404 <c>not_found</c> for an unknown slug and
    /// for a supplier that is not active (pending or suspended): the page says "does not exist" for both. The answer carries
    /// <c>X-Robots-Tag: noindex</c>: the showcase is not indexable in v0 (as the page, see the runbook). With the flag
    /// <c>SupplierShowcaseBooking</c> on it also carries <c>services</c> (the published ones, with price and unit) and, once
    /// measured, <c>medianResponseMinutes</c>; with it off those two are left out and the answer is the one of SU-13.
    /// </summary>
    [HttpGet("{slug}")]
    [ProducesResponseType(typeof(SupplierShowcaseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<SupplierShowcaseDto>> GetBySlug(string slug, CancellationToken ct)
    {
        MarkNotIndexable();

        var profile = await showcase.FindActiveSupplierAsync(slug, ct);
        if (profile is null)
            return SupplierNotFound();

        var listed = await comuneDirectory.GetByIstatCodesAsync(SupplierComuniView.IstatCodes(profile), ct);
        var today = TimeProvider.System.TodayInRomeAsDateOnly();
        var availability = await supplierService.GetAvailabilityAsync(profile.OrgId, today, today.AddDays(13), ct);

        var dto = SupplierShowcaseMapper.ToDto(profile, listed, availability);
        if (featureFlags.IsEnabled(FeatureFlags.SupplierShowcaseBooking))
        {
            var extension = await showcase.GetExtensionAsync(profile, ct);
            dto.Services = extension.Services.Select(PublicSupplierMapper.ToSummaryDto).ToList();
            dto.MedianResponseMinutes = extension.MedianResponseMinutes;
        }

        return Ok(dto);
    }

    /// <summary>
    /// The published (<c>Active</c>) services of the supplier, by the supplier's order, each in full as the detail is: name,
    /// category, summary, description, "from" price and unit, whether the prices include VAT as the supplier declared, duration,
    /// what is included and excluded, photos and the structured supplements the estimate is computed from. 404 <c>not_found</c>
    /// as the page; 404 (no body worth reading) while the flag is off.
    /// </summary>
    [HttpGet("{slug}/services")]
    [FeatureGate(FeatureFlags.SupplierShowcaseBooking)]
    [ProducesResponseType(typeof(PublicSupplierServiceListResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PublicSupplierServiceListResponse>> ListServices(string slug, CancellationToken ct)
    {
        MarkNotIndexable();

        var supplier = await showcase.FindActiveSupplierAsync(slug, ct);
        if (supplier is null)
            return SupplierNotFound();

        var services = await showcase.ListServicesAsync(supplier, ct);
        return Ok(new PublicSupplierServiceListResponse
        {
            Items = services.Select(PublicSupplierMapper.ToDetailDto).ToList(),
            Total = services.Count,
        });
    }

    /// <summary>
    /// One published service by its slug, with its description and the structured supplements the estimate is computed from.
    /// 404 <c>supplier_service_not_found</c> for an unknown slug, a draft, a paused or a deleted service and a service of
    /// another supplier: the same answer for all.
    /// </summary>
    [HttpGet("{slug}/services/{serviceSlug}")]
    [FeatureGate(FeatureFlags.SupplierShowcaseBooking)]
    [ProducesResponseType(typeof(PublicSupplierServiceDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PublicSupplierServiceDetailDto>> GetService(string slug, string serviceSlug, CancellationToken ct)
    {
        MarkNotIndexable();

        var supplier = await showcase.FindActiveSupplierAsync(slug, ct);
        if (supplier is null)
            return SupplierNotFound();

        var service = await showcase.FindServiceAsync(supplier, serviceSlug, ct);
        return service is null ? ServiceNotFound() : Ok(PublicSupplierMapper.ToDetailDto(service));
    }

    /// <summary>
    /// The free slots of a service: <c>?service=</c> (its slug, required), <c>from</c> (a date, Europe/Rome; default and never
    /// before today) and <c>days</c> (1 to 62, default 14), cut at the supplier's horizon. Each day says whether it is
    /// available and lists its slots in UTC and in Rome time; a day without a slot is just "not available", with no reason.
    /// Computed by the supplier's slot planner and cached for 30 seconds: <b>a slot shown is not a promise</b>, the booking
    /// recomputes it. 400 <c>validation_error</c> for a missing service or a <c>days</c> outside 1 to 62.
    /// </summary>
    [HttpGet("{slug}/slots")]
    [FeatureGate(FeatureFlags.SupplierShowcaseBooking)]
    [EnableRateLimiting(RateLimitPolicies.PublicSupplierSlots)]
    [ProducesResponseType(typeof(PublicSlotsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PublicSlotsResponse>> GetSlots(
        string slug,
        [FromQuery] string? service,
        [FromQuery] DateOnly? from,
        [FromQuery] int? days,
        CancellationToken ct)
    {
        MarkNotIndexable();

        if (string.IsNullOrWhiteSpace(service))
            return this.ApiProblem(StatusCodes.Status400BadRequest, ProblemCodes.ValidationError, "SupplierSlotsServiceRequired");
        if (days is < 1 or > PublicShowcaseLimits.SlotsMaxDays)
            return this.ApiProblem(
                StatusCodes.Status400BadRequest,
                ProblemCodes.ValidationError,
                "SupplierSlotsDaysInvalid",
                PublicShowcaseLimits.SlotsMaxDays);

        var supplier = await showcase.FindActiveSupplierAsync(slug, ct);
        if (supplier is null)
            return SupplierNotFound();

        var slots = await showcase.GetSlotsAsync(supplier, service, from, days, ct);
        return slots is null ? ServiceNotFound() : Ok(PublicSupplierMapper.ToDto(slots));
    }

    /// <summary>
    /// The price estimate of a service for the options, the quantity, the surface and the comune the customer chose: a total
    /// and its lines, <c>isEstimate</c>, and <c>requiresQuote</c> when there is no total: a service on quote, or a comune
    /// outside the supplier's zones (the supplier decides): both are a 200 answer that says "ask for a quote", never an error.
    /// VAT is only the supplier's declaration (<c>pricesIncludeVat</c>). 422 <c>supplier_quote_invalid</c> (with
    /// <c>fields</c>) for a value that does not fit the service.
    /// </summary>
    [HttpPost("{slug}/quote")]
    [FeatureGate(FeatureFlags.SupplierShowcaseBooking)]
    [EnableRateLimiting(RateLimitPolicies.PublicSupplierQuote)]
    [RequestSizeLimit(PublicShowcaseLimits.QuoteMaxBodyBytes)]
    [ProducesResponseType(typeof(PublicQuoteResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<PublicQuoteResponse>> Quote(string slug, [FromBody] PublicQuoteRequest request, CancellationToken ct)
    {
        MarkNotIndexable();

        var supplier = await showcase.FindActiveSupplierAsync(slug, ct);
        if (supplier is null)
            return SupplierNotFound();

        try
        {
            var quote = await showcase.QuoteAsync(supplier, request.Service, PublicSupplierMapper.ToInput(request), ct);
            return quote is null ? ServiceNotFound() : Ok(PublicSupplierMapper.ToDto(quote));
        }
        catch (SupplierQuoteRuleException ex)
        {
            var problem = ApiProblemDetails.Create(
                HttpContext, StatusCodes.Status422UnprocessableEntity, ex.Code, ex.MessageKey, ex.MessageArgs);
            problem.Extensions["fields"] = ex.Fields;
            return new ObjectResult(problem)
            {
                StatusCode = StatusCodes.Status422UnprocessableEntity,
                ContentTypes = { ApiProblemDetails.ContentType },
            };
        }
    }

    /// <summary>The showcase is not indexable in v0 (D11): every answer says so, errors included.</summary>
    private void MarkNotIndexable() => Response.Headers["X-Robots-Tag"] = "noindex";

    /// <summary>The 404 of an unknown, pending or suspended supplier: one answer for the three.</summary>
    private ObjectResult SupplierNotFound() =>
        this.ApiProblem(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "SupplierShowcaseNotFound");

    /// <summary>The 404 of a service that is not a published one of this supplier: one answer for every reason.</summary>
    private ObjectResult ServiceNotFound() =>
        this.ApiProblem(StatusCodes.Status404NotFound, SupplierServiceCatalogErrors.NotFound, "SupplierServiceNotFound");
}
