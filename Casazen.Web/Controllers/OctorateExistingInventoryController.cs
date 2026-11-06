using Casazen.Core.Authorization;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// The import of the listings that already exist on OTAs or in another channel manager, the <c>onOtas</c> scenario of the connect wizard
/// (E02/F01/US03, D6, ADR-023): create the batch, give the access to each channel in the Octorate panel, import the rooms, then go on for the
/// listings a person confirmed; and the declaration of the scenario of each property or group in a mixed portfolio. Nothing here creates or
/// publishes a listing on Octorate or on an OTA.
/// </summary>
/// <remarks>
/// <para><b>Policy.</b> Every endpoint needs <see cref="CasazenPolicies.OtaWrite"/> and <c>Features:OctorateConnector</c> (404 while it is off,
/// before authentication). The service then checks the org and the reach of the caller on the batch: the whole inventory needs a caller who
/// reaches the whole org, and the properties of a batch must be the ones the caller reaches (a property of another org is the same 404 as
/// an unknown one).</para>
/// <para><b>No secret.</b> The access to a channel is given in the Octorate panel: no endpoint takes a username, a password, a cookie or an OTA
/// token, and only <c>access/start</c> returns the address of the panel (https, with no organization, property or person data). Every answer
/// is <c>private, no-store</c>.</para>
/// </remarks>
[ApiController]
[Route("api/octorate")]
[FeatureGate(FeatureFlags.OctorateConnector)]
[Authorize(Policy = CasazenPolicies.OtaWrite)]
public sealed class OctorateExistingInventoryController(
    IExistingInventoryImportService import,
    IOrgContextResolver orgContextResolver) : ControllerBase
{
    /// <summary>Creates a batch for the chosen OTA channels and the properties of the scope; nothing is called on Octorate.</summary>
    /// <response code="201">The new batch.</response>
    /// <response code="403">No org, or the whole inventory was asked for by a caller who does not reach the whole org (<c>existing_inventory_needs_whole_org</c>).</response>
    /// <response code="404">A property is not the org's or is not within the caller's reach (<c>octorate_property_not_found</c>); the group does not exist.</response>
    /// <response code="422"><c>existing_inventory_channels_required</c>, <c>existing_inventory_channel_not_supported</c>, <c>existing_inventory_scope_invalid</c> (both properties and group, or an empty list of properties: the whole inventory is asked for by leaving the list out).</response>
    [HttpPost("existing-inventory/batches")]
    [ProducesResponseType(typeof(ExistingInventoryBatchDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<ExistingInventoryBatchDto>> Create(
        [FromBody] CreateExistingInventoryBatchRequest request,
        CancellationToken cancellationToken)
    {
        if (await CallerAsync(cancellationToken) is not { } caller)
            return NoOrg();

        // A selection of no property is not «all the properties»: only leaving the list out takes the whole inventory. A wizard with nothing
        // ticked must not start a batch on the whole organization of someone who reaches it.
        if (request.PropertyIds is { Count: 0 })
            throw new DomainRuleException("existing_inventory_scope_invalid", "ExistingInventoryScopeInvalid");

        var hasProperties = request.PropertyIds is { Count: > 0 };
        var hasGroup = !string.IsNullOrWhiteSpace(request.Group);
        if (hasProperties && hasGroup)
            throw new DomainRuleException("existing_inventory_scope_invalid", "ExistingInventoryScopeInvalid");

        var scope = hasProperties
            ? ExistingInventoryScope.ForProperties(request.PropertyIds!)
            : hasGroup
                ? ExistingInventoryScope.ForGroup(request.Group!)
                : ExistingInventoryScope.AllInventory;
        var batch = await import.CreateBatchAsync(caller, request.Channels ?? [], scope, cancellationToken);

        NoStore();
        return Created($"/api/octorate/existing-inventory/batches/{batch.Id}", ExistingInventoryBatchDto.From(batch));
    }

    /// <summary>The batch as stored: the steps, the counts and the stable error codes of each channel. Reads the result of the channel access when it is due.</summary>
    /// <response code="200">The batch.</response>
    /// <response code="404">The batch is not the org's, or the caller does not reach it.</response>
    [HttpGet("existing-inventory/batches/{id:guid}")]
    [ProducesResponseType(typeof(ExistingInventoryBatchDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ExistingInventoryBatchDto>> Get(Guid id, CancellationToken cancellationToken)
    {
        if (await CallerAsync(cancellationToken) is not { } caller)
            return NoOrg();

        var batch = await import.GetBatchAsync(caller, id, cancellationToken);
        NoStore();
        return Ok(ExistingInventoryBatchDto.From(batch));
    }

    /// <summary>
    /// Starts (or reuses) the handoff to the Octorate panel for the access to a channel. The step and the resume point are saved before the
    /// answer, which carries the handoff mode and the address of the panel; a channel whose access is already given answers
    /// <c>alreadyComplete</c> and no address. The person gives the access on the panel: CasaZen takes no credential.
    /// </summary>
    /// <response code="200">How and where to open the panel.</response>
    /// <response code="404">The batch is not the org's or the channel is not in it.</response>
    /// <response code="409"><c>octorate_panel_url_missing</c>: the panel address is not configured.</response>
    [HttpPost("existing-inventory/batches/{id:guid}/channels/{code}/access/start")]
    [ProducesResponseType(typeof(ChannelAccessStartDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ChannelAccessStartDto>> StartAccess(Guid id, string code, CancellationToken cancellationToken)
    {
        if (await CallerAsync(cancellationToken) is not { } caller)
            return NoOrg();

        var result = await import.StartChannelAccessAsync(caller, id, code, cancellationToken);
        NoStore();
        return Ok(ChannelAccessStartDto.From(result));
    }

    /// <summary>
    /// The person says the access was given. Only after the check stored «could not tell» for the channel; never the normal path: a verified
    /// access moves the import on by itself.
    /// </summary>
    /// <response code="200">The batch.</response>
    /// <response code="404">The batch is not the org's or the channel is not in it.</response>
    /// <response code="409"><c>panel_step_verification_required</c>: CasaZen has not tried to check it yet; <c>panel_step_not_valid</c>: the check found the access not valid.</response>
    [HttpPost("existing-inventory/batches/{id:guid}/channels/{code}/access/confirm")]
    [ProducesResponseType(typeof(ExistingInventoryBatchDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ExistingInventoryBatchDto>> ConfirmAccess(Guid id, string code, CancellationToken cancellationToken)
    {
        if (await CallerAsync(cancellationToken) is not { } caller)
            return NoOrg();

        var batch = await import.ConfirmChannelAccessAsync(caller, id, code, cancellationToken);
        NoStore();
        return Ok(ExistingInventoryBatchDto.From(batch));
    }

    /// <summary>
    /// Imports the rooms of every channel whose access is given, reads the listings again and stops at <c>AwaitingMapping</c>. Repeating it is
    /// safe. A listing whose last operation has an unknown result (<c>d6_result_unknown</c>) is asked again only when its accommodation id is in
    /// <c>retryUnknown</c>.
    /// </summary>
    /// <response code="200">The batch after the run (a channel that needs attention says why).</response>
    /// <response code="404">The batch is not the org's, or the caller does not reach it.</response>
    /// <response code="409"><c>existing_inventory_access_pending</c>: no channel has its access given yet.</response>
    [HttpPost("existing-inventory/batches/{id:guid}/run")]
    [ProducesResponseType(typeof(ExistingInventoryBatchDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ExistingInventoryBatchDto>> Run(
        Guid id,
        [FromBody] RunExistingInventoryRequest? request,
        CancellationToken cancellationToken)
    {
        if (await CallerAsync(cancellationToken) is not { } caller)
            return NoOrg();

        var batch = await import.RunAsync(caller, id, cancellationToken, request?.RetryUnknown);
        NoStore();
        return Ok(ExistingInventoryBatchDto.From(batch));
    }

    /// <summary>
    /// Goes on for the listings a person confirmed: reservations, calendars, «mark connected». The accommodation ids in <c>leaveForLater</c>
    /// stay write-blocked and are not marked connected; the other listings go on. Repeating it is safe. A listing whose last operation has an
    /// unknown result (<c>d6_result_unknown</c>) is asked again only when its accommodation id is in <c>retryUnknown</c>.
    /// </summary>
    /// <response code="200">The batch after the run.</response>
    /// <response code="404">The batch is not the org's, or the caller does not reach it.</response>
    /// <response code="409"><c>existing_inventory_not_ready</c>: no channel has its rooms imported; <c>existing_inventory_mapping_pending</c>: a listing is neither confirmed, rejected nor left for later.</response>
    [HttpPost("existing-inventory/batches/{id:guid}/continue")]
    [ProducesResponseType(typeof(ExistingInventoryBatchDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ExistingInventoryBatchDto>> Continue(
        Guid id,
        [FromBody] ContinueExistingInventoryRequest? request,
        CancellationToken cancellationToken)
    {
        if (await CallerAsync(cancellationToken) is not { } caller)
            return NoOrg();

        var batch = await import.ContinueAsync(caller, id, request?.LeaveForLater, cancellationToken, request?.RetryUnknown);
        NoStore();
        return Ok(ExistingInventoryBatchDto.From(batch));
    }

    /// <summary>The scenario the person declared for each property or group of the org it reaches.</summary>
    /// <response code="200">The intents.</response>
    [HttpGet("inventory-intents")]
    [ProducesResponseType(typeof(IReadOnlyList<InventoryIntentDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<IReadOnlyList<InventoryIntentDto>>> ListIntents(CancellationToken cancellationToken)
    {
        if (await CallerAsync(cancellationToken) is not { } caller)
            return NoOrg();

        var intents = await import.ListIntentsAsync(caller, cancellationToken);
        NoStore();
        return Ok(intents.Select(InventoryIntentDto.From).ToList());
    }

    /// <summary>
    /// Declares the scenario of each property (or named group) of a mixed portfolio. «New» is a choice only the person makes: it is refused
    /// while an import batch or a mapping exists for the property, and nothing turns an existing scenario into it.
    /// </summary>
    /// <response code="200">The intents that were set.</response>
    /// <response code="404">A property is not the org's or is not within the caller's reach.</response>
    /// <response code="409"><c>inventory_intent_change_refused</c>, <c>inventory_intent_conflict</c>.</response>
    /// <response code="422"><c>inventory_scenario_invalid</c>, <c>inventory_group_required</c>, <c>inventory_intent_duplicate</c>.</response>
    [HttpPut("inventory-intents")]
    [ProducesResponseType(typeof(IReadOnlyList<InventoryIntentDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<IReadOnlyList<InventoryIntentDto>>> SetIntents(
        [FromBody] InventoryIntentsRequest request,
        CancellationToken cancellationToken)
    {
        if (await CallerAsync(cancellationToken) is not { } caller)
            return NoOrg();

        var assignments = new List<InventoryIntentAssignment>();
        foreach (var assignment in request.Assignments ?? [])
        {
            if (!OctorateInventoryScenarioCodes.TryParse(assignment.Scenario, out var scenario))
                throw new DomainRuleException("inventory_scenario_invalid", "InventoryScenarioInvalid");

            assignments.Add(new InventoryIntentAssignment(assignment.PropertyId, scenario, assignment.Group));
        }

        var intents = await import.SetIntentsAsync(caller, assignments, cancellationToken);
        NoStore();
        return Ok(intents.Select(InventoryIntentDto.From).ToList());
    }

    /// <summary>The caller's org, id and token roles; <c>null</c> when it has no org.</summary>
    private async Task<ExistingInventoryCaller?> CallerAsync(CancellationToken cancellationToken)
    {
        if (User.GetUserId() is not { Length: > 0 } userId
            || await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken) is not { } orgId)
        {
            return null;
        }

        return new ExistingInventoryCaller(orgId, userId, User.GetRoles());
    }

    private void NoStore() => Response.Headers.CacheControl = "private, no-store";

    private ObjectResult NoOrg() => this.ApiProblem(StatusCodes.Status403Forbidden, "no_org_context", "NoOrganizationContext");
}
