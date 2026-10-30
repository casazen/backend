using System.Text.Json;
using System.Text.Json.Serialization;
using Casazen.Core.Authorization;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Core.Validation;
using Casazen.Web.Authorization;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Casazen.Web.Controllers;

/// <summary>
/// The tenants of the long-term area (LR-09): who lives in the landlord's properties, since when, for how much, how punctually, and the
/// two duties the law puts on the landlord about a person's data. There is no tenant record: a person is read from the parties of the
/// leases (same fiscal code, <see cref="Casazen.Core.Leases.LongRentTenantRules.PersonKey"/>), and the <c>id</c> of a person is opaque,
/// never the fiscal code. Reads need <c>lease.read</c> and are restricted in SQL to the caller's org and, unless org-wide, to the
/// properties they reach (<see cref="HostScope"/>, the scope the lease endpoints use); a person with no lease in that reach is a 404. The
/// e-mail is masked and the fiscal code never leaves the export. The <b>export</b> (full personal data, in clear) and the <b>erasure
/// request</b> need <c>lease.create</c> as well, and each lease they touch is authorized as a <see cref="HostResource"/> of its property
/// (<see cref="LeaseOperations.Create"/>, as the lease erasure button does): leases of properties the caller may not handle are left out.
/// </summary>
[ApiController]
[Route("api/long-rent/tenants")]
[Authorize(Policy = CasazenPolicies.LeaseRead)]
public class LongRentTenantsController(
    ILongRentTenantService tenants,
    IHostResourceLookup hostResources,
    IAuthorizationService authorizationService,
    IOrgContextResolver orgContextResolver,
    IHostScopeResolver hostScopeResolver,
    ILogger<LongRentTenantsController> logger) : ControllerBase
{
    private const string TenantNotFoundCode = "tenant_not_found";

    /// <summary>The name of the downloaded file: neutral on purpose, it must not carry the name of the person.</summary>
    private const string ExportFileName = "dati-personali.json";

    private static readonly JsonSerializerOptions ExportJson = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// The people of the caller's reach, ordered by last name: <c>view</c> is <c>All</c> (default), <c>Current</c> (at least one lease that
    /// has not concluded) or <c>Former</c> (every lease concluded, by the end of the period each is in, LR-14; 400 <c>validation_error</c>
    /// for any other value); <c>q</c>, a text looked for, without regard to case, in the first and last name (either order) and in the name
    /// of their properties (at most 100 characters); <c>page</c> and <c>pageSize</c> (default 25, at most 100). A person on several leases
    /// is one row. <c>counts</c> are the people of each view for the search asked. Each row carries the e-mail masked, whether the tenant is
    /// a citizen of the Union, the leases (with their stage and rent) and the punctuality: the installments due so far and how many were paid
    /// within <c>LongRentTenants:OnTimeDays</c> days (default 5) of their due date.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(TenantPage), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<TenantPage>> GetAll(
        [FromQuery] string? view = null,
        [FromQuery] string? q = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = TenantListQuery.DefaultPageSize,
        CancellationToken cancellationToken = default)
    {
        var listView = TenantListView.All;
        if (!string.IsNullOrWhiteSpace(view) && !EnumNames.TryParseDefined(view, out listView))
            return this.ApiProblem(StatusCodes.Status400BadRequest, ProblemCodes.ValidationError, "TenantListViewUnknown");

        var scope = await GetHostScopeAsync(cancellationToken);
        if (scope is null)
            return Unauthorized();

        var query = new TenantListQuery(listView, q, Math.Max(1, page), Math.Clamp(pageSize, 1, TenantListQuery.MaxPageSize));
        Response.Headers.CacheControl = "private, no-store";
        return Ok(await tenants.GetTenantsAsync(scope, query, cancellationToken));
    }

    /// <summary>
    /// The sheet of a person: their row, their six latest installments due, what is overdue and the grace period used. 404
    /// <c>tenant_not_found</c> for an id that is not a tenant of the caller's reach (another org's, a property out of reach, or a person whose
    /// data were anonymized).
    /// </summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(TenantDetail), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<TenantDetail>> GetById(Guid id, CancellationToken cancellationToken = default)
    {
        var scope = await GetHostScopeAsync(cancellationToken);
        if (scope is null)
            return Unauthorized();

        var detail = await tenants.GetTenantAsync(scope, id, cancellationToken);
        if (detail is null)
            return NotFoundProblem();

        Response.Headers.CacheControl = "private, no-store";
        return Ok(detail);
    }

    /// <summary>
    /// The copy of the personal data of a person (art. 15 and 20 GDPR), as a JSON file: for each of their leases the data they gave as a
    /// party (name, fiscal code, citizenship, e-mail, residence, the day their documents were checked), the terms, the property they live
    /// in, the installments and the type and day of the events. Never the data of another party of those leases, the landlord's included.
    /// Needs <c>lease.read</c> and <c>lease.create</c> (the data are in clear) and the permission to handle each lease it covers; 403 when
    /// the caller may handle none of them. Never cached, rate limited per person, and the file name does not carry the name of the person.
    /// </summary>
    [HttpGet("{id:guid}/export")]
    [Authorize(Policy = CasazenPolicies.LeaseCreate)]
    [EnableRateLimiting(RateLimitPolicies.TenantPrivacy)]
    [ProducesResponseType(typeof(FileContentResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Export(Guid id, CancellationToken cancellationToken = default)
    {
        var scope = await GetHostScopeAsync(cancellationToken);
        if (scope is null)
            return Unauthorized();

        var (allowed, denied) = await AuthorizeLeasesAsync(scope, id, cancellationToken);
        if (denied is not null)
            return denied;

        var export = await tenants.ExportAsync(scope, id, allowed!, cancellationToken);
        if (export is null)
            return NotFoundProblem();

        // Who asked and how many leases: the content is personal data and is never written to a log.
        logger.LogInformation("Tenant data export requested by user {UserId}. Leases={Leases}", User.GetUserId(), export.Leases.Count);
        Response.Headers.CacheControl = "private, no-store";
        return File(JsonSerializer.SerializeToUtf8Bytes(export, ExportJson), "application/json", ExportFileName);
    }

    /// <summary>
    /// The erasure request of a person (art. 17 GDPR), applied lease by lease with the rules of <c>POST api/leases/{id}/erasure-request</c>
    /// (<see cref="ILeasePartyPrivacyService"/>): a lease that has ended is anonymized now (the person also on a lease that has not ended
    /// keeps their data until that one ends: <c>PartiallyAnonymized</c>), a lease that has not ended is <b>not touched</b> and is only set to
    /// be anonymized from the day after it ends (<c>Scheduled</c>, <c>anonymizationFrom</c>): those are the leases that hold the data back.
    /// The signed contract and the receipts are never removed. Each lease records the request in its history. Idempotent. Needs
    /// <c>lease.create</c> and the permission to handle each lease it covers; 403 when the caller may handle none of them.
    /// </summary>
    [HttpPost("{id:guid}/erasure-request")]
    [Authorize(Policy = CasazenPolicies.LeaseCreate)]
    [EnableRateLimiting(RateLimitPolicies.TenantPrivacy)]
    [ProducesResponseType(typeof(TenantErasureResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RequestErasure(Guid id, CancellationToken cancellationToken = default)
    {
        var scope = await GetHostScopeAsync(cancellationToken);
        if (scope is null)
            return Unauthorized();

        var (allowed, denied) = await AuthorizeLeasesAsync(scope, id, cancellationToken);
        if (denied is not null)
            return denied;

        var result = await tenants.RequestErasureAsync(scope, id, allowed!, cancellationToken);
        if (result is null)
            return NotFoundProblem();

        logger.LogInformation("Tenant erasure requested by user {UserId}. Leases={Leases}", User.GetUserId(), result.Leases.Count);
        Response.Headers.CacheControl = "private, no-store";
        return Ok(result);
    }

    /// <summary>
    /// The leases of the person the caller may handle (<see cref="LeaseOperations.Create"/> on the property of each, one check per
    /// property): 404 when there is no such person in the caller's reach, 403 when there is but none of their leases may be handled.
    /// </summary>
    private async Task<(IReadOnlySet<Guid>? Allowed, IActionResult? Denied)> AuthorizeLeasesAsync(
        HostScope scope, Guid tenantId, CancellationToken cancellationToken)
    {
        var leases = await tenants.FindLeasesAsync(scope, tenantId, cancellationToken);
        if (leases is null)
            return (null, NotFoundProblem());

        var maySet = new Dictionary<Guid, bool>();
        var allowed = new HashSet<Guid>();
        foreach (var lease in leases)
        {
            if (!maySet.TryGetValue(lease.PropertyId, out var may))
            {
                var resource = await hostResources.ForPropertyAsync(lease.PropertyId, cancellationToken);
                may = resource is not null && await authorizationService.IsAuthorizedAsync(User, resource, LeaseOperations.Create);
                maySet[lease.PropertyId] = may;
            }

            if (may)
                allowed.Add(lease.LeaseId);
        }

        return allowed.Count == 0 ? (null, Forbid()) : (allowed, null);
    }

    private ObjectResult NotFoundProblem() =>
        this.ApiProblem(StatusCodes.Status404NotFound, TenantNotFoundCode, "TenantNotFound");

    private async Task<HostScope?> GetHostScopeAsync(CancellationToken cancellationToken)
    {
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken);
        return orgId is null ? null : await hostScopeResolver.ResolveHostScopeAsync(User, orgId.Value, cancellationToken);
    }
}
