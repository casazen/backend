using System.Text.Json;
using System.Text.Json.Serialization;
using Casazen.Core.Authorization;
using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs.Owners;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// The mandates of the owners of an agency (E13/F01/US01/T01, NF-110): the versions of an owner (<c>agency.owner.view</c>), a new version and the standard
/// mandate applied in one call to every owner without one (<c>agency.owner.financial.manage</c>, the step «Mandato standard» of the «Fatturazione»
/// wizard). The reach of the caller is resolved here and applied by the service: an owner outside it is the 404 of an owner that does not exist.
/// </summary>
[ApiController]
[Authorize(Policy = CasazenPolicies.AgencyOwnerView)]
public class OwnerMandatesController(
    IOwnerMandateService mandates,
    IOrgContextResolver orgContextResolver,
    IHostScopeResolver hostScopeResolver) : ControllerBase
{
    [HttpGet("api/owners/{ownerId:guid}/mandates")]
    [ProducesResponseType(typeof(List<OwnerMandateDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<List<OwnerMandateDto>>> List(Guid ownerId)
    {
        if (await ScopeAsync() is not { } scope)
            return Forbid();

        var versions = await mandates.ListAsync(scope, ownerId, HttpContext.RequestAborted);
        return Ok(versions.Select(OwnerMandateDto.From).ToList());
    }

    [HttpPost("api/owners/{ownerId:guid}/mandates")]
    [Authorize(Policy = CasazenPolicies.AgencyOwnerFinancialManage)]
    [ProducesResponseType(typeof(OwnerMandateDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<OwnerMandateDto>> Create(Guid ownerId, [FromBody] OwnerMandateCommand command)
    {
        if (await ScopeAsync() is not { } scope)
            return Forbid();

        var mandate = await mandates.CreateVersionAsync(scope, ownerId, command, User.GetUserId(), HttpContext.RequestAborted);
        return CreatedAtAction(nameof(List), new { ownerId }, OwnerMandateDto.From(mandate));
    }

    [HttpPost("api/owners/{ownerId:guid}/mandates/read-document")]
    [Authorize(Policy = CasazenPolicies.AgencyOwnerFinancialManage)]
    [RequestSizeLimit(OwnerMandateDocuments.MaxBytes + 65536)]
    [RequestFormLimits(MultipartBodyLengthLimit = OwnerMandateDocuments.MaxBytes + 65536)]
    public async Task<ActionResult<OwnerMandateReading>> ReadDocument(Guid ownerId, [FromForm] MandateDocumentForm form, [FromServices] OwnerMandateDocuments documents)
    {
        if (await ScopeAsync() is not { } scope)
            return Forbid();
        if (form.File is null)
            return BadRequest();
        Response.Headers.CacheControl = "no-store";
        return Ok(await documents.ReadAsync(scope, ownerId, form.File, HttpContext.RequestAborted));
    }

    [HttpPost("api/owners/{ownerId:guid}/mandates/with-document")]
    [Authorize(Policy = CasazenPolicies.AgencyOwnerFinancialManage)]
    [RequestSizeLimit(OwnerMandateDocuments.MaxBytes + 65536)]
    [RequestFormLimits(MultipartBodyLengthLimit = OwnerMandateDocuments.MaxBytes + 65536)]
    public async Task<ActionResult<OwnerMandateDto>> CreateWithDocument(Guid ownerId, [FromForm] MandateDocumentForm form, [FromServices] OwnerMandateDocuments documents)
    {
        if (await ScopeAsync() is not { } scope)
            return Forbid();
        if (form.File is null || string.IsNullOrWhiteSpace(form.Command) || form.Command.Length > 16000)
            return BadRequest();
        OwnerMandateCommand? command;
        try
        {
            var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            options.Converters.Add(new JsonStringEnumConverter());
            command = JsonSerializer.Deserialize<OwnerMandateCommand>(form.Command, options);
        }
        catch (JsonException) { return BadRequest(); }
        if (command is null)
            return BadRequest();
        var mandate = await documents.SaveAsync(scope, ownerId, command, form.File, User.GetUserId(), HttpContext.RequestAborted);
        return CreatedAtAction(nameof(List), new { ownerId }, OwnerMandateDto.From(mandate));
    }

    [HttpGet("api/owners/{ownerId:guid}/mandates/{mandateId:guid}/document")]
    [Authorize(Policy = CasazenPolicies.AgencyOwnerFinancialManage)]
    public async Task<IActionResult> DownloadDocument(Guid ownerId, Guid mandateId, [FromServices] OwnerMandateDocuments documents)
    {
        if (await ScopeAsync() is not { } scope)
            return Forbid();
        var stream = await documents.DownloadAsync(scope, ownerId, mandateId, HttpContext.RequestAborted);
        Response.Headers.CacheControl = "no-store";
        Response.Headers.XContentTypeOptions = "nosniff";
        return File(stream, "application/pdf", $"mandato-{mandateId:N}.pdf");
    }

    [HttpPost("api/owner-mandates/apply-template")]
    [Authorize(Policy = CasazenPolicies.AgencyOwnerFinancialManage)]
    [ProducesResponseType(typeof(OwnerMandateTemplateResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<OwnerMandateTemplateResult>> ApplyTemplate([FromBody] OwnerMandateTemplateCommand command)
    {
        if (await ScopeAsync() is not { } scope)
            return Forbid();

        return Ok(await mandates.ApplyTemplateAsync(scope, command, User.GetUserId(), HttpContext.RequestAborted));
    }

    private async Task<HostScope?> ScopeAsync()
    {
        var ct = HttpContext.RequestAborted;
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(ct);
        return orgId is null ? null : await hostScopeResolver.ResolveHostScopeAsync(User, orgId.Value, ct);
    }
}

public sealed class MandateDocumentForm
{
    public IFormFile? File { get; set; }
    public string? Command { get; set; }
}
