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
    Task<OwnerPortalDocuments> ListDocumentsForOwnerAsync(OwnerPortalAccess access, CancellationToken cancellationToken = default);

    Task<OwnerFileContent> OpenDocumentForOwnerAsync(OwnerPortalAccess access, Guid documentId, Guid? versionId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<OwnerDocumentAdminView>> ListDocumentsForAgencyAsync(Guid orgId, Guid? propertyOwnerId, CancellationToken cancellationToken = default);

    Task<OwnerDocumentAdminView> ShareDocumentAsync(
        Guid orgId,
        ShareOwnerDocument command,
        OwnerFileUpload file,
        string actorUserId,
        CancellationToken cancellationToken = default);

    Task<OwnerDocumentAdminView> AddDocumentVersionAsync(
        Guid orgId,
        Guid documentId,
        OwnerDocumentVersionInput input,
        OwnerFileUpload file,
        string actorUserId,
        CancellationToken cancellationToken = default);

    Task<OwnerDocumentAdminView> SetDocumentVisibilityAsync(
        Guid orgId,
        Guid documentId,
        bool visible,
        string actorUserId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<OwnerDocumentDownloadView>> ListDocumentDownloadsAsync(Guid orgId, Guid documentId, CancellationToken cancellationToken = default);

    Task<OwnerFileContent> OpenDocumentForAgencyAsync(
        Guid orgId,
        Guid documentId,
        Guid versionId,
        string actorUserId,
        CancellationToken cancellationToken = default);

    Task<OwnerDocumentAlertResult> RaiseExpiryAlertsAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<OwnerContactView>> ListContactsForOwnerAsync(OwnerPortalAccess access, CancellationToken cancellationToken = default);

    Task<OwnerContactView> InviteContactAsync(OwnerPortalAccess access, InviteOwnerContact command, CancellationToken cancellationToken = default);

    Task<OwnerContactView> SetContactPermissionsAsync(
        OwnerPortalAccess access,
        Guid contactId,
        IReadOnlyCollection<string>? permissions,
        CancellationToken cancellationToken = default);

    Task<OwnerContactView> RevokeContactByOwnerAsync(OwnerPortalAccess access, Guid contactId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<OwnerContactView>> ListContactsForAgencyAsync(Guid orgId, Guid? propertyOwnerId, CancellationToken cancellationToken = default);

    Task<OwnerContactView> RevokeContactByAgencyAsync(Guid orgId, Guid contactId, string actorUserId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The invited contact accepts with its own account: the e-mail of the account (from the token or Auth0) must be verified and equal to the invited one;
    /// the account becomes an <c>OwnerUser</c> of its own (never the owner's), and the claims of the portal are handed to the identity provider. A repeat
    /// by the same account answers the same.
    /// </summary>
    /// <exception cref="Exceptions.DomainGoneException">The link is unknown, lapsed, used by another account or withdrawn.</exception>
    /// <exception cref="Exceptions.DomainForbiddenException">The e-mail of the account is not verified, or not the invited one.</exception>
    Task<OwnerContactAccepted> AcceptContactAsync(AcceptOwnerContact command, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<OwnerConversationView>> ListConversationsForOwnerAsync(OwnerPortalAccess access, CancellationToken cancellationToken = default);

    Task<OwnerConversationView> StartConversationByOwnerAsync(
        OwnerPortalAccess access,
        StartOwnerConversation command,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<OwnerMessageView>> ListMessagesForOwnerAsync(OwnerPortalAccess access, Guid conversationId, CancellationToken cancellationToken = default);

    Task<OwnerMessageView> PostMessageByOwnerAsync(
        OwnerPortalAccess access,
        Guid conversationId,
        string? body,
        OwnerFileUpload? attachment,
        CancellationToken cancellationToken = default);

    Task<OwnerFileContent> OpenAttachmentForOwnerAsync(
        OwnerPortalAccess access,
        Guid conversationId,
        Guid messageId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<OwnerConversationView>> ListConversationsForAgencyAsync(Guid orgId, Guid? propertyOwnerId, CancellationToken cancellationToken = default);

    Task<OwnerConversationView> StartConversationByAgencyAsync(
        Guid orgId,
        StartOwnerConversation command,
        string actorUserId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<OwnerMessageView>> ListMessagesForAgencyAsync(Guid orgId, Guid conversationId, CancellationToken cancellationToken = default);

    Task<OwnerMessageView> PostMessageByAgencyAsync(
        Guid orgId,
        Guid conversationId,
        string? body,
        OwnerFileUpload? attachment,
        string actorUserId,
        CancellationToken cancellationToken = default);

    Task<OwnerFileContent> OpenAttachmentForAgencyAsync(Guid orgId, Guid conversationId, Guid messageId, CancellationToken cancellationToken = default);

    Task<OwnerInboxPreferences> GetInboxPreferencesAsync(Guid ownerUserId, CancellationToken cancellationToken = default);

    Task<OwnerInboxPreferences> SetInboxPreferencesAsync(Guid ownerUserId, bool emailNotifications, CancellationToken cancellationToken = default);
}

public static class OwnerCollaborationRules
{
    public const int ExpiryAlertDays = 30;

    public const int MaxContactsPerOwner = 10;

    /// <summary>
    /// The most invitations of contacts one person of the portal makes in a day, <b>the withdrawn ones counted too</b>: each sends an e-mail to an address the
    /// owner chose, and the limit of the contacts in force alone would never stop inviting, withdrawing and inviting again (429
    /// <c>owner_contact_daily_limit_reached</c>, as <c>OwnerInvitationService</c> does for the invitations of an agency).
    /// </summary>
    public const int MaxContactInvitationsPerDay = 20;

    public const int ContactInvitationDays = 7;

    public const int MaxList = 500;
}

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
    public const string ContactDailyLimitReached = "owner_contact_daily_limit_reached";
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
    public const string ContactDailyLimitReachedMessageKey = "OwnerContactDailyLimitReached";
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

public sealed record OwnerFileUpload(string? FileName, byte[] Content);

public sealed record OwnerFileContent(Stream Content, string FileName, string ContentType);

public sealed record ShareOwnerDocument(
    Guid PropertyOwnerId,
    Guid? PropertyId,
    OwnerDocumentType Type,
    string? Title,
    DateOnly? EffectiveFrom,
    DateOnly? ExpiresOn);

public sealed record OwnerDocumentVersionInput(DateOnly? EffectiveFrom, DateOnly? ExpiresOn);

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

public sealed record OwnerDocumentAdminView(OwnerDocumentView Document, string OwnerName, bool Visible, DateTime? RevokedAt, int Downloads, DateTime? LastDownloadAt);

public sealed record OwnerDocumentDownloadView(Guid VersionId, int VersionNumber, DateTime DownloadedAt, string By, bool ByAgency);

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

public sealed record OwnerPortalDocuments(IReadOnlyList<OwnerDocumentView> Documents, IReadOnlyList<OwnerStatementReferenceView> Statements);

public sealed record OwnerDocumentAlertResult(int Raised, int Closed);

public sealed record InviteOwnerContact(Guid? AgencyId, string? Email, string? FirstName, string? LastName, IReadOnlyCollection<string>? Permissions);

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

public sealed record AcceptOwnerContact(string? Token, string Auth0Sub, string? Email, bool EmailVerified, string FirstName, string LastName);

public sealed record OwnerContactAccepted(
    Guid OwnerUserId,
    IReadOnlyList<Guid> AgencyIds,
    IReadOnlyList<Guid> PropertyIds,
    bool AlreadyAccepted,
    string ClaimsRefresh);

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

public sealed record OwnerAttachmentView(string FileName, string ContentType, long SizeBytes);

public sealed record OwnerMessageView(
    Guid Id,
    Guid ConversationId,
    OwnerMessageSender Sender,
    string AuthorName,
    string Body,
    OwnerAttachmentView? Attachment,
    DateTime CreatedAt);

public sealed record OwnerInboxPreferences(bool EmailNotifications);
