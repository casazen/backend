using Casazen.Core.Authorization;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Casazen.Web.Controllers;

/// <summary>
/// The collaboration with the owners as the agency runs it (E07/F07/US01/T02, NF-111): the documents it shares (versions, withdrawal, the download audit),
/// the contacts the owners let in (read and withdraw), and the inbox.
/// </summary>
/// <remarks>
/// <para><b>Policies.</b> <see cref="CasazenPolicies.AgencyOwnerView"/> to read, <see cref="CasazenPolicies.AgencyOwnerManage"/> to share, withdraw and write.
/// A whole-org function, as the owner accounting: a caller restricted to some properties is refused (403) even with the permission, since an owner profile
/// spans properties it does not reach. An item of another org is the same 404 as a missing one.</para>
/// <para>Files of at most 10 MB, PDF, JPEG or PNG by their content, scanned before they are stored. Behind <c>Features:AgencyTier</c>: 404 while it is off.
/// Every answer is private and never cached.</para>
/// </remarks>
[ApiController]
[Route("api/owner-collaboration")]
[Authorize(Policy = CasazenPolicies.AgencyOwnerView)]
[FeatureGate(FeatureFlags.AgencyTier)]
[PrivateAnswer]
[EnableRateLimiting(RateLimitPolicies.OwnerPortalRead)]
public sealed class OwnerCollaborationController(
    IOwnerCollaborationService collaboration,
    IOrgContextResolver orgContextResolver,
    IHostScopeResolver hostScopeResolver) : ControllerBase
{
    [HttpGet("documents")]
    [ProducesResponseType(typeof(IReadOnlyList<OwnerDocumentAdminView>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<IReadOnlyList<OwnerDocumentAdminView>>> ListDocuments([FromQuery] Guid? ownerId, CancellationToken cancellationToken)
    {
        if (await OrgAsync(cancellationToken) is not { } orgId)
            return Forbidden();

        return Ok(await collaboration.ListDocumentsForAgencyAsync(orgId, ownerId, cancellationToken));
    }

    [HttpPost("documents")]
    [Authorize(Policy = CasazenPolicies.AgencyOwnerManage)]
    [EnableRateLimiting(RateLimitPolicies.OwnerPortalWrite)]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(OwnerPortalFiles.MaxRequestBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = OwnerPortalFiles.MaxRequestBytes)]
    [ProducesResponseType(typeof(OwnerDocumentAdminView), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status413PayloadTooLarge)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<OwnerDocumentAdminView>> ShareDocument([FromForm] ShareOwnerDocumentForm form, CancellationToken cancellationToken)
    {
        if (await OrgAsync(cancellationToken) is not { } orgId || User.GetUserId() is not { } userId)
            return Forbidden();

        var (file, problem) = await OwnerPortalFiles.ReadAsync(this, form.File, required: true, cancellationToken);
        if (problem is not null)
            return problem;

        var document = await collaboration.ShareDocumentAsync(
            orgId,
            new ShareOwnerDocument(form.OwnerId, form.PropertyId, form.Type, form.Title, form.EffectiveFrom, form.ExpiresOn),
            file!,
            userId,
            cancellationToken);
        return StatusCode(StatusCodes.Status201Created, document);
    }

    [HttpPost("documents/{documentId:guid}/versions")]
    [Authorize(Policy = CasazenPolicies.AgencyOwnerManage)]
    [EnableRateLimiting(RateLimitPolicies.OwnerPortalWrite)]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(OwnerPortalFiles.MaxRequestBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = OwnerPortalFiles.MaxRequestBytes)]
    [ProducesResponseType(typeof(OwnerDocumentAdminView), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status413PayloadTooLarge)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<OwnerDocumentAdminView>> AddVersion(
        Guid documentId,
        [FromForm] OwnerDocumentVersionForm form,
        CancellationToken cancellationToken)
    {
        if (await OrgAsync(cancellationToken) is not { } orgId || User.GetUserId() is not { } userId)
            return Forbidden();

        var (file, problem) = await OwnerPortalFiles.ReadAsync(this, form.File, required: true, cancellationToken);
        if (problem is not null)
            return problem;

        var document = await collaboration.AddDocumentVersionAsync(
            orgId, documentId, new OwnerDocumentVersionInput(form.EffectiveFrom, form.ExpiresOn), file!, userId, cancellationToken);
        return StatusCode(StatusCodes.Status201Created, document);
    }

    [HttpPost("documents/{documentId:guid}/revoke")]
    [Authorize(Policy = CasazenPolicies.AgencyOwnerManage)]
    [ProducesResponseType(typeof(OwnerDocumentAdminView), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<OwnerDocumentAdminView>> RevokeDocument(Guid documentId, CancellationToken cancellationToken)
    {
        if (await OrgAsync(cancellationToken) is not { } orgId || User.GetUserId() is not { } userId)
            return Forbidden();

        return Ok(await collaboration.SetDocumentVisibilityAsync(orgId, documentId, visible: false, userId, cancellationToken));
    }

    [HttpPost("documents/{documentId:guid}/restore")]
    [Authorize(Policy = CasazenPolicies.AgencyOwnerManage)]
    [ProducesResponseType(typeof(OwnerDocumentAdminView), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<OwnerDocumentAdminView>> RestoreDocument(Guid documentId, CancellationToken cancellationToken)
    {
        if (await OrgAsync(cancellationToken) is not { } orgId || User.GetUserId() is not { } userId)
            return Forbidden();

        return Ok(await collaboration.SetDocumentVisibilityAsync(orgId, documentId, visible: true, userId, cancellationToken));
    }

    [HttpGet("documents/{documentId:guid}/downloads")]
    [ProducesResponseType(typeof(IReadOnlyList<OwnerDocumentDownloadView>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IReadOnlyList<OwnerDocumentDownloadView>>> ListDownloads(Guid documentId, CancellationToken cancellationToken)
    {
        if (await OrgAsync(cancellationToken) is not { } orgId)
            return Forbidden();

        return Ok(await collaboration.ListDocumentDownloadsAsync(orgId, documentId, cancellationToken));
    }

    [HttpGet("documents/{documentId:guid}/versions/{versionId:guid}/file")]
    [EnableRateLimiting(RateLimitPolicies.OwnerPortalDownload)]
    [ProducesResponseType(typeof(FileStreamResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DownloadVersion(Guid documentId, Guid versionId, CancellationToken cancellationToken)
    {
        if (await OrgAsync(cancellationToken) is not { } orgId || User.GetUserId() is not { } userId)
            return Forbidden();

        return OwnerPortalFiles.Download(this, await collaboration.OpenDocumentForAgencyAsync(orgId, documentId, versionId, userId, cancellationToken));
    }

    [HttpGet("contacts")]
    [ProducesResponseType(typeof(IReadOnlyList<OwnerContactView>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<IReadOnlyList<OwnerContactView>>> ListContacts([FromQuery] Guid? ownerId, CancellationToken cancellationToken)
    {
        if (await OrgAsync(cancellationToken) is not { } orgId)
            return Forbidden();

        return Ok(await collaboration.ListContactsForAgencyAsync(orgId, ownerId, cancellationToken));
    }

    [HttpPost("contacts/{contactId:guid}/revoke")]
    [Authorize(Policy = CasazenPolicies.AgencyOwnerManage)]
    [ProducesResponseType(typeof(OwnerContactView), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<OwnerContactView>> RevokeContact(Guid contactId, CancellationToken cancellationToken)
    {
        if (await OrgAsync(cancellationToken) is not { } orgId || User.GetUserId() is not { } userId)
            return Forbidden();

        return Ok(await collaboration.RevokeContactByAgencyAsync(orgId, contactId, userId, cancellationToken));
    }

    [HttpGet("conversations")]
    [ProducesResponseType(typeof(IReadOnlyList<OwnerConversationView>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<IReadOnlyList<OwnerConversationView>>> ListConversations([FromQuery] Guid? ownerId, CancellationToken cancellationToken)
    {
        if (await OrgAsync(cancellationToken) is not { } orgId)
            return Forbidden();

        return Ok(await collaboration.ListConversationsForAgencyAsync(orgId, ownerId, cancellationToken));
    }

    [HttpPost("conversations")]
    [Authorize(Policy = CasazenPolicies.AgencyOwnerManage)]
    [EnableRateLimiting(RateLimitPolicies.OwnerPortalWrite)]
    [ProducesResponseType(typeof(OwnerConversationView), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<OwnerConversationView>> StartConversation([FromBody] StartOwnerConversationBody body, CancellationToken cancellationToken)
    {
        if (await OrgAsync(cancellationToken) is not { } orgId || User.GetUserId() is not { } userId)
            return Forbidden();

        var conversation = await collaboration.StartConversationByAgencyAsync(orgId, body.ToCommand(body.OwnerId), userId, cancellationToken);
        return StatusCode(StatusCodes.Status201Created, conversation);
    }

    [HttpGet("conversations/{conversationId:guid}/messages")]
    [ProducesResponseType(typeof(IReadOnlyList<OwnerMessageView>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IReadOnlyList<OwnerMessageView>>> ListMessages(Guid conversationId, CancellationToken cancellationToken)
    {
        if (await OrgAsync(cancellationToken) is not { } orgId)
            return Forbidden();

        return Ok(await collaboration.ListMessagesForAgencyAsync(orgId, conversationId, cancellationToken));
    }

    [HttpPost("conversations/{conversationId:guid}/messages")]
    [Authorize(Policy = CasazenPolicies.AgencyOwnerManage)]
    [EnableRateLimiting(RateLimitPolicies.OwnerPortalWrite)]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(OwnerPortalFiles.MaxRequestBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = OwnerPortalFiles.MaxRequestBytes)]
    [ProducesResponseType(typeof(OwnerMessageView), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status413PayloadTooLarge)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<OwnerMessageView>> PostMessage(Guid conversationId, [FromForm] OwnerMessageForm form, CancellationToken cancellationToken)
    {
        if (await OrgAsync(cancellationToken) is not { } orgId || User.GetUserId() is not { } userId)
            return Forbidden();

        var (file, problem) = await OwnerPortalFiles.ReadAsync(this, form.File, required: false, cancellationToken);
        if (problem is not null)
            return problem;

        var message = await collaboration.PostMessageByAgencyAsync(orgId, conversationId, form.Body, file, userId, cancellationToken);
        return StatusCode(StatusCodes.Status201Created, message);
    }

    [HttpGet("conversations/{conversationId:guid}/messages/{messageId:guid}/attachment")]
    [EnableRateLimiting(RateLimitPolicies.OwnerPortalDownload)]
    [ProducesResponseType(typeof(FileStreamResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DownloadAttachment(Guid conversationId, Guid messageId, CancellationToken cancellationToken)
    {
        if (await OrgAsync(cancellationToken) is not { } orgId)
            return Forbidden();

        return OwnerPortalFiles.Download(this, await collaboration.OpenAttachmentForAgencyAsync(orgId, conversationId, messageId, cancellationToken));
    }

    private Task<Guid?> OrgAsync(CancellationToken cancellationToken) =>
        HostCallerResolver.OrgWideAsync(User, orgContextResolver, hostScopeResolver, cancellationToken);

    private ObjectResult Forbidden() => this.ApiProblem(StatusCodes.Status403Forbidden, ProblemCodes.Forbidden, "Forbidden");
}

public sealed class ShareOwnerDocumentForm
{
    public Guid OwnerId { get; set; }

    public Guid? PropertyId { get; set; }

    public OwnerDocumentType Type { get; set; } = OwnerDocumentType.Other;

    public string? Title { get; set; }

    public DateOnly? EffectiveFrom { get; set; }

    public DateOnly? ExpiresOn { get; set; }

    public IFormFile? File { get; set; }
}

public sealed class OwnerDocumentVersionForm
{
    public DateOnly? EffectiveFrom { get; set; }

    public DateOnly? ExpiresOn { get; set; }

    public IFormFile? File { get; set; }
}
