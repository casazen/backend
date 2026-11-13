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
    /// <summary>The documents shared with the owners of the org (or with one: <c>ownerId</c>), with their versions, downloads and state.</summary>
    [HttpGet("documents")]
    [ProducesResponseType(typeof(IReadOnlyList<OwnerDocumentAdminView>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<IReadOnlyList<OwnerDocumentAdminView>>> ListDocuments([FromQuery] Guid? ownerId, CancellationToken cancellationToken)
    {
        if (await OrgAsync(cancellationToken) is not { } orgId)
            return Forbidden();

        return Ok(await collaboration.ListDocumentsForAgencyAsync(orgId, ownerId, cancellationToken));
    }

    /// <summary>
    /// Shares a document (multipart: <c>ownerId</c>, <c>propertyId</c>?, <c>type</c> (<c>Mandate</c>, <c>Contract</c>, <c>Insurance</c>, <c>Statement</c>,
    /// <c>Invoice</c>, <c>Other</c>), <c>title</c>, <c>effectiveFrom</c>?, <c>expiresOn</c>?, <c>file</c>). 201. 404 <c>owner_document_owner_not_found</c>;
    /// 413 <c>owner_file_too_large</c>; 422 <c>owner_document_invalid</c>, <c>owner_file_type_not_allowed</c>, <c>owner_file_active_content</c>.
    /// </summary>
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

    /// <summary>Adds a new file to a document (multipart: <c>effectiveFrom</c>?, <c>expiresOn</c>?, <c>file</c>): a new version, the earlier ones kept. 201.</summary>
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

    /// <summary>Withdraws a document from the portal: the owner stops seeing it at the next request. A repeat answers the same.</summary>
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

    /// <summary>Shares again a document that was withdrawn.</summary>
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

    /// <summary>The downloads of a document (the audit), newest first: the version, when, and who.</summary>
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

    /// <summary>The file of a version; the download of a person of the agency is written too.</summary>
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

    /// <summary>The contacts the owners of the org let into their portal (or those of one owner: <c>ownerId</c>), with their permissions and state.</summary>
    [HttpGet("contacts")]
    [ProducesResponseType(typeof(IReadOnlyList<OwnerContactView>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<IReadOnlyList<OwnerContactView>>> ListContacts([FromQuery] Guid? ownerId, CancellationToken cancellationToken)
    {
        if (await OrgAsync(cancellationToken) is not { } orgId)
            return Forbidden();

        return Ok(await collaboration.ListContactsForAgencyAsync(orgId, ownerId, cancellationToken));
    }

    /// <summary>Withdraws a contact: nothing from its next request on. A repeat answers the same.</summary>
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

    /// <summary>The conversations with the owners of the org (or with one: <c>ownerId</c>), the latest first.</summary>
    [HttpGet("conversations")]
    [ProducesResponseType(typeof(IReadOnlyList<OwnerConversationView>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<IReadOnlyList<OwnerConversationView>>> ListConversations([FromQuery] Guid? ownerId, CancellationToken cancellationToken)
    {
        if (await OrgAsync(cancellationToken) is not { } orgId)
            return Forbidden();

        return Ok(await collaboration.ListConversationsForAgencyAsync(orgId, ownerId, cancellationToken));
    }

    /// <summary>
    /// Opens a conversation with an owner: <c>{ ownerId, topic, propertyId?, ownerStayId?, documentId?, statementVersionId?, subject, body }</c>, the reference
    /// of that owner and org. 201. 404 <c>owner_conversation_reference_not_found</c>; 422 <c>owner_conversation_invalid</c>, <c>owner_message_invalid</c>.
    /// </summary>
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

    /// <summary>The messages of a conversation, oldest first.</summary>
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

    /// <summary>Writes a message (multipart: <c>body</c>, optional <c>file</c>). 201. The owner side is told by e-mail, without the words.</summary>
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

    /// <summary>The file of a message.</summary>
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

    /// <summary>The org of the caller when it reaches the whole org; <c>null</c> for a caller restricted to some properties (or with no org).</summary>
    private async Task<Guid?> OrgAsync(CancellationToken cancellationToken)
    {
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken);
        if (orgId is null)
            return null;

        var scope = await hostScopeResolver.ResolveHostScopeAsync(User, orgId.Value, cancellationToken);
        return scope is { IsOrgWide: true } ? orgId : null;
    }

    private ObjectResult Forbidden() => this.ApiProblem(StatusCodes.Status403Forbidden, ProblemCodes.Forbidden, "Forbidden");
}

/// <summary>A document to share: the owner profile, the property (optional), the type, the title, the dates and the file.</summary>
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

/// <summary>A new file of a document, with its dates.</summary>
public sealed class OwnerDocumentVersionForm
{
    public DateOnly? EffectiveFrom { get; set; }

    public DateOnly? ExpiresOn { get; set; }

    public IFormFile? File { get; set; }
}
