using Casazen.Core.Entities.Enums;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.Configuration;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Casazen.Web.Controllers;

/// <summary>
/// The collaboration of the owner portal (E07/F07/US01/T02, NF-111, ADR-011): the documents the agency shares, the contacts the owner lets in with their own
/// account and explicit permissions, and the inbox with the agency.
/// </summary>
/// <remarks>
/// <para><b>Who.</b> The owner portal policy; what the caller reaches is resolved again from the database at every request
/// (<see cref="IOwnerPortalAccessResolver"/>), so a withdrawn contact or an ended ownership loses the access at the next call. A document, a contact or a
/// conversation of another owner or agency is the same 404 as a missing one; a permission the caller holds nowhere is 403
/// <c>owner_portal_permission_missing</c>; only an owner manages its contacts (403 <c>owner_contact_owner_only</c>).</para>
/// <para><b>Files.</b> At most 10 MB, PDF, JPEG or PNG by their content, scanned before they are stored; every download of a document is written before the
/// file is handed out. Behind <c>Features:AgencyTier</c>: 404 while it is off. Every answer is private and never cached.</para>
/// </remarks>
[ApiController]
[Route("api/owner-portal")]
[Authorize(Policy = CasazenPolicies.OwnerPortal)]
[FeatureGate(FeatureFlags.AgencyTier)]
[PrivateAnswer]
public sealed class OwnerPortalCollaborationController(
    IOwnerPortalAccessResolver accessResolver,
    IOwnerCollaborationService collaboration,
    IOptions<Auth0Options> auth0Options) : ControllerBase
{
    private readonly string? _customClaimNamespace = auth0Options.Value.CustomClaimNamespace;

    /// <summary>
    /// The documents the caller may read (<c>documents.read</c>; a statement or an invoice also <c>accounting.read</c>), each with its versions and its
    /// expiry state, and the published statement versions of the owner accounting (<c>accounting.read</c>), referenced where they are.
    /// </summary>
    [HttpGet("documents")]
    [ProducesResponseType(typeof(OwnerPortalDocuments), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<OwnerPortalDocuments>> ListDocuments(CancellationToken cancellationToken)
    {
        if (await AccessAsync(cancellationToken) is not { } access)
            return Unauthorized();

        return Ok(await collaboration.ListDocumentsForOwnerAsync(access, cancellationToken));
    }

    /// <summary>The file of a document: the version in force, or <c>versionId</c>. The download is written first (the audit). 404 <c>owner_document_not_found</c>.</summary>
    [HttpGet("documents/{documentId:guid}/file")]
    [ProducesResponseType(typeof(FileStreamResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DownloadDocument(Guid documentId, [FromQuery] Guid? versionId, CancellationToken cancellationToken)
    {
        if (await AccessAsync(cancellationToken) is not { } access)
            return Unauthorized();

        return OwnerPortalFiles.Download(this, await collaboration.OpenDocumentForOwnerAsync(access, documentId, versionId, cancellationToken));
    }

    /// <summary>The contacts of the owner profiles the caller owns, with their permissions and state (<c>invited</c>, <c>expired</c>, <c>active</c>, <c>revoked</c>).</summary>
    [HttpGet("contacts")]
    [ProducesResponseType(typeof(IReadOnlyList<OwnerContactView>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<IReadOnlyList<OwnerContactView>>> ListContacts(CancellationToken cancellationToken)
    {
        if (await AccessAsync(cancellationToken) is not { } access)
            return Unauthorized();

        return Ok(await collaboration.ListContactsForOwnerAsync(access, cancellationToken));
    }

    /// <summary>
    /// Invites a contact with its own account: <c>{ agencyId?, email, firstName, lastName, permissions: ["calendar.read", "stays.request", "accounting.read",
    /// "documents.read", "inbox.use"] }</c>. 201. 422 <c>owner_portal_permissions_empty</c> (never «all»), <c>owner_portal_permission_unknown</c>,
    /// <c>owner_contact_invalid</c>, <c>owner_contact_agency_required</c>, <c>owner_contact_limit_reached</c>, <c>owner_contact_email_not_queued</c>; 409
    /// <c>owner_contact_exists</c>.
    /// </summary>
    [HttpPost("contacts")]
    [ProducesResponseType(typeof(OwnerContactView), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<OwnerContactView>> InviteContact([FromBody] InviteOwnerContactBody body, CancellationToken cancellationToken)
    {
        if (await AccessAsync(cancellationToken) is not { } access)
            return Unauthorized();

        var contact = await collaboration.InviteContactAsync(
            access, new InviteOwnerContact(body.AgencyId, body.Email, body.FirstName, body.LastName, body.Permissions), cancellationToken);
        return StatusCode(StatusCodes.Status201Created, contact);
    }

    /// <summary>Changes what a contact may do: <c>{ permissions: [...] }</c>, at least one. Effective at its next request.</summary>
    [HttpPut("contacts/{contactId:guid}/permissions")]
    [ProducesResponseType(typeof(OwnerContactView), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<OwnerContactView>> SetContactPermissions(
        Guid contactId,
        [FromBody] OwnerContactPermissionsBody body,
        CancellationToken cancellationToken)
    {
        if (await AccessAsync(cancellationToken) is not { } access)
            return Unauthorized();

        return Ok(await collaboration.SetContactPermissionsAsync(access, contactId, body.Permissions, cancellationToken));
    }

    /// <summary>Withdraws a contact: nothing from its next request on. A repeat answers the same.</summary>
    [HttpPost("contacts/{contactId:guid}/revoke")]
    [ProducesResponseType(typeof(OwnerContactView), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<OwnerContactView>> RevokeContact(Guid contactId, CancellationToken cancellationToken)
    {
        if (await AccessAsync(cancellationToken) is not { } access)
            return Unauthorized();

        return Ok(await collaboration.RevokeContactByOwnerAsync(access, contactId, cancellationToken));
    }

    /// <summary>The conversations with the agency the caller may read (<c>inbox.use</c>), the latest first.</summary>
    [HttpGet("conversations")]
    [ProducesResponseType(typeof(IReadOnlyList<OwnerConversationView>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<IReadOnlyList<OwnerConversationView>>> ListConversations(CancellationToken cancellationToken)
    {
        if (await AccessAsync(cancellationToken) is not { } access)
            return Unauthorized();

        return Ok(await collaboration.ListConversationsForOwnerAsync(access, cancellationToken));
    }

    /// <summary>
    /// Opens a conversation: <c>{ agencyId?, topic: "general" | "property" | "ownerStay" | "document" | "statementVersion", propertyId?, ownerStayId?,
    /// documentId?, statementVersionId?, subject, body }</c>, exactly the reference the topic names. 201. 404 <c>owner_conversation_reference_not_found</c>;
    /// 422 <c>owner_conversation_invalid</c>, <c>owner_message_invalid</c>.
    /// </summary>
    [HttpPost("conversations")]
    [ProducesResponseType(typeof(OwnerConversationView), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<OwnerConversationView>> StartConversation([FromBody] StartOwnerConversationBody body, CancellationToken cancellationToken)
    {
        if (await AccessAsync(cancellationToken) is not { } access)
            return Unauthorized();

        var conversation = await collaboration.StartConversationByOwnerAsync(access, body.ToCommand(null), cancellationToken);
        return StatusCode(StatusCodes.Status201Created, conversation);
    }

    /// <summary>The messages of a conversation, oldest first. 404 <c>owner_conversation_not_found</c>.</summary>
    [HttpGet("conversations/{conversationId:guid}/messages")]
    [ProducesResponseType(typeof(IReadOnlyList<OwnerMessageView>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IReadOnlyList<OwnerMessageView>>> ListMessages(Guid conversationId, CancellationToken cancellationToken)
    {
        if (await AccessAsync(cancellationToken) is not { } access)
            return Unauthorized();

        return Ok(await collaboration.ListMessagesForOwnerAsync(access, conversationId, cancellationToken));
    }

    /// <summary>
    /// Writes a message (multipart: <c>body</c>, optional <c>file</c> of at most 10 MB, PDF, JPEG or PNG). 201. 413 <c>owner_file_too_large</c>; 422
    /// <c>owner_message_invalid</c>, <c>owner_file_type_not_allowed</c>, <c>owner_file_active_content</c>.
    /// </summary>
    [HttpPost("conversations/{conversationId:guid}/messages")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(OwnerPortalFiles.MaxRequestBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = OwnerPortalFiles.MaxRequestBytes)]
    [ProducesResponseType(typeof(OwnerMessageView), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status413PayloadTooLarge)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<OwnerMessageView>> PostMessage(Guid conversationId, [FromForm] OwnerMessageForm form, CancellationToken cancellationToken)
    {
        if (await AccessAsync(cancellationToken) is not { } access)
            return Unauthorized();

        var (file, problem) = await OwnerPortalFiles.ReadAsync(this, form.File, required: false, cancellationToken);
        if (problem is not null)
            return problem;

        var message = await collaboration.PostMessageByOwnerAsync(access, conversationId, form.Body, file, cancellationToken);
        return StatusCode(StatusCodes.Status201Created, message);
    }

    /// <summary>The file of a message. 404 <c>owner_attachment_not_found</c>.</summary>
    [HttpGet("conversations/{conversationId:guid}/messages/{messageId:guid}/attachment")]
    [ProducesResponseType(typeof(FileStreamResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DownloadAttachment(Guid conversationId, Guid messageId, CancellationToken cancellationToken)
    {
        if (await AccessAsync(cancellationToken) is not { } access)
            return Unauthorized();

        return OwnerPortalFiles.Download(this, await collaboration.OpenAttachmentForOwnerAsync(access, conversationId, messageId, cancellationToken));
    }

    /// <summary>Whether the caller gets an e-mail when the agency writes.</summary>
    [HttpGet("inbox/preferences")]
    [ProducesResponseType(typeof(OwnerInboxPreferences), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<OwnerInboxPreferences>> GetInboxPreferences(CancellationToken cancellationToken)
    {
        if (User.GetOwnerUserId(_customClaimNamespace) is not { } ownerUserId)
            return Unauthorized();

        return Ok(await collaboration.GetInboxPreferencesAsync(ownerUserId, cancellationToken));
    }

    /// <summary>Sets whether the caller gets an e-mail when the agency writes: <c>{ emailNotifications }</c>.</summary>
    [HttpPut("inbox/preferences")]
    [ProducesResponseType(typeof(OwnerInboxPreferences), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<OwnerInboxPreferences>> SetInboxPreferences([FromBody] OwnerInboxPreferences body, CancellationToken cancellationToken)
    {
        if (User.GetOwnerUserId(_customClaimNamespace) is not { } ownerUserId)
            return Unauthorized();

        return Ok(await collaboration.SetInboxPreferencesAsync(ownerUserId, body.EmailNotifications, cancellationToken));
    }

    private async Task<OwnerPortalAccess?> AccessAsync(CancellationToken cancellationToken) =>
        User.GetOwnerUserId(_customClaimNamespace) is { } ownerUserId
            ? await accessResolver.ResolveAsync(ownerUserId, cancellationToken)
            : null;
}

/// <summary>A contact to invite.</summary>
public sealed record InviteOwnerContactBody(Guid? AgencyId, string? Email, string? FirstName, string? LastName, List<string>? Permissions);

/// <summary>The permissions of a contact.</summary>
public sealed record OwnerContactPermissionsBody(List<string>? Permissions);

/// <summary>A conversation to open; <c>topic</c> is a name of <see cref="OwnerConversationTopic"/> (case-insensitive).</summary>
public sealed record StartOwnerConversationBody(
    Guid? OwnerId,
    Guid? AgencyId,
    string? Topic,
    Guid? PropertyId,
    Guid? OwnerStayId,
    Guid? DocumentId,
    Guid? StatementVersionId,
    string? Subject,
    string? Body)
{
    /// <summary>The command, with the owner profile of the agency side (<paramref name="propertyOwnerId"/>; <c>null</c> on the owner side).</summary>
    public StartOwnerConversation ToCommand(Guid? propertyOwnerId) =>
        new(
            propertyOwnerId,
            AgencyId,
            Enum.TryParse<OwnerConversationTopic>(Topic, ignoreCase: true, out var topic) && Enum.IsDefined(topic) ? topic : (OwnerConversationTopic)0,
            PropertyId,
            OwnerStayId,
            DocumentId,
            StatementVersionId,
            Subject,
            Body);
}

/// <summary>A message of the inbox: the words and an optional file.</summary>
public sealed class OwnerMessageForm
{
    public string? Body { get; set; }

    public IFormFile? File { get; set; }
}

/// <summary>The upload and the download of the files of the owner portal, for its controllers.</summary>
internal static class OwnerPortalFiles
{
    /// <summary>The largest request: the file and a margin for the other parts of the form.</summary>
    public const int MaxRequestBytes = OwnerFileRules.MaxBytes + (256 * 1024);

    /// <summary>The file of the form read into memory, or the problem to answer (400 none when required, 413 too large).</summary>
    public static async Task<(OwnerFileUpload? File, ObjectResult? Problem)> ReadAsync(
        ControllerBase controller,
        IFormFile? file,
        bool required,
        CancellationToken cancellationToken)
    {
        if (file is null || file.Length == 0)
        {
            return required
                ? (null, controller.ApiProblem(StatusCodes.Status400BadRequest, OwnerFileRules.Missing, OwnerFileRules.MissingMessageKey))
                : (null, null);
        }

        // Judged on the size the request declares, before a byte of it is copied.
        if (file.Length > OwnerFileRules.MaxBytes)
        {
            return (null, controller.ApiProblem(
                StatusCodes.Status413PayloadTooLarge, OwnerFileRules.TooLarge, OwnerFileRules.TooLargeMessageKey, OwnerFileRules.MaxBytes / (1024 * 1024)));
        }

        var content = new byte[file.Length];
        await using (var stream = file.OpenReadStream())
            await stream.ReadExactlyAsync(content, cancellationToken);
        return (new OwnerFileUpload(file.FileName, content), null);
    }

    /// <summary>
    /// A file as a download: never rendered from our origin, never cached, and under a name made safe again with the extension of its type (the one the scan
    /// recognised), whatever the name stored: no double extension, no script or program name reaches the person who saves it.
    /// </summary>
    public static FileStreamResult Download(ControllerBase controller, OwnerFileContent file)
    {
        controller.Response.Headers.CacheControl = "private, no-store";
        controller.Response.Headers.XContentTypeOptions = "nosniff";
        var name = OwnerFileRules.SafeFileName(
            file.FileName, OwnerFileRules.ExtensionOf(file.ContentType), Casazen.Core.Entities.OwnerDocumentVersion.FileNameMaxLength);
        return controller.File(file.Content, file.ContentType, name);
    }
}
