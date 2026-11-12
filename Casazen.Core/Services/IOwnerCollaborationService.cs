using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

/// <summary>
/// The collaboration of the owner portal (E07/F07/US01/T02, NF-111, ADR-011, ADR-012): the documents an agency shares with its owners, the contacts an owner
/// lets into its portal with their own account and explicit permissions, and the inbox between the agency and the owner.
/// </summary>
/// <remarks>
/// <para><b>Who.</b> The owner-portal methods take the <see cref="OwnerPortalAccess"/> of the caller, resolved again at every request: an ended ownership,
/// a removed owner or a withdrawn contact closes the access at the next call, and the audit rows stay. A document, a contact or a conversation of another
/// owner or agency is the same 404 as a missing one (ADR-011-R2); a permission the caller holds nowhere is 403. The agency methods take the org the web layer
/// resolved for an org-wide caller.</para>
/// <para><b>Documents</b> (AC-NF111-3): per owner profile and, when it says so, per property; immutable versions with their effective and expiry dates; a
/// scan before any file is stored (<see cref="IOwnerFileScanner"/>); every download written before the file is handed out; a withdrawal hides the document
/// at the next request. The published statements of the owner accounting (E06) are listed where they are, never copied (<c>accounting.read</c>).</para>
/// <para><b>Contacts</b> (AC-NF111-4): invited by the owner, accepted with the contact's own account, holding only the permissions given, each independent;
/// withdrawn by the owner or by the agency, effective at the next request and recorded in the activity log.</para>
/// <para><b>Inbox</b> (AC-NF111-5): conversations general or about a property, an owner stay, a document or a statement version of the same owner profile,
/// messages kept as written; the e-mail that tells the other side says that there is a message and links to CasaZen: never its words, an amount or a guest.</para>
/// </remarks>
public interface IOwnerCollaborationService
{
    // ---------------------------------------------------------------- documents

    /// <summary>
    /// The documents the caller may read (<c>documents.read</c>; the statements and invoices also <c>accounting.read</c>) with their versions, and the
    /// published statement versions of the owner accounting (<c>accounting.read</c>). 403 when the caller holds neither permission.
    /// </summary>
    Task<OwnerPortalDocuments> ListDocumentsForOwnerAsync(OwnerPortalAccess access, CancellationToken cancellationToken = default);

    /// <summary>The file of a version (the current one when <paramref name="versionId"/> is <c>null</c>); the download is written first.</summary>
    /// <exception cref="Exceptions.NotFoundException"><see cref="OwnerCollaborationErrors.DocumentNotFound"/>.</exception>
    Task<OwnerFileContent> OpenDocumentForOwnerAsync(OwnerPortalAccess access, Guid documentId, Guid? versionId, CancellationToken cancellationToken = default);

    /// <summary>The documents of the org, or of one owner profile, with their versions and downloads.</summary>
    Task<IReadOnlyList<OwnerDocumentAdminView>> ListDocumentsForAgencyAsync(Guid orgId, Guid? propertyOwnerId, CancellationToken cancellationToken = default);

    /// <summary>Shares a new document with its first file.</summary>
    /// <exception cref="Exceptions.DomainRuleException"><see cref="OwnerCollaborationErrors.DocumentInvalid"/>, a code of <see cref="OwnerFileRules"/>.</exception>
    /// <exception cref="Exceptions.NotFoundException"><see cref="OwnerCollaborationErrors.DocumentOwnerNotFound"/>.</exception>
    Task<OwnerDocumentAdminView> ShareDocumentAsync(
        Guid orgId,
        ShareOwnerDocument command,
        OwnerFileUpload file,
        string actorUserId,
        CancellationToken cancellationToken = default);

    /// <summary>Adds a new file to a document: a new version, the earlier ones kept as they were.</summary>
    Task<OwnerDocumentAdminView> AddDocumentVersionAsync(
        Guid orgId,
        Guid documentId,
        OwnerDocumentVersionInput input,
        OwnerFileUpload file,
        string actorUserId,
        CancellationToken cancellationToken = default);

    /// <summary>Withdraws a document from the portal (<paramref name="visible"/> false) or shares it again; a repeat changes nothing.</summary>
    Task<OwnerDocumentAdminView> SetDocumentVisibilityAsync(
        Guid orgId,
        Guid documentId,
        bool visible,
        string actorUserId,
        CancellationToken cancellationToken = default);

    /// <summary>The downloads of a document, newest first.</summary>
    Task<IReadOnlyList<OwnerDocumentDownloadView>> ListDocumentDownloadsAsync(Guid orgId, Guid documentId, CancellationToken cancellationToken = default);

    /// <summary>The file of a version for a person of the agency; the download is written too.</summary>
    Task<OwnerFileContent> OpenDocumentForAgencyAsync(
        Guid orgId,
        Guid documentId,
        Guid versionId,
        string actorUserId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The background work of the documents: a «To do» of the agency for every shared document whose current version lapses within
    /// <see cref="OwnerCollaborationRules.ExpiryAlertDays"/> days (raised once per version), closed when a new version or a withdrawal settles it.
    /// </summary>
    Task<OwnerDocumentAlertResult> RaiseExpiryAlertsAsync(CancellationToken cancellationToken = default);

    // ---------------------------------------------------------------- contacts

    /// <summary>The contacts of the owner profiles the caller owns (a delegated contact manages none: 403).</summary>
    Task<IReadOnlyList<OwnerContactView>> ListContactsForOwnerAsync(OwnerPortalAccess access, CancellationToken cancellationToken = default);

    /// <summary>
    /// The owner invites a contact for one of its owner profiles, with at least one permission (an empty list is 422, never «all»): the link goes by e-mail and
    /// lasts seven days; an e-mail that cannot be queued withdraws the invitation (422).
    /// </summary>
    Task<OwnerContactView> InviteContactAsync(OwnerPortalAccess access, InviteOwnerContact command, CancellationToken cancellationToken = default);

    /// <summary>The owner changes what a contact may do: effective at its next request.</summary>
    Task<OwnerContactView> SetContactPermissionsAsync(
        OwnerPortalAccess access,
        Guid contactId,
        IReadOnlyCollection<string>? permissions,
        CancellationToken cancellationToken = default);

    /// <summary>The owner withdraws a contact: nothing from its next request on; a repeat changes nothing.</summary>
    Task<OwnerContactView> RevokeContactByOwnerAsync(OwnerPortalAccess access, Guid contactId, CancellationToken cancellationToken = default);

    /// <summary>The contacts of the org, or of one owner profile.</summary>
    Task<IReadOnlyList<OwnerContactView>> ListContactsForAgencyAsync(Guid orgId, Guid? propertyOwnerId, CancellationToken cancellationToken = default);

    /// <summary>The agency withdraws a contact; a repeat changes nothing.</summary>
    Task<OwnerContactView> RevokeContactByAgencyAsync(Guid orgId, Guid contactId, string actorUserId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The invited contact accepts with its own account: the e-mail of the account (from the token or Auth0) must be verified and equal to the invited one;
    /// the account becomes an <c>OwnerUser</c> of its own (never the owner's), and the claims of the portal are handed to the identity provider. A repeat
    /// by the same account answers the same.
    /// </summary>
    /// <exception cref="Exceptions.DomainGoneException">The link is unknown, lapsed, used by another account or withdrawn.</exception>
    /// <exception cref="Exceptions.DomainForbiddenException">The e-mail of the account is not verified, or not the invited one.</exception>
    Task<OwnerContactAccepted> AcceptContactAsync(AcceptOwnerContact command, CancellationToken cancellationToken = default);

    // ---------------------------------------------------------------- inbox

    /// <summary>The conversations the caller may read (<c>inbox.use</c>), the latest first.</summary>
    Task<IReadOnlyList<OwnerConversationView>> ListConversationsForOwnerAsync(OwnerPortalAccess access, CancellationToken cancellationToken = default);

    /// <summary>Opens a conversation with the agency and writes its first message; the agency is told by e-mail, without the words.</summary>
    Task<OwnerConversationView> StartConversationByOwnerAsync(
        OwnerPortalAccess access,
        StartOwnerConversation command,
        CancellationToken cancellationToken = default);

    /// <summary>The messages of a conversation, oldest first.</summary>
    Task<IReadOnlyList<OwnerMessageView>> ListMessagesForOwnerAsync(OwnerPortalAccess access, Guid conversationId, CancellationToken cancellationToken = default);

    /// <summary>Writes a message, with an optional file that passed the scan; the agency is told by e-mail, without the words.</summary>
    Task<OwnerMessageView> PostMessageByOwnerAsync(
        OwnerPortalAccess access,
        Guid conversationId,
        string? body,
        OwnerFileUpload? attachment,
        CancellationToken cancellationToken = default);

    /// <summary>The file of a message of a conversation the caller may read.</summary>
    Task<OwnerFileContent> OpenAttachmentForOwnerAsync(
        OwnerPortalAccess access,
        Guid conversationId,
        Guid messageId,
        CancellationToken cancellationToken = default);

    /// <summary>The conversations of the org, or of one owner profile, the latest first.</summary>
    Task<IReadOnlyList<OwnerConversationView>> ListConversationsForAgencyAsync(Guid orgId, Guid? propertyOwnerId, CancellationToken cancellationToken = default);

    /// <summary>The agency opens a conversation with an owner profile; the owner and its contacts with <c>inbox.use</c> are told by e-mail.</summary>
    Task<OwnerConversationView> StartConversationByAgencyAsync(
        Guid orgId,
        StartOwnerConversation command,
        string actorUserId,
        CancellationToken cancellationToken = default);

    /// <summary>The messages of a conversation of the org, oldest first.</summary>
    Task<IReadOnlyList<OwnerMessageView>> ListMessagesForAgencyAsync(Guid orgId, Guid conversationId, CancellationToken cancellationToken = default);

    /// <summary>A person of the agency writes a message; the owner side is told by e-mail, without the words.</summary>
    Task<OwnerMessageView> PostMessageByAgencyAsync(
        Guid orgId,
        Guid conversationId,
        string? body,
        OwnerFileUpload? attachment,
        string actorUserId,
        CancellationToken cancellationToken = default);

    /// <summary>The file of a message of a conversation of the org.</summary>
    Task<OwnerFileContent> OpenAttachmentForAgencyAsync(Guid orgId, Guid conversationId, Guid messageId, CancellationToken cancellationToken = default);

    /// <summary>Whether the person of the portal gets an e-mail when the agency writes.</summary>
    Task<OwnerInboxPreferences> GetInboxPreferencesAsync(Guid ownerUserId, CancellationToken cancellationToken = default);

    /// <summary>Sets whether the person of the portal gets an e-mail when the agency writes.</summary>
    Task<OwnerInboxPreferences> SetInboxPreferencesAsync(Guid ownerUserId, bool emailNotifications, CancellationToken cancellationToken = default);
}

/// <summary>The limits of the collaboration of the owner portal.</summary>
public static class OwnerCollaborationRules
{
    /// <summary>The days before the expiry of a document when the agency is told.</summary>
    public const int ExpiryAlertDays = 30;

    /// <summary>The most contacts in force (invited or active) of one owner profile.</summary>
    public const int MaxContactsPerOwner = 10;

    /// <summary>The days the link of the invitation of a contact lasts.</summary>
    public const int ContactInvitationDays = 7;

    /// <summary>The most items a list answers.</summary>
    public const int MaxList = 500;
}

/// <summary>Error codes of the collaboration of the owner portal, with the keys of their localized messages.</summary>
public static class OwnerCollaborationErrors
{
    public const string DocumentNotFound = "owner_document_not_found";
    public const string DocumentInvalid = "owner_document_invalid";
    public const string DocumentOwnerNotFound = "owner_document_owner_not_found";
    public const string ContactNotFound = "owner_contact_not_found";
    public const string ContactOwnerOnly = "owner_contact_owner_only";
    public const string ContactAgencyRequired = "owner_contact_agency_required";
    public const string ContactInvalid = "owner_contact_invalid";
    public const string ContactExists = "owner_contact_exists";
    public const string ContactLimitReached = "owner_contact_limit_reached";
    public const string ContactRevoked = "owner_contact_revoked";
    public const string ContactEmailNotQueued = "owner_contact_email_not_queued";
    public const string InvitationInvalid = "owner_contact_invitation_invalid";
    public const string InvitationExpired = "owner_contact_invitation_expired";
    public const string InvitationUsed = "owner_contact_invitation_used";
    public const string InvitationRevoked = "owner_contact_invitation_revoked";
    public const string EmailNotVerified = "owner_contact_email_not_verified";
    public const string EmailMismatch = "owner_contact_email_mismatch";
    public const string ConversationNotFound = "owner_conversation_not_found";
    public const string ConversationInvalid = "owner_conversation_invalid";
    public const string ConversationReferenceNotFound = "owner_conversation_reference_not_found";
    public const string MessageInvalid = "owner_message_invalid";
    public const string AttachmentNotFound = "owner_attachment_not_found";

    public const string DocumentNotFoundMessageKey = "OwnerDocumentNotFound";
    public const string DocumentInvalidMessageKey = "OwnerDocumentInvalid";
    public const string DocumentOwnerNotFoundMessageKey = "OwnerDocumentOwnerNotFound";
    public const string ContactNotFoundMessageKey = "OwnerContactNotFound";
    public const string ContactOwnerOnlyMessageKey = "OwnerContactOwnerOnly";
    public const string ContactAgencyRequiredMessageKey = "OwnerContactAgencyRequired";
    public const string ContactInvalidMessageKey = "OwnerContactInvalid";
    public const string ContactExistsMessageKey = "OwnerContactExists";
    public const string ContactLimitReachedMessageKey = "OwnerContactLimitReached";
    public const string ContactRevokedMessageKey = "OwnerContactRevoked";
    public const string ContactEmailNotQueuedMessageKey = "OwnerContactEmailNotQueued";
    public const string InvitationInvalidMessageKey = "OwnerContactInvitationInvalid";
    public const string InvitationExpiredMessageKey = "OwnerContactInvitationExpired";
    public const string InvitationUsedMessageKey = "OwnerContactInvitationUsed";
    public const string InvitationRevokedMessageKey = "OwnerContactInvitationRevoked";
    public const string EmailNotVerifiedMessageKey = "OwnerContactEmailNotVerified";
    public const string EmailMismatchMessageKey = "OwnerContactEmailMismatch";
    public const string ConversationNotFoundMessageKey = "OwnerConversationNotFound";
    public const string ConversationInvalidMessageKey = "OwnerConversationInvalid";
    public const string ConversationReferenceNotFoundMessageKey = "OwnerConversationReferenceNotFound";
    public const string MessageInvalidMessageKey = "OwnerMessageInvalid";
    public const string AttachmentNotFoundMessageKey = "OwnerAttachmentNotFound";
}

/// <summary>An uploaded file: the name it came with (only its last part is kept) and its bytes.</summary>
public sealed record OwnerFileUpload(string? FileName, byte[] Content);

/// <summary>A file handed out: the caller disposes the stream.</summary>
public sealed record OwnerFileContent(Stream Content, string FileName, string ContentType);

/// <summary>A document to share with an owner profile, optionally for one of its properties.</summary>
public sealed record ShareOwnerDocument(
    Guid PropertyOwnerId,
    Guid? PropertyId,
    OwnerDocumentType Type,
    string? Title,
    DateOnly? EffectiveFrom,
    DateOnly? ExpiresOn);

/// <summary>The dates of a new version.</summary>
public sealed record OwnerDocumentVersionInput(DateOnly? EffectiveFrom, DateOnly? ExpiresOn);

/// <summary>How a version reads on a day: <c>upcoming</c> (not in force yet), <c>current</c>, <c>superseded</c> (a later one is in force), <c>expired</c>.</summary>
public sealed record OwnerDocumentVersionView(
    Guid Id,
    int VersionNumber,
    string FileName,
    string ContentType,
    long SizeBytes,
    string Sha256,
    DateOnly? EffectiveFrom,
    DateOnly? ExpiresOn,
    string State,
    DateTime UploadedAt);

/// <summary>
/// A shared document: its versions, the newest first, and the expiry state of the current one (<c>none</c>, <c>valid</c>, <c>expiring</c> within
/// <see cref="OwnerCollaborationRules.ExpiryAlertDays"/> days, <c>expired</c>).
/// </summary>
public sealed record OwnerDocumentView(
    Guid Id,
    Guid OrgId,
    Guid PropertyOwnerId,
    Guid? PropertyId,
    string? PropertyName,
    OwnerDocumentType Type,
    string Title,
    string ExpiryState,
    DateOnly? ExpiresOn,
    IReadOnlyList<OwnerDocumentVersionView> Versions,
    DateTime UpdatedAt);

/// <summary>A shared document as the agency reads it: the owner profile, the downloads and whether it is withdrawn.</summary>
public sealed record OwnerDocumentAdminView(OwnerDocumentView Document, string OwnerName, bool Visible, DateTime? RevokedAt, int Downloads, DateTime? LastDownloadAt);

/// <summary>One download: the version, when, and who (the name of the person of the portal, or <c>User.Id</c> of the agency with <paramref name="ByAgency"/>).</summary>
public sealed record OwnerDocumentDownloadView(Guid VersionId, int VersionNumber, DateTime DownloadedAt, string By, bool ByAgency);

/// <summary>A published (or superseded) statement version of the owner accounting, referenced where it is (E06), never copied.</summary>
public sealed record OwnerStatementReferenceView(
    Guid StatementVersionId,
    Guid OrgId,
    Guid PropertyOwnerId,
    int PeriodYear,
    int PeriodMonth,
    int VersionNumber,
    OwnerStatementVersionStatus Status,
    DateTime PublishedAt,
    string? PublishedSha256);

/// <summary>What the documents page of the portal shows.</summary>
public sealed record OwnerPortalDocuments(IReadOnlyList<OwnerDocumentView> Documents, IReadOnlyList<OwnerStatementReferenceView> Statements);

/// <summary>What one run of the expiry alerts did.</summary>
public sealed record OwnerDocumentAlertResult(int Raised, int Closed);

/// <summary>A contact the owner invites: the agency (needed when the owner has more than one), the person and its permissions.</summary>
public sealed record InviteOwnerContact(Guid? AgencyId, string? Email, string? FirstName, string? LastName, IReadOnlyCollection<string>? Permissions);

/// <summary>A delegated contact: <c>status</c> is <c>invited</c>, <c>expired</c> (the link lapsed), <c>active</c> or <c>revoked</c>.</summary>
public sealed record OwnerContactView(
    Guid Id,
    Guid OrgId,
    Guid PropertyOwnerId,
    string Email,
    string FirstName,
    string LastName,
    IReadOnlyList<string> Permissions,
    string Status,
    DateTime CreatedAt,
    DateTime InvitationExpiresAt,
    DateTime? AcceptedAt,
    DateTime? RevokedAt);

/// <summary>The contact who accepts: the secret of the link and the account that holds it (the e-mail comes from the token or Auth0).</summary>
public sealed record AcceptOwnerContact(string? Token, string Auth0Sub, string? Email, bool EmailVerified, string FirstName, string LastName);

/// <summary>What the acceptance did, and the claims of the portal (ADR-011-R7) handed to the identity provider (<see cref="OwnerClaimsRefresh"/>).</summary>
public sealed record OwnerContactAccepted(
    Guid OwnerUserId,
    IReadOnlyList<Guid> AgencyIds,
    IReadOnlyList<Guid> PropertyIds,
    bool AlreadyAccepted,
    string ClaimsRefresh);

/// <summary>
/// A conversation to open: on the agency side the owner profile (<paramref name="PropertyOwnerId"/>), on the owner side the agency when the owner has more
/// than one (<paramref name="AgencyId"/>); the topic and exactly the reference it names; the subject and the first message.
/// </summary>
public sealed record StartOwnerConversation(
    Guid? PropertyOwnerId,
    Guid? AgencyId,
    OwnerConversationTopic Topic,
    Guid? PropertyId,
    Guid? OwnerStayId,
    Guid? DocumentId,
    Guid? StatementVersionId,
    string? Subject,
    string? Body);

/// <summary>A conversation of the inbox.</summary>
public sealed record OwnerConversationView(
    Guid Id,
    Guid OrgId,
    Guid PropertyOwnerId,
    OwnerConversationTopic Topic,
    Guid? PropertyId,
    Guid? OwnerStayId,
    Guid? DocumentId,
    Guid? StatementVersionId,
    string Subject,
    DateTime CreatedAt,
    DateTime LastMessageAt);

/// <summary>The file of a message: its name, type and size.</summary>
public sealed record OwnerAttachmentView(string FileName, string ContentType, long SizeBytes);

/// <summary>A message: who wrote it (the agency, or the person of the portal by name), the words and the file.</summary>
public sealed record OwnerMessageView(
    Guid Id,
    Guid ConversationId,
    OwnerMessageSender Sender,
    string AuthorName,
    string Body,
    OwnerAttachmentView? Attachment,
    DateTime CreatedAt);

/// <summary>The notification preference of the owner inbox.</summary>
public sealed record OwnerInboxPreferences(bool EmailNotifications);
