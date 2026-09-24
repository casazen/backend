using System.ComponentModel.DataAnnotations;
using Casazen.Core.Authorization;
using Casazen.Core.DTOs.Leases;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.Infrastructure;
using Casazen.Web.Resources;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;

namespace Casazen.Web.Controllers;

/// <summary>
/// Long-term leases. Reads need <c>lease.read</c>, each write its own lease permission. Creation and every read (list,
/// detail, registration, checklist, advisory, receipt, exports) authorize the row as a <see cref="HostResource"/> of
/// the lease's property (TN-3): the property owner or an org-wide member of its org with the lease permission; another
/// org's lease is invisible (404), a lease of the org the caller may not handle answers 403. Signing, the provider
/// filing delega and the IMU "sent" attestation still act for the property owner only (services); the manual RLI
/// declaration needs <c>lease.register</c> on the lease (LT-01).
/// Responses are DTOs (LT-11, A7-17): never EF entities, no clear personal data of the parties.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize(Policy = CasazenPolicies.LeaseRead)]
public class LeasesController(
    ILeaseWorkflowService leaseService,
    IComuneImuNotificationService imuNotification,
    ICedolareAdvisoryService cedolareAdvisory,
    IRliExportService rliExport,
    IRliChecklistService rliChecklist,
    IHostResourceLookup hostResources,
    IAuthorizationService authorizationService,
    IOrgContextResolver orgContextResolver,
    IStringLocalizer<SharedResources> localizer) : ControllerBase
{
    private const string LeaseNotFoundCode = "lease_not_found";
    private const string PropertyNotFoundCode = "property_not_found";

    /// <summary>The receipt (at most <see cref="RliRegistrationLimits.MaxReceiptBytes"/>) plus the other form fields.</summary>
    private const long ManualRegistrationRequestLimit = RliRegistrationLimits.MaxReceiptBytes + 64 * 1024;

    private string? GetOwnerId() => User.GetUserId();

    /// <summary>Lease list of the caller's org, restricted to the properties they own unless org-wide (TN-3).</summary>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<LeaseSummaryDto>>> GetAll(
        [FromQuery] Guid? propertyId = null, CancellationToken cancellationToken = default)
    {
        // Org and ownership filter applied in SQL (HostScope), never the whole table filtered in memory.
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken);
        if (orgId is null || User.GetHostScope(orgId.Value) is not { } scope)
            return Unauthorized();

        return Ok(await leaseService.GetLeasesAsync(scope, propertyId));
    }

    /// <summary>Lease detail: parties with masked fiscal code and email, registration, timeline without payloads.</summary>
    [HttpGet("{id:guid}")]
    public async Task<ActionResult<LeaseDetailDto>> GetById(Guid id)
    {
        var (lease, denied) = await AuthorizeLeaseAsync(id, LeaseOperations.Read);
        return denied ?? Ok(LeaseDtoMapper.ToDetail(lease!));
    }

    /// <summary>Create a new lease contract draft.</summary>
    [HttpPost]
    [Authorize(Policy = CasazenPolicies.LeaseCreate)]
    public async Task<IActionResult> Create([FromBody] CreateLeaseDto dto)
    {
        if (GetOwnerId() is null) return Unauthorized();

        var property = await hostResources.ForPropertyAsync(dto.PropertyId, HttpContext.RequestAborted);
        if (property is null)
            return this.ApiProblem(StatusCodes.Status404NotFound, PropertyNotFoundCode, "PropertyNotFound");
        if (!await authorizationService.IsAuthorizedAsync(User, property, LeaseOperations.Create))
            return Forbid();

        try
        {
            var request = new CreateLeaseRequest(
                dto.FiscalRegime,
                dto.StartDate,
                dto.EndDate,
                dto.MonthlyRent,
                dto.Parties.Select(p => new CreatePartyRequest(
                    p.Role, p.FirstName, p.LastName, p.FiscalCode, p.Citizenship, p.ContactEmail)),
                dto.CanoneConcordato is null
                    ? null
                    : new RentBandCharacteristics(
                        dto.CanoneConcordato.Sqm,
                        dto.CanoneConcordato.TypeAElementCount,
                        dto.CanoneConcordato.TypeBElementCount,
                        dto.CanoneConcordato.TypeCElementCount,
                        dto.CanoneConcordato.TypeDElementCount,
                        dto.CanoneConcordato.IsFurnished,
                        dto.CanoneConcordato.ContractYears,
                        dto.CanoneConcordato.ZoneName,
                        dto.CanoneConcordato.CadastralSheet));

            var lease = await leaseService.CreateDraftAsync(dto.PropertyId, request);
            var created = await leaseService.GetLeaseDetailAsync(lease.Id) ?? lease;
            return CreatedAtAction(nameof(GetById), new { id = lease.Id }, LeaseDtoMapper.ToDetail(created));
        }
        catch (ApeComplianceException ex)
        {
            var error = ex.Code == ApeComplianceException.InvalidContentCode
                ? localizer["ApeInvalidContent"].Value
                : ex.Message;
            return BadRequest(new { error, code = ex.Code });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Generate the final contract PDF and initiate digital signing for a lease in Draft status. 422
    /// <c>contract_template_not_approved</c> while the template of the regime is not approved (LT-03).
    /// </summary>
    [HttpPost("{id:guid}/signing")]
    [Authorize(Policy = CasazenPolicies.LeaseSign)]
    public async Task<IActionResult> InitiateSigning(Guid id)
    {
        if (GetOwnerId() is not { } ownerId) return Unauthorized();
        try
        {
            var result = await leaseService.InitiateSigningAsync(id, ownerId);
            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (UnauthorizedAccessException)
        {
            return Forbid();
        }
    }

    /// <summary>
    /// Preview of the contract PDF (LT-03, A7-03): marked "BOZZA - template non approvato" until the template of the
    /// regime is complete and approved, with the missing clause texts and data. Never sent to signature or registration.
    /// Needs <c>lease.sign</c>: the document carries the parties' full fiscal codes.
    /// </summary>
    [HttpGet("{id:guid}/contract/preview")]
    [Authorize(Policy = CasazenPolicies.LeaseSign)]
    public async Task<IActionResult> GetContractPreview(Guid id, [FromServices] ILeaseTemplateService contractTemplates)
    {
        var (lease, denied) = await AuthorizeLeaseAsync(id, LeaseOperations.Sign);
        if (denied is not null)
            return denied;

        var pdf = await contractTemplates.GeneratePreviewPdfAsync(lease!);
        return File(pdf, "application/pdf", $"bozza-contratto-{id}.pdf");
    }

    /// <summary>
    /// Submits a Signed lease to the RLI filing provider on the owner's delega (LT-01). Only with
    /// <c>Features:RliProvider</c> on (404 otherwise) and a configured provider (409 <c>rli_provider_unavailable</c>).
    /// 202: the filing is <b>in progress</b>, not done; the lease is Registered only when the provider returns the
    /// receipt. 502 <c>rli_provider_failed</c>: the failure is recorded (registration Failed, lease Signed) and the
    /// landlord can retry or register manually.
    /// </summary>
    [HttpPost("{id:guid}/registration")]
    [FeatureGate(FeatureFlags.RliProvider)]
    [Authorize(Policy = CasazenPolicies.LeaseRegister)]
    public async Task<IActionResult> TriggerRegistration(
        Guid id,
        [FromBody] TriggerRegistrationDto dto,
        [FromServices] IRliRegistrationService registrations)
    {
        if (GetOwnerId() is not { } ownerId) return Unauthorized();
        var (_, denied) = await AuthorizeLeaseAsync(id, LeaseOperations.Register);
        if (denied is not null)
            return denied;

        try
        {
            var registration = await registrations.SubmitToProviderAsync(
                id, ownerId, new RegistrationAuthorizationRequest(dto.TosVersion, dto.AttestationAccepted));
            return Accepted(new
            {
                leaseId = id,
                registrationStatus = registration.Status.ToString(),
                message = localizer["RliRegistrationAccepted"].Value
            });
        }
        catch (LeaseRegistrationProviderException)
        {
            // Logged with its cause by the service; the client learns only that the provider failed.
            return this.ApiProblem(
                StatusCodes.Status502BadGateway, RliRegistrationErrorCodes.ProviderFailed, "RliProviderFailed");
        }
        catch (ApeComplianceException ex)
        {
            var error = ex.Code == ApeComplianceException.InvalidContentCode
                ? localizer["ApeInvalidContent"].Value
                : ex.Message;
            return BadRequest(new { error, code = ex.Code });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Manual registration (LT-01, default path, D15): the landlord filed the contract on the official channel of the
    /// Agenzia delle Entrate and declares the registration number or protocol, its date and the receipt PDF (private
    /// bucket, FD-07). Only now the lease becomes Registered. Multipart fields: <c>registrationCode</c>,
    /// <c>registrationDate</c>, <c>receipt</c>.
    /// </summary>
    [HttpPost("{id:guid}/registration/manual")]
    [Consumes("multipart/form-data")]
    [Authorize(Policy = CasazenPolicies.LeaseRegister)]
    [RequestSizeLimit(ManualRegistrationRequestLimit)]
    [RequestFormLimits(MultipartBodyLengthLimit = ManualRegistrationRequestLimit)]
    public async Task<ActionResult<LeaseRegistrationDto>> DeclareManualRegistration(
        Guid id,
        [FromForm] ManualRegistrationForm form,
        [FromServices] IRliRegistrationService registrations,
        CancellationToken cancellationToken)
    {
        if (GetOwnerId() is not { } userId) return Unauthorized();
        var (_, denied) = await AuthorizeLeaseAsync(id, LeaseOperations.Register);
        if (denied is not null)
            return denied;

        await using var receipt = form.Receipt!.OpenReadStream();
        var registration = await registrations.DeclareManualRegistrationAsync(
            id,
            userId,
            new ManualRegistrationDeclaration(form.RegistrationCode!, form.RegistrationDate!.Value, receipt, form.Receipt.Length),
            cancellationToken);
        return Ok(LeaseDtoMapper.ToRegistration(registration));
    }

    [HttpGet("{id:guid}/rli/advisory")]
    public async Task<IActionResult> GetRliAdvisory(Guid id, CancellationToken cancellationToken)
    {
        var (_, denied) = await AuthorizeLeaseAsync(id, LeaseOperations.Read);
        if (denied is not null)
            return denied;

        var result = await cedolareAdvisory.EvaluateAsync(id, cancellationToken);
        return result is null ? NotFound() : Ok(result);
    }

    [HttpGet("{id:guid}/rli/export")]
    [Authorize(Policy = CasazenPolicies.LeaseRegister)]
    public async Task<IActionResult> ExportRli(Guid id, CancellationToken cancellationToken)
    {
        var (_, denied) = await AuthorizeLeaseAsync(id, LeaseOperations.Read);
        if (denied is not null)
            return denied;

        var result = await rliExport.ExportAsync(id, cancellationToken);
        return result is null
            ? NotFound()
            : File(result.PdfBytes, "application/pdf", result.FileName);
    }

    /// <summary>RLI checklist; item labels are localized here from their stable keys (A7-26).</summary>
    [HttpGet("{id:guid}/rli/checklist")]
    public async Task<ActionResult<RliChecklistResponse>> GetRliChecklist(Guid id, CancellationToken cancellationToken)
    {
        var (lease, denied) = await AuthorizeLeaseAsync(id, LeaseOperations.Read);
        if (denied is not null)
            return denied;

        var result = await rliChecklist.GetAsync(lease!, cancellationToken);
        return Ok(new RliChecklistResponse(
            result.RegistrationDeadline,
            result.DaysRemaining,
            result.TosVersion,
            result.AttestationText,
            result.ProviderFilingAvailable,
            result.Items.Select(i => new RliChecklistItemResponse(i.Key, ChecklistLabel(i.Key), i.Done, i.Failed)).ToList()));
    }

    /// <summary>Get current RLI registration status.</summary>
    [HttpGet("{id:guid}/registration")]
    public async Task<ActionResult<LeaseRegistrationDto>> GetRegistration(Guid id)
    {
        var (lease, denied) = await AuthorizeLeaseAsync(id, LeaseOperations.Read);
        if (denied is not null)
            return denied;

        if (lease!.Registration is not { } registration)
            return NotFound();

        return Ok(LeaseDtoMapper.ToRegistration(registration));
    }

    /// <summary>
    /// The RLI registration receipt (PDF) from the private bucket (FD-07): only through this authenticated endpoint, for
    /// a caller who may read the lease (TN-3). 404 <c>rli_receipt_not_available</c> until the lease is registered.
    /// </summary>
    [HttpGet("{id:guid}/registration/receipt")]
    public async Task<IActionResult> GetReceipt(
        Guid id,
        [FromServices] IRliRegistrationService registrations,
        CancellationToken cancellationToken)
    {
        var (_, denied) = await AuthorizeLeaseAsync(id, LeaseOperations.Read);
        if (denied is not null)
            return denied;

        var receipt = await registrations.OpenReceiptAsync(id, cancellationToken);
        Response.Headers.CacheControl = "private, no-store";
        return File(receipt.Content, "application/pdf", receipt.FileName);
    }

    /// <summary>Export a draft comune IMU-reduction notification (PDF). Landlord sends it themselves.</summary>
    [HttpGet("{id:guid}/canone-concordato/imu-notification/export")]
    public async Task<IActionResult> ExportImuNotification(Guid id, CancellationToken cancellationToken)
    {
        var (_, denied) = await AuthorizeLeaseAsync(id, LeaseOperations.Read);
        if (denied is not null)
            return denied;

        try
        {
            var result = await imuNotification.ExportAsync(id, cancellationToken);
            return result is null
                ? NotFound()
                : File(result.PdfBytes, "application/pdf", result.FileName);
        }
        catch (ImuNotificationNotReadyException ex)
        {
            return Conflict(new { error = ex.Message });
        }
    }

    /// <summary>Landlord attests they sent the IMU notification. Never inferred.</summary>
    [HttpPost("{id:guid}/canone-concordato/imu-notification/mark-sent")]
    [Authorize(Policy = CasazenPolicies.LeaseRegister)]
    public async Task<IActionResult> MarkImuNotificationSent(Guid id, CancellationToken cancellationToken)
    {
        if (GetOwnerId() is not { } ownerId) return Unauthorized();
        try
        {
            var result = await imuNotification.MarkSentAsync(id, ownerId, cancellationToken);
            return result is null ? NotFound() : NoContent();
        }
        catch (ImuNotificationNotReadyException ex)
        {
            return Conflict(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Loads a lease of the caller's org (tenant filter) and authorizes <paramref name="operation"/> on it as a row of
    /// its property: 404 <c>lease_not_found</c> when it is not visible, 403 when the caller may not handle it.
    /// </summary>
    private async Task<(LeaseContract? Lease, ActionResult? Denied)> AuthorizeLeaseAsync(
        Guid leaseId, HostOperationRequirement operation)
    {
        var lease = await leaseService.GetLeaseDetailAsync(leaseId);
        if (lease is null)
            return (null, this.ApiProblem(StatusCodes.Status404NotFound, LeaseNotFoundCode, "LeaseNotFound"));

        // Without its property the owner is unknown: fail closed rather than treat the lease as an org-level row.
        if (lease.Property is null)
            return (null, Forbid());

        var resource = HostResource.ForProperty(lease.Property) with { OrgId = lease.OrgId };
        return await authorizationService.IsAuthorizedAsync(User, resource, operation)
            ? (lease, null)
            : (null, Forbid());
    }

    private string ChecklistLabel(string key)
    {
        var label = localizer[$"RliChecklist_{key}"];
        return label.ResourceNotFound ? key : label.Value;
    }
}

/// <summary>
/// RLI checklist as returned by the API, with labels in the request language. <c>ProviderFilingAvailable</c>: the
/// provider path exists (flag on and configured provider); otherwise the landlord registers manually (LT-01).
/// </summary>
public record RliChecklistResponse(
    DateTime RegistrationDeadline,
    int DaysRemaining,
    string TosVersion,
    string AttestationText,
    bool ProviderFilingAvailable,
    IReadOnlyList<RliChecklistItemResponse> Items);

/// <summary>A checklist item: <c>Done</c> only when the step happened, <c>Failed</c> when its last attempt failed.</summary>
public record RliChecklistItemResponse(string Key, string Label, bool Done, bool Failed);

public record CreateLeaseDto(
    [param: Required] Guid PropertyId,
    [param: Required, EnumDataType(typeof(FiscalRegime))] FiscalRegime FiscalRegime,
    [param: Required] DateTime StartDate,
    [param: Required] DateTime EndDate,
    [param: Range(0.01, 1_000_000.0)] decimal MonthlyRent,
    [param: Required, MinLength(1)] IEnumerable<CreatePartyDto> Parties,
    CanoneConcordatoCharacteristicsDto? CanoneConcordato = null);

public record CanoneConcordatoCharacteristicsDto(
    [param: Range(1, 10_000)] decimal Sqm,
    [param: Range(0, 100)] int TypeAElementCount,
    [param: Range(0, 100)] int TypeBElementCount,
    [param: Range(0, 100)] int TypeCElementCount,
    [param: Range(0, 100)] int TypeDElementCount,
    bool IsFurnished,
    [param: Range(1, 99)] int ContractYears,
    [param: MaxLength(100)] string? ZoneName,
    [param: MaxLength(100)] string? CadastralSheet);

public record CreatePartyDto(
    [param: Required, EnumDataType(typeof(PartyRole))] PartyRole Role,
    [param: Required, MaxLength(100), MinLength(1)] string FirstName,
    [param: Required, MaxLength(100), MinLength(1)] string LastName,
    [param: Required, MaxLength(16), MinLength(1)] string FiscalCode,
    [param: Required, MaxLength(2), MinLength(2)] string Citizenship,
    [param: Required, EmailAddress] string ContactEmail);

public record TriggerRegistrationDto(
    [param: Required, MaxLength(80)] string TosVersion,
    [param: Required] bool AttestationAccepted);

/// <summary>Manual RLI registration: what the landlord reads on the receipt of the Agenzia delle Entrate, plus the receipt.</summary>
public sealed class ManualRegistrationForm
{
    /// <summary>Registration number or protocol, as written on the receipt.</summary>
    [Required, StringLength(RliRegistrationLimits.MaxRegistrationCodeLength, MinimumLength = 1)]
    public string? RegistrationCode { get; set; }

    /// <summary>Date of the registration (calendar date).</summary>
    [Required]
    public DateTime? RegistrationDate { get; set; }

    /// <summary>The receipt, a PDF of at most 10 MB.</summary>
    [Required]
    public IFormFile? Receipt { get; set; }
}
