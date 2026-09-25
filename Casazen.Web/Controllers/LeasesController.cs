using System.ComponentModel.DataAnnotations;
using Casazen.Core.Authorization;
using Casazen.Core.DTOs.Leases;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
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
/// org's lease is invisible (404), a lease of the org the caller may not handle answers 403. The signature endpoints
/// (LT-02) need <c>lease.sign</c> on the lease, the signed contract and the signers <c>lease.read</c>; the provider
/// filing delega and the IMU "sent" attestation still act for the property owner only (services); the manual RLI
/// declaration needs <c>lease.register</c> on the lease (LT-01), as do the Questura declaration and the delivery date of
/// the property (LT-07).
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
    IStringLocalizer<SharedResources> localizer,
    TimeProvider? timeProvider = null) : ControllerBase
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    private const string LeaseNotFoundCode = "lease_not_found";
    private const string PropertyNotFoundCode = "property_not_found";

    /// <summary>The receipt (at most <see cref="RliRegistrationLimits.MaxReceiptBytes"/>) plus the other form fields.</summary>
    private const long ManualRegistrationRequestLimit = RliRegistrationLimits.MaxReceiptBytes + 64 * 1024;

    /// <summary>The signed contract (at most <see cref="LeaseSigningLimits.MaxSignedContractBytes"/>) plus the other form fields.</summary>
    private const long SignedDocumentRequestLimit = LeaseSigningLimits.MaxSignedContractBytes + 64 * 1024;

    /// <summary>The Questura receipt (at most <see cref="QuesturaCommunicationLimits.MaxReceiptBytes"/>) plus the other form fields.</summary>
    private const long QuesturaRequestLimit = QuesturaCommunicationLimits.MaxReceiptBytes + 64 * 1024;

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
        return denied ?? Ok(LeaseDtoMapper.ToDetail(lease!, _clock.TodayInRome()));
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
                dto.CanoneConcordato?.ToCharacteristics())
            {
                ContractType = dto.ContractType,
                TaxRegime = dto.TaxRegime,
                SecurityDeposit = dto.SecurityDeposit,
            };

            var lease = await leaseService.CreateDraftAsync(dto.PropertyId, request);
            var created = await leaseService.GetLeaseDetailAsync(lease.Id) ?? lease;
            return CreatedAtAction(nameof(GetById), new { id = lease.Id }, LeaseDtoMapper.ToDetail(created, _clock.TodayInRome()));
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
    /// Provider signature (LT-02): sends the final contract to the e-signature provider and returns the personal signing
    /// links, persisted (<c>GET signers</c>). Only with <c>Features:ESignProvider</c> on (404 otherwise) and a configured
    /// provider (409 <c>esign_provider_unavailable</c>); 422 <c>contract_template_not_approved</c> while the template is
    /// not approved (LT-03); 502 <c>esign_provider_failed</c> when the provider refuses. The default is the offline
    /// signature (<c>contract.pdf</c> + <c>signed-document</c>).
    /// </summary>
    [HttpPost("{id:guid}/signing")]
    [FeatureGate(FeatureFlags.ESignProvider)]
    [Authorize(Policy = CasazenPolicies.LeaseSign)]
    public async Task<ActionResult<SigningInitiatedDto>> InitiateSigning(
        Guid id, [FromServices] ILeaseSigningService signing, CancellationToken cancellationToken)
    {
        var (_, denied) = await AuthorizeLeaseAsync(id, LeaseOperations.Sign);
        if (denied is not null)
            return denied;

        try
        {
            var signers = await signing.InitiateProviderSigningAsync(id, cancellationToken);
            return Ok(new SigningInitiatedDto(id, LeaseStatus.AwaitingSignature, signers));
        }
        catch (ESignProviderException)
        {
            // Logged with its cause by the service; the client learns only that the provider failed.
            return this.ApiProblem(StatusCodes.Status502BadGateway, LeaseSigningErrorCodes.ProviderFailed, "ESignProviderFailed");
        }
        catch (InvalidOperationException ex)
        {
            return SignatureRuleProblem(ex);
        }
    }

    /// <summary>
    /// The final contract to be signed offline (LT-02): only from a complete, approved template (422
    /// <c>contract_template_not_approved</c> / <c>contract_data_missing</c>, LT-03; the BOZZA is <c>contract/preview</c>),
    /// only before every party signed (409 <c>lease_already_signed</c>). Needs <c>lease.sign</c>: the document carries
    /// the parties' full fiscal codes.
    /// </summary>
    [HttpGet("{id:guid}/contract.pdf")]
    [Authorize(Policy = CasazenPolicies.LeaseSign)]
    public async Task<IActionResult> GetContractForSignature(
        Guid id, [FromServices] ILeaseSigningService signing, CancellationToken cancellationToken)
    {
        var (_, denied) = await AuthorizeLeaseAsync(id, LeaseOperations.Sign);
        if (denied is not null)
            return denied;

        try
        {
            var pdf = await signing.GenerateContractForSignatureAsync(id, cancellationToken);
            Response.Headers.CacheControl = "private, no-store";
            return File(pdf, "application/pdf", $"contratto-{id}.pdf");
        }
        catch (InvalidOperationException ex)
        {
            return SignatureRuleProblem(ex);
        }
    }

    /// <summary>
    /// Offline signature (LT-02, default path, D15): the landlord uploads the contract signed by every party (PDF, checked
    /// on its content, at most 20 MB, private bucket, FD-07) and declares the stipula date (not after today). Only now
    /// the lease is Signed, the stipula recorded and the RLI deadline fixed (LT-04). Multipart fields:
    /// <c>stipulaDate</c>, <c>signedContract</c>.
    /// </summary>
    [HttpPost("{id:guid}/signed-document")]
    [Consumes("multipart/form-data")]
    [Authorize(Policy = CasazenPolicies.LeaseSign)]
    [RequestSizeLimit(SignedDocumentRequestLimit)]
    [RequestFormLimits(MultipartBodyLengthLimit = SignedDocumentRequestLimit)]
    public async Task<ActionResult<LeaseDetailDto>> DeclareOfflineSignature(
        Guid id,
        [FromForm] SignedDocumentForm form,
        [FromServices] ILeaseSigningService signing,
        CancellationToken cancellationToken)
    {
        if (GetOwnerId() is not { } userId) return Unauthorized();
        var (_, denied) = await AuthorizeLeaseAsync(id, LeaseOperations.Sign);
        if (denied is not null)
            return denied;

        try
        {
            await using var signedContract = form.SignedContract!.OpenReadStream();
            await signing.DeclareOfflineSignatureAsync(
                id,
                userId,
                new OfflineSignatureDeclaration(form.StipulaDate!.Value, signedContract, form.SignedContract.Length),
                cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            return SignatureRuleProblem(ex);
        }

        return Ok(LeaseDtoMapper.ToDetail((await leaseService.GetLeaseDetailAsync(id))!, _clock.TodayInRome()));
    }

    /// <summary>
    /// The contract signed by every party, from the private bucket (FD-07): only through this authenticated endpoint,
    /// for a caller who may read the lease (TN-3: the owner or an org-wide member of its org). 404
    /// <c>lease_signed_contract_not_available</c> until a signed contract is stored.
    /// </summary>
    [HttpGet("{id:guid}/signed-document")]
    public async Task<IActionResult> GetSignedDocument(
        Guid id, [FromServices] ILeaseSigningService signing, CancellationToken cancellationToken)
    {
        var (_, denied) = await AuthorizeLeaseAsync(id, LeaseOperations.Read);
        if (denied is not null)
            return denied;

        var file = await signing.OpenSignedContractAsync(id, cancellationToken);
        Response.Headers.CacheControl = "private, no-store";
        return File(file.Content, "application/pdf", file.FileName);
    }

    /// <summary>
    /// Stipula date of a lease already signed whose signature was never recorded (older leases, LT-02): fixes the RLI
    /// deadline (LT-04). 409 <c>lease_stipula_already_recorded</c>, 422 <c>lease_stipula_lease_not_signed</c> before the
    /// signature (use <c>signed-document</c>), 422 <c>lease_stipula_date_in_future</c>.
    /// </summary>
    [HttpPost("{id:guid}/stipula")]
    [Authorize(Policy = CasazenPolicies.LeaseSign)]
    public async Task<ActionResult<LeaseDetailDto>> DeclareStipula(
        Guid id,
        [FromBody] DeclareStipulaDto dto,
        [FromServices] ILeaseSigningService signing,
        CancellationToken cancellationToken)
    {
        if (GetOwnerId() is not { } userId) return Unauthorized();
        var (_, denied) = await AuthorizeLeaseAsync(id, LeaseOperations.Sign);
        if (denied is not null)
            return denied;

        await signing.DeclareStipulaAsync(id, userId, dto.StipulaDate!.Value, cancellationToken);
        return Ok(LeaseDtoMapper.ToDetail((await leaseService.GetLeaseDetailAsync(id))!, _clock.TodayInRome()));
    }

    /// <summary>
    /// The signature panel (LT-02, A7-16): the parties as signers, persisted so the provider links survive a refresh;
    /// whether the provider path exists; whether the final contract to sign can be downloaded now (otherwise the reason:
    /// template not approved or data missing, LT-03, or already signed). Offline, each party is "to be signed on paper or
    /// PDF" until the signed contract is uploaded. The provider link is returned only to a caller who may sign the lease.
    /// </summary>
    [HttpGet("{id:guid}/signers")]
    public async Task<ActionResult<LeaseSigningStateDto>> GetSigners(
        Guid id, [FromServices] ILeaseSigningService signing, CancellationToken cancellationToken)
    {
        var (lease, denied) = await AuthorizeLeaseAsync(id, LeaseOperations.Read);
        if (denied is not null)
            return denied;

        var state = await signing.GetSigningStateAsync(id, cancellationToken);
        var resource = HostResource.ForProperty(lease!.Property) with { OrgId = lease.OrgId };
        if (await authorizationService.IsAuthorizedAsync(User, resource, LeaseOperations.Sign))
            return Ok(state);

        return Ok(state with { Signers = state.Signers.Select(s => s with { SigningUrl = null }).ToList() });
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

    /// <summary>
    /// Tax advisory of the lease (LT-08): cedolare secca against the ordinary regime, from the configured parameters. The
    /// stamp duty and the IRPEF comparison need data CasaZen does not hold: here they come back as "input required".
    /// </summary>
    [HttpGet("{id:guid}/rli/advisory")]
    public Task<ActionResult<CedolareAdvisoryResult>> GetRliAdvisory(Guid id, CancellationToken cancellationToken) =>
        EvaluateAdvisoryAsync(id, CedolareAdvisoryInput.None, cancellationToken);

    /// <summary>
    /// The same advisory computed with the data the landlord gives (pages and copies of the contract, other taxable
    /// income). A calculation, not a write: it needs <c>lease.read</c> like the GET. POST so that the income never ends up
    /// in a URL or an access log; nothing is stored.
    /// </summary>
    [HttpPost("{id:guid}/rli/advisory")]
    public Task<ActionResult<CedolareAdvisoryResult>> EvaluateRliAdvisory(
        Guid id, [FromBody] CedolareAdvisoryRequest request, CancellationToken cancellationToken) =>
        EvaluateAdvisoryAsync(
            id,
            new CedolareAdvisoryInput(request.WrittenPages, request.Lines, request.Copies, request.OtherTaxableIncomeEur),
            cancellationToken);

    private async Task<ActionResult<CedolareAdvisoryResult>> EvaluateAdvisoryAsync(
        Guid id, CedolareAdvisoryInput input, CancellationToken cancellationToken)
    {
        var (_, denied) = await AuthorizeLeaseAsync(id, LeaseOperations.Read);
        if (denied is not null)
            return denied;

        var result = await cedolareAdvisory.EvaluateAsync(id, input, cancellationToken);
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
            result.Items.Select(i => new RliChecklistItemResponse(i.Key, ChecklistLabel(i.Key), i.Done, i.Failed)).ToList(),
            result.Questura));
    }

    /// <summary>
    /// Delivery date of the property (LT-07): the 48 hours of the Questura communication for an extra-EU tenant count
    /// from it; without it the start date applies. <c>null</c> clears it. 422 <c>questura_delivery_date_after_end</c>.
    /// Returns the updated checklist.
    /// </summary>
    [HttpPut("{id:guid}/rli/questura/delivery-date")]
    [Authorize(Policy = CasazenPolicies.LeaseRegister)]
    public async Task<ActionResult<RliChecklistResponse>> DeclareDeliveryDate(
        Guid id,
        [FromBody] DeclareDeliveryDateDto dto,
        [FromServices] IQuesturaCommunicationService questura,
        CancellationToken cancellationToken)
    {
        if (GetOwnerId() is not { } userId) return Unauthorized();
        var (_, denied) = await AuthorizeLeaseAsync(id, LeaseOperations.Register);
        if (denied is not null)
            return denied;

        await questura.DeclareDeliveryDateAsync(id, userId, dto.DeliveryDate, cancellationToken);
        return await GetRliChecklist(id, cancellationToken);
    }

    /// <summary>
    /// The landlord declares they sent the communication to the public-security authority for an extra-EU tenant
    /// (art. 7 D.Lgs. 286/1998, LT-07, A7-08): the date (not after today) and, optionally, the receipt (PDF, at most
    /// 10 MB, private bucket, FD-07). Only this ticks the checklist item (event <c>QuesturaCommunicationMarkedDone</c>).
    /// 422 <c>questura_not_required</c> / <c>questura_communication_date_in_future</c> / <c>questura_receipt_invalid</c>,
    /// 409 <c>questura_already_marked_done</c>. Multipart fields: <c>communicationDate</c>, <c>receipt</c> (optional).
    /// Returns the updated checklist.
    /// </summary>
    [HttpPost("{id:guid}/rli/questura/mark-done")]
    [Consumes("multipart/form-data")]
    [Authorize(Policy = CasazenPolicies.LeaseRegister)]
    [RequestSizeLimit(QuesturaRequestLimit)]
    [RequestFormLimits(MultipartBodyLengthLimit = QuesturaRequestLimit)]
    public async Task<ActionResult<RliChecklistResponse>> MarkQuesturaCommunicationDone(
        Guid id,
        [FromForm] QuesturaCommunicationForm form,
        [FromServices] IQuesturaCommunicationService questura,
        CancellationToken cancellationToken)
    {
        if (GetOwnerId() is not { } userId) return Unauthorized();
        var (_, denied) = await AuthorizeLeaseAsync(id, LeaseOperations.Register);
        if (denied is not null)
            return denied;

        await using var receipt = form.Receipt?.OpenReadStream();
        await questura.MarkDoneAsync(
            id,
            userId,
            new QuesturaCommunicationDeclaration(form.CommunicationDate!.Value, receipt, form.Receipt?.Length),
            cancellationToken);
        return await GetRliChecklist(id, cancellationToken);
    }

    /// <summary>
    /// The receipt of the Questura communication from the private bucket (FD-07): only through this authenticated
    /// endpoint, for a caller who may read the lease (TN-3). 404 <c>questura_receipt_not_available</c> without one.
    /// </summary>
    [HttpGet("{id:guid}/rli/questura/receipt")]
    public async Task<IActionResult> GetQuesturaReceipt(
        Guid id, [FromServices] IQuesturaCommunicationService questura, CancellationToken cancellationToken)
    {
        var (_, denied) = await AuthorizeLeaseAsync(id, LeaseOperations.Read);
        if (denied is not null)
            return denied;

        var receipt = await questura.OpenReceiptAsync(id, cancellationToken);
        Response.Headers.CacheControl = "private, no-store";
        return File(receipt.Content, "application/pdf", receipt.FileName);
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

    /// <summary>
    /// Pre-signature checks that still throw <see cref="InvalidOperationException"/> (APE): 400 with the APE code, as for
    /// the lease creation. The term of the contract type is a domain rule (422, LT-10).
    /// </summary>
    private BadRequestObjectResult SignatureRuleProblem(InvalidOperationException ex) =>
        ex is ApeComplianceException ape
            ? BadRequest(new
            {
                error = ape.Code == ApeComplianceException.InvalidContentCode ? localizer["ApeInvalidContent"].Value : ape.Message,
                code = ape.Code,
            })
            : BadRequest(new { error = ex.Message });

    private string ChecklistLabel(string key)
    {
        var label = localizer[$"RliChecklist_{key}"];
        return label.ResourceNotFound ? key : label.Value;
    }
}

/// <summary>
/// RLI checklist as returned by the API, with labels in the request language. <c>ProviderFilingAvailable</c>: the
/// provider path exists (flag on and configured provider); otherwise the landlord registers manually (LT-01).
/// <c>RegistrationDeadline</c> and <c>DaysRemaining</c> are null while the deadline is to be determined (LT-04);
/// <c>DaysRemaining</c> is 0 on the deadline day and negative once it has passed. <c>Questura</c>: the communication to
/// the public-security authority for an extra-EU tenant (LT-07), null when no tenant is extra-EU.
/// </summary>
public record RliChecklistResponse(
    DateTime? RegistrationDeadline,
    int? DaysRemaining,
    string TosVersion,
    string AttestationText,
    bool ProviderFilingAvailable,
    IReadOnlyList<RliChecklistItemResponse> Items,
    QuesturaCommunicationStatus? Questura);

/// <summary>A checklist item: <c>Done</c> only when the step happened, <c>Failed</c> when its last attempt failed.</summary>
public record RliChecklistItemResponse(string Key, string Label, bool Done, bool Failed);

/// <summary>
/// A new lease (LT-10). <c>ContractType</c> (Libero 4+4, Concordato 3+2, Transitorio 1-18 months) and <c>TaxRegime</c>
/// replace the legacy <c>FiscalRegime</c>, accepted alone from older clients. The term comes from the dates. A canone
/// concordato lease needs <c>CanoneConcordato</c>: the server computes the range from it (422
/// <c>concordato_rent_out_of_range</c> with verified agreement data; with unconfirmed data the range is indicative and
/// the lease is created, A7-23).
/// </summary>
public record CreateLeaseDto(
    [param: Required] Guid PropertyId,
    [param: EnumDataType(typeof(FiscalRegime))] FiscalRegime? FiscalRegime,
    [param: Required] DateTime StartDate,
    [param: Required] DateTime EndDate,
    [param: Range(0.01, 1_000_000.0)] decimal MonthlyRent,
    [param: Required, MinLength(1)] IEnumerable<CreatePartyDto> Parties,
    CanoneConcordatoCharacteristicsDto? CanoneConcordato = null,
    [param: EnumDataType(typeof(LeaseContractType))] LeaseContractType? ContractType = null,
    [param: EnumDataType(typeof(LeaseTaxRegime))] LeaseTaxRegime? TaxRegime = null,
    [param: Range(0.0, 1_000_000.0)] decimal? SecurityDeposit = null);

/// <summary>
/// Characteristics of the unit for the canone concordato range (LT-10). No contract years: the term comes from the lease
/// dates (A7-12). Appurtenances in square metres; element counts as defined by the territorial agreement.
/// </summary>
public record CanoneConcordatoCharacteristicsDto(
    [param: Range(1, 10_000)] decimal Sqm,
    [param: Range(0, 100)] int TypeAElementCount,
    [param: Range(0, 100)] int TypeBElementCount,
    [param: Range(0, 100)] int TypeCElementCount,
    [param: Range(0, 100)] int TypeDElementCount,
    bool IsFurnished,
    [param: MaxLength(100)] string? ZoneName,
    [param: MaxLength(20)] string? CadastralSheet,
    [param: Range(0, 100)] int QualifyingTypeDElementCount = 0,
    bool StoveHeating = false,
    bool AirConditioning = false,
    [param: Range(0, 10_000)] decimal GarageSqm = 0,
    [param: Range(0, 10_000)] decimal BalconySqm = 0,
    [param: Range(0, 10_000)] decimal OtherAppurtenanceSqm = 0,
    [param: Range(0, 100_000)] decimal PrivateGreenSqm = 0)
{
    public RentBandCharacteristics ToCharacteristics() => new()
    {
        Sqm = Sqm,
        GarageSqm = GarageSqm,
        BalconySqm = BalconySqm,
        OtherAppurtenanceSqm = OtherAppurtenanceSqm,
        PrivateGreenSqm = PrivateGreenSqm,
        TypeAElementCount = TypeAElementCount,
        TypeBElementCount = TypeBElementCount,
        TypeCElementCount = TypeCElementCount,
        TypeDElementCount = TypeDElementCount,
        QualifyingTypeDElementCount = QualifyingTypeDElementCount,
        StoveHeating = StoveHeating,
        IsFurnished = IsFurnished,
        AirConditioning = AirConditioning,
        ZoneName = ZoneName,
        CadastralSheet = CadastralSheet,
    };
}

public record CreatePartyDto(
    [param: Required, EnumDataType(typeof(PartyRole))] PartyRole Role,
    [param: Required, MaxLength(100), MinLength(1)] string FirstName,
    [param: Required, MaxLength(100), MinLength(1)] string LastName,
    [param: Required, MaxLength(16), MinLength(1)] string FiscalCode,
    [param: Required, MaxLength(2), MinLength(2), RegularExpression("^[A-Za-z]{2}$", ErrorMessage = "LeasePartyCitizenshipInvalid")] string Citizenship,
    [param: Required, EmailAddress] string ContactEmail);

/// <summary>Stipula date of a lease already signed (calendar date, not after today).</summary>
public record DeclareStipulaDto([param: Required] DateTime? StipulaDate);

/// <summary>Delivery date of the property (calendar date, not after the end of the lease); null clears it (LT-07).</summary>
public record DeclareDeliveryDateDto(DateTime? DeliveryDate);

/// <summary>
/// Questura communication declared by the landlord (LT-07): the date it was sent (calendar date, not after today) and,
/// optionally, the receipt (PDF of at most 10 MB).
/// </summary>
public sealed class QuesturaCommunicationForm
{
    [Required]
    public DateTime? CommunicationDate { get; set; }

    public IFormFile? Receipt { get; set; }
}

/// <summary>Offline signature: the stipula date and the contract signed by every party.</summary>
public sealed class SignedDocumentForm
{
    /// <summary>Date of the stipula: the day the last party signed (calendar date, not after today).</summary>
    [Required]
    public DateTime? StipulaDate { get; set; }

    /// <summary>The contract signed by every party, a PDF of at most 20 MB.</summary>
    [Required]
    public IFormFile? SignedContract { get; set; }
}

public record TriggerRegistrationDto(
    [param: Required, MaxLength(80)] string TosVersion,
    [param: Required] bool AttestationAccepted);

/// <summary>
/// Data of the tax advisory that CasaZen does not hold (LT-08); each may be omitted. The stamp duty needs the written
/// pages and the copies, the IRPEF comparison the landlord's taxable income of the year without this rent.
/// </summary>
public sealed record CedolareAdvisoryRequest(
    [param: Range(1, 1_000)] int? WrittenPages,
    [param: Range(1, 100_000)] int? Lines,
    [param: Range(1, 20)] int? Copies,
    [param: Range(0d, 100_000_000d)] decimal? OtherTaxableIncomeEur);

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
