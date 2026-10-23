using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs.Supplier;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// The supplier's customers (SP-16a): the page "Clienti". The hosts of CasaZen and the private customers of its public showcase, aggregated from the
/// requests, with what they did together and, in the detail, their latest requests. Every route is scoped to the caller's own supplier org (its supplier
/// link, never a value of the request, and never provisioned by a read of the customers): a customer of another supplier answers 404 like one that does not
/// exist. Not behind a feature flag. The answers carry personal data, so nothing is cached by the browser or a proxy, and <b>there is no export</b>
/// (decision SP-D25). Runbook <c>docs/runbooks/suppliers.md</c> section 31.
/// </summary>
/// <remarks>
/// Errors: 404 <c>not_found</c> when the caller has no linked supplier org, <c>supplier_client_not_found</c> for a customer that is not the caller's; 400
/// <c>validation_error</c> for a <c>filter</c> that is not one of the five.
/// </remarks>
[ApiController]
[Route("api/supplier/clients")]
[Authorize(Policy = CasazenPolicies.Supplier)]
public class SupplierClientsController(
    ISupplierClientsService clients,
    ISupplierOrgContextResolver supplierOrgContextResolver) : ControllerBase
{
    /// <summary>
    /// A page of the supplier's customers, the one with the latest activity first. <c>filter</c>: <c>tutti</c> (default), <c>host</c>, <c>privati</c>,
    /// <c>abituali</c> (at least three jobs taken) or <c>nuovi</c> (no job taken yet). <c>pageSize</c> up to 200 (the console searches the name on the rows
    /// it loaded). Each customer carries <c>id</c> (the <c>clientId</c> of the inbox), <c>kind</c>, <c>label</c> ("Nome C." for a private customer until a job
    /// of theirs is taken, then the full name), <c>jobsAccepted</c>, <c>jobsCompleted</c>, <c>lastJobAt</c>, <c>nextJobAt</c>, <c>grossPaidCents</c>,
    /// <c>regular</c>, <c>source</c> and, <b>only after the take</b>, <c>contact</c>; a customer removed by the retention has <c>anonymized: true</c> and no name.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(SupplierClientsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<SupplierClientsResponse>> List(
        [FromQuery] string? filter = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken cancellationToken = default)
    {
        if (!SupplierClientFilters.TryParse(filter, out var parsed))
            return this.ApiProblem(StatusCodes.Status400BadRequest, ProblemCodes.ValidationError, SupplierClientErrors.FilterInvalidMessageKey);

        var orgId = await supplierOrgContextResolver.GetLinkedSupplierOrgIdAsync(cancellationToken);
        if (orgId is null)
            return this.ApiProblem(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "SupplierProfileNotFound");

        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, SupplierClientLimits.MaxPageSize);
        var result = await clients.ListAsync(orgId.Value, parsed, page, pageSize, cancellationToken);

        SetNoStore();
        return Ok(SupplierClientsMapper.ToResponse(result, page, pageSize));
    }

    /// <summary>
    /// One customer with its latest six requests that are open or taken (each is a request of <c>GET api/supplier/inbox/{id}</c>). 404
    /// <c>supplier_client_not_found</c> when the id is not a customer of the caller's supplier org (another supplier's answers the same).
    /// </summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(SupplierClientDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<SupplierClientDetailDto>> Get(Guid id, CancellationToken cancellationToken)
    {
        var orgId = await supplierOrgContextResolver.GetLinkedSupplierOrgIdAsync(cancellationToken);
        if (orgId is null)
            return this.ApiProblem(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "SupplierProfileNotFound");

        var detail = await clients.GetAsync(orgId.Value, id, cancellationToken);

        SetNoStore();
        return Ok(SupplierClientsMapper.ToDetailDto(detail));
    }

    /// <summary>The answers carry the personal data of private customers: nothing is cached by the browser or a proxy.</summary>
    private void SetNoStore() => Response.Headers.CacheControl = "private, no-store";
}
