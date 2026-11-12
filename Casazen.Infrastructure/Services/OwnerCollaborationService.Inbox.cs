using System.Globalization;
using System.Security.Cryptography;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email.Templates;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// The owner inbox (E07/F07/US01/T02, AC-NF111-5): conversations between an agency and one of its owner profiles, general or about one item of that owner
/// profile, messages kept as written, a file per message after the scan. The other side is told by e-mail that there is a message, with a link to CasaZen:
/// never the words, an amount or a guest.
/// </summary>
public sealed partial class OwnerCollaborationService
{
    // ---------------------------------------------------------------- owner portal

    public async Task<IReadOnlyList<OwnerConversationView>> ListConversationsForOwnerAsync(OwnerPortalAccess access, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(access);
        access.Require(OwnerPortalPermission.InboxUse);
        var ownerIds = access.With(OwnerPortalPermission.InboxUse).Select(g => g.PropertyOwnerId).Distinct().ToList();
        var conversations = await Conversations().AsNoTracking()
            .Where(c => ownerIds.Contains(c.PropertyOwnerId))
            .OrderByDescending(c => c.LastMessageAt)
            .Take(OwnerCollaborationRules.MaxList)
            .ToListAsync(cancellationToken);

        var documentIds = conversations.Where(c => c.OwnerDocumentId != null).Select(c => c.OwnerDocumentId!.Value).Distinct().ToList();
        var documents = await Documents().AsNoTracking().Where(d => documentIds.Contains(d.Id)).ToDictionaryAsync(d => d.Id, cancellationToken);
        return conversations.Where(c => CanRead(access, c, documents)).Select(ConversationView).ToList();
    }

    public async Task<OwnerConversationView> StartConversationByOwnerAsync(
        OwnerPortalAccess access,
        StartOwnerConversation command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(command);
        access.Require(OwnerPortalPermission.InboxUse);
        var (subject, body) = Words(command.Subject, command.Body, attachment: false);
        var target = await OwnerTargetAsync(access, command, cancellationToken);

        var now = Now();
        var conversation = NewConversation(target, subject, now);
        conversation.CreatedByOwnerUserId = access.OwnerUserId;
        var message = new OwnerMessage
        {
            OrgId = target.OrgId,
            ConversationId = conversation.Id,
            Sender = OwnerMessageSender.Owner,
            SenderOwnerUserId = access.OwnerUserId,
            Body = body,
            CreatedAt = now,
        };
        db.OwnerConversations.Add(conversation);
        db.OwnerMessages.Add(message);
        Audit(target.OrgId, conversation.Id, OwnerPortalParts.Conversation, OwnerPortalAuditStatuses.Opened, access.Actor);
        await db.SaveChangesAsync(cancellationToken);

        await TellAgencyAsync(conversation, cancellationToken);
        return ConversationView(conversation);
    }

    public async Task<IReadOnlyList<OwnerMessageView>> ListMessagesForOwnerAsync(OwnerPortalAccess access, Guid conversationId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(access);
        var conversation = await ReadableConversationAsync(access, conversationId, cancellationToken);
        return await MessageViewsAsync(conversation, cancellationToken);
    }

    public async Task<OwnerMessageView> PostMessageByOwnerAsync(
        OwnerPortalAccess access,
        Guid conversationId,
        string? body,
        OwnerFileUpload? attachment,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(access);
        var readable = await ReadableConversationAsync(access, conversationId, cancellationToken);
        var message = await PostAsync(readable.Id, OwnerMessageSender.Owner, null, access.OwnerUserId, body, attachment, cancellationToken);
        await TellAgencyAsync(readable, cancellationToken);
        return (await MessageViewsAsync(readable, cancellationToken)).Single(m => m.Id == message.Id);
    }

    public async Task<OwnerFileContent> OpenAttachmentForOwnerAsync(
        OwnerPortalAccess access,
        Guid conversationId,
        Guid messageId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(access);
        var conversation = await ReadableConversationAsync(access, conversationId, cancellationToken);
        return await AttachmentAsync(conversation, messageId, cancellationToken);
    }

    public async Task<OwnerInboxPreferences> GetInboxPreferencesAsync(Guid ownerUserId, CancellationToken cancellationToken = default)
    {
        var optOut = await db.OwnerUsers.AsNoTracking().Where(u => u.Id == ownerUserId).Select(u => (bool?)u.InboxEmailOptOut).FirstOrDefaultAsync(cancellationToken);
        return new OwnerInboxPreferences(optOut is not true);
    }

    public async Task<OwnerInboxPreferences> SetInboxPreferencesAsync(Guid ownerUserId, bool emailNotifications, CancellationToken cancellationToken = default)
    {
        var user = await db.OwnerUsers.FirstOrDefaultAsync(u => u.Id == ownerUserId, cancellationToken)
            ?? throw new NotFoundException($"OwnerUser {ownerUserId} not found");
        if (user.InboxEmailOptOut == emailNotifications)
        {
            user.InboxEmailOptOut = !emailNotifications;
            await db.SaveChangesAsync(cancellationToken);
        }

        return new OwnerInboxPreferences(emailNotifications);
    }

    // ---------------------------------------------------------------- agency

    public async Task<IReadOnlyList<OwnerConversationView>> ListConversationsForAgencyAsync(Guid orgId, Guid? propertyOwnerId, CancellationToken cancellationToken = default)
    {
        var query = Conversations().AsNoTracking().Where(c => c.OrgId == orgId);
        if (propertyOwnerId is { } ownerId)
            query = query.Where(c => c.PropertyOwnerId == ownerId);

        var conversations = await query.OrderByDescending(c => c.LastMessageAt).Take(OwnerCollaborationRules.MaxList).ToListAsync(cancellationToken);
        return conversations.Select(ConversationView).ToList();
    }

    public async Task<OwnerConversationView> StartConversationByAgencyAsync(
        Guid orgId,
        StartOwnerConversation command,
        string actorUserId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorUserId);
        var (subject, body) = Words(command.Subject, command.Body, attachment: false);
        var target = await AgencyTargetAsync(orgId, command, cancellationToken);

        var now = Now();
        var conversation = NewConversation(target, subject, now);
        conversation.CreatedByUserId = actorUserId;
        db.OwnerConversations.Add(conversation);
        db.OwnerMessages.Add(new OwnerMessage
        {
            OrgId = orgId,
            ConversationId = conversation.Id,
            Sender = OwnerMessageSender.Agency,
            SenderUserId = actorUserId,
            Body = body,
            CreatedAt = now,
        });
        Audit(orgId, conversation.Id, OwnerPortalParts.Conversation, OwnerPortalAuditStatuses.Opened, actorUserId);
        await db.SaveChangesAsync(cancellationToken);

        await TellOwnerSideAsync(conversation, cancellationToken);
        return ConversationView(conversation);
    }

    public async Task<IReadOnlyList<OwnerMessageView>> ListMessagesForAgencyAsync(Guid orgId, Guid conversationId, CancellationToken cancellationToken = default)
    {
        var conversation = await AgencyConversationAsync(orgId, conversationId, cancellationToken);
        return await MessageViewsAsync(conversation, cancellationToken);
    }

    public async Task<OwnerMessageView> PostMessageByAgencyAsync(
        Guid orgId,
        Guid conversationId,
        string? body,
        OwnerFileUpload? attachment,
        string actorUserId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorUserId);
        var conversation = await AgencyConversationAsync(orgId, conversationId, cancellationToken);
        var message = await PostAsync(conversation.Id, OwnerMessageSender.Agency, actorUserId, null, body, attachment, cancellationToken);
        await TellOwnerSideAsync(conversation, cancellationToken);
        return (await MessageViewsAsync(conversation, cancellationToken)).Single(m => m.Id == message.Id);
    }

    public async Task<OwnerFileContent> OpenAttachmentForAgencyAsync(Guid orgId, Guid conversationId, Guid messageId, CancellationToken cancellationToken = default)
    {
        var conversation = await AgencyConversationAsync(orgId, conversationId, cancellationToken);
        return await AttachmentAsync(conversation, messageId, cancellationToken);
    }

    // ---------------------------------------------------------------- shared

    /// <summary>The org, owner profile and reference of a new conversation, checked.</summary>
    private sealed record ConversationTarget(
        Guid OrgId,
        Guid PropertyOwnerId,
        OwnerConversationTopic Topic,
        Guid? PropertyId,
        Guid? OwnerStayId,
        Guid? DocumentId,
        Guid? StatementVersionId);

    private IQueryable<OwnerConversation> Conversations() => db.OwnerConversations.IgnoreQueryFilters([AppDbContext.TenantQueryFilter]);

    private IQueryable<OwnerMessage> Messages() => db.OwnerMessages.IgnoreQueryFilters([AppDbContext.TenantQueryFilter]);

    /// <summary>
    /// The caller reads the conversation: <c>inbox.use</c> on its owner profile and, for a conversation about a property or an owner stay, on that property;
    /// about a document, the document still shared and readable by the caller; about a statement version, <c>accounting.read</c> on the owner profile.
    /// </summary>
    private static bool CanRead(OwnerPortalAccess access, OwnerConversation conversation, IReadOnlyDictionary<Guid, OwnerDocument> documents)
    {
        if (!access.Covers(conversation.OrgId, conversation.PropertyOwnerId, OwnerPortalPermission.InboxUse))
            return false;

        return conversation.Topic switch
        {
            OwnerConversationTopic.Property or OwnerConversationTopic.OwnerStay => access.Grants.Any(g =>
                g.OrgId == conversation.OrgId
                && g.PropertyOwnerId == conversation.PropertyOwnerId
                && g.PropertyId == conversation.PropertyId
                && g.Permissions.HasFlag(OwnerPortalPermission.InboxUse)),
            OwnerConversationTopic.Document => conversation.OwnerDocumentId is { } documentId
                && documents.TryGetValue(documentId, out var document)
                && document.RevokedAt is null
                && CanRead(access, document),
            OwnerConversationTopic.StatementVersion => access.Covers(conversation.OrgId, conversation.PropertyOwnerId, OwnerPortalPermission.AccountingRead),
            _ => true,
        };
    }

    private async Task<OwnerConversation> ReadableConversationAsync(OwnerPortalAccess access, Guid conversationId, CancellationToken cancellationToken)
    {
        access.Require(OwnerPortalPermission.InboxUse);
        var conversation = await Conversations().AsNoTracking().FirstOrDefaultAsync(c => c.Id == conversationId, cancellationToken);
        var documents = conversation?.OwnerDocumentId is { } documentId
            ? await Documents().AsNoTracking().Where(d => d.Id == documentId).ToDictionaryAsync(d => d.Id, cancellationToken)
            : new Dictionary<Guid, OwnerDocument>();
        if (conversation is null || !CanRead(access, conversation, documents))
            throw ConversationNotFound(conversationId);
        return conversation;
    }

    private async Task<OwnerConversation> AgencyConversationAsync(Guid orgId, Guid conversationId, CancellationToken cancellationToken) =>
        await Conversations().AsNoTracking().FirstOrDefaultAsync(c => c.Id == conversationId && c.OrgId == orgId, cancellationToken)
        ?? throw ConversationNotFound(conversationId);

    /// <summary>
    /// What a conversation the caller opens is about: the reference its topic names and nothing else, reached by the caller with <c>inbox.use</c> (and the
    /// permission of the item); the owner profile and the agency follow from it (a general one: the agency named, or the only one).
    /// </summary>
    private async Task<ConversationTarget> OwnerTargetAsync(OwnerPortalAccess access, StartOwnerConversation command, CancellationToken cancellationToken)
    {
        RequireOnlyTheReferenceOf(command);
        switch (command.Topic)
        {
            case OwnerConversationTopic.General:
            {
                var profiles = access.With(OwnerPortalPermission.InboxUse).Select(g => (g.OrgId, g.PropertyOwnerId)).Distinct().ToList();
                var candidates = command.AgencyId is { } agency ? profiles.Where(p => p.OrgId == agency).ToList() : profiles;
                if (candidates.Count != 1)
                    throw ConversationInvalid("agencyId");
                return new ConversationTarget(candidates[0].OrgId, candidates[0].PropertyOwnerId, command.Topic, null, null, null, null);
            }

            case OwnerConversationTopic.Property:
            {
                var grant = access.On(command.PropertyId!.Value, OwnerPortalPermission.InboxUse).FirstOrDefault() ?? throw ReferenceNotFound();
                return new ConversationTarget(grant.OrgId, grant.PropertyOwnerId, command.Topic, grant.PropertyId, null, null, null);
            }

            case OwnerConversationTopic.OwnerStay:
            {
                var stay = await db.OwnerStayRequests.IgnoreQueryFilters([AppDbContext.TenantQueryFilter]).AsNoTracking()
                    .FirstOrDefaultAsync(s => s.Id == command.OwnerStayId, cancellationToken);
                if (stay is null || !access.On(stay.PropertyId, OwnerPortalPermission.InboxUse).Any(g => g.OrgId == stay.OrgId && g.PropertyOwnerId == stay.PropertyOwnerId))
                    throw ReferenceNotFound();
                return new ConversationTarget(stay.OrgId, stay.PropertyOwnerId, command.Topic, stay.PropertyId, stay.Id, null, null);
            }

            case OwnerConversationTopic.Document:
            {
                var document = await Documents().AsNoTracking().FirstOrDefaultAsync(d => d.Id == command.DocumentId && d.RevokedAt == null, cancellationToken);
                if (document is null || !CanRead(access, document) || !access.Covers(document.OrgId, document.PropertyOwnerId, OwnerPortalPermission.InboxUse))
                    throw ReferenceNotFound();
                return new ConversationTarget(document.OrgId, document.PropertyOwnerId, command.Topic, document.PropertyId, null, document.Id, null);
            }

            default:
            {
                var version = await db.OwnerStatementVersions.IgnoreQueryFilters([AppDbContext.TenantQueryFilter]).AsNoTracking()
                    .FirstOrDefaultAsync(v => v.Id == command.StatementVersionId && v.PublishedAt != null, cancellationToken);
                if (version is null
                    || !access.Covers(version.OrgId, version.PropertyOwnerId, OwnerPortalPermission.AccountingRead)
                    || !access.Covers(version.OrgId, version.PropertyOwnerId, OwnerPortalPermission.InboxUse))
                {
                    throw ReferenceNotFound();
                }

                return new ConversationTarget(version.OrgId, version.PropertyOwnerId, command.Topic, null, null, null, version.Id);
            }
        }
    }

    /// <summary>The same for the agency: the owner profile is of the org, and the reference is of that owner profile and org.</summary>
    private async Task<ConversationTarget> AgencyTargetAsync(Guid orgId, StartOwnerConversation command, CancellationToken cancellationToken)
    {
        RequireOnlyTheReferenceOf(command);
        if (command.PropertyOwnerId is not { } ownerId
            || !await db.PropertyOwners.IgnoreQueryFilters([AppDbContext.TenantQueryFilter]).AnyAsync(o => o.Id == ownerId && o.OrgId == orgId, cancellationToken))
        {
            throw ReferenceNotFound();
        }

        var found = command.Topic switch
        {
            OwnerConversationTopic.General => true,
            OwnerConversationTopic.Property => await db.PropertyOwnerships.IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
                .AnyAsync(o => o.OrgId == orgId && o.PropertyOwnerId == ownerId && o.PropertyId == command.PropertyId, cancellationToken),
            OwnerConversationTopic.OwnerStay => await db.OwnerStayRequests.IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
                .AnyAsync(s => s.Id == command.OwnerStayId && s.OrgId == orgId && s.PropertyOwnerId == ownerId, cancellationToken),
            OwnerConversationTopic.Document => await Documents()
                .AnyAsync(d => d.Id == command.DocumentId && d.OrgId == orgId && d.PropertyOwnerId == ownerId, cancellationToken),
            _ => await db.OwnerStatementVersions.IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
                .AnyAsync(v => v.Id == command.StatementVersionId && v.OrgId == orgId && v.PropertyOwnerId == ownerId && v.PublishedAt != null, cancellationToken),
        };
        if (!found)
            throw ReferenceNotFound();

        var propertyId = command.Topic switch
        {
            OwnerConversationTopic.Property => command.PropertyId,
            OwnerConversationTopic.OwnerStay => await db.OwnerStayRequests.IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
                .Where(s => s.Id == command.OwnerStayId).Select(s => (Guid?)s.PropertyId).FirstOrDefaultAsync(cancellationToken),
            OwnerConversationTopic.Document => await Documents().Where(d => d.Id == command.DocumentId).Select(d => d.PropertyId).FirstOrDefaultAsync(cancellationToken),
            _ => null,
        };
        return new ConversationTarget(orgId, ownerId, command.Topic, propertyId, command.OwnerStayId, command.DocumentId, command.StatementVersionId);
    }

    /// <summary>A topic that is not one of the list, or a reference that is not the one the topic names (or is missing), is refused.</summary>
    private static void RequireOnlyTheReferenceOf(StartOwnerConversation command)
    {
        var valid = command.Topic switch
        {
            OwnerConversationTopic.General => command.PropertyId is null && command.OwnerStayId is null && command.DocumentId is null && command.StatementVersionId is null,
            OwnerConversationTopic.Property => command.PropertyId is not null && command.OwnerStayId is null && command.DocumentId is null && command.StatementVersionId is null,
            OwnerConversationTopic.OwnerStay => command.OwnerStayId is not null && command.PropertyId is null && command.DocumentId is null && command.StatementVersionId is null,
            OwnerConversationTopic.Document => command.DocumentId is not null && command.PropertyId is null && command.OwnerStayId is null && command.StatementVersionId is null,
            OwnerConversationTopic.StatementVersion => command.StatementVersionId is not null && command.PropertyId is null && command.OwnerStayId is null && command.DocumentId is null,
            _ => false,
        };
        if (!valid)
            throw ConversationInvalid("topic");
    }

    private static OwnerConversation NewConversation(ConversationTarget target, string subject, DateTime now) => new()
    {
        OrgId = target.OrgId,
        PropertyOwnerId = target.PropertyOwnerId,
        Topic = target.Topic,
        PropertyId = target.PropertyId,
        OwnerStayRequestId = target.OwnerStayId,
        OwnerDocumentId = target.DocumentId,
        OwnerStatementVersionId = target.StatementVersionId,
        Subject = subject,
        CreatedAt = now,
        LastMessageAt = now,
    };

    /// <summary>The subject and the words of a message, trimmed and bounded; the words may be empty only next to a file.</summary>
    private static (string Subject, string Body) Words(string? subject, string? body, bool attachment)
    {
        var cleanSubject = (subject ?? string.Empty).Trim();
        if (cleanSubject.Length is 0 or > OwnerConversation.SubjectMaxLength)
            throw ConversationInvalid("subject");

        return (cleanSubject, Body(body, attachment));
    }

    private static string Body(string? body, bool attachment)
    {
        var clean = (body ?? string.Empty).Trim();
        if ((clean.Length == 0 && !attachment) || clean.Length > OwnerMessage.BodyMaxLength)
            throw new DomainRuleException(OwnerCollaborationErrors.MessageInvalid, OwnerCollaborationErrors.MessageInvalidMessageKey, OwnerMessage.BodyMaxLength);
        return clean;
    }

    /// <summary>Writes a message (with its file, stored after the scan) and moves the conversation to the top of the inbox.</summary>
    private async Task<OwnerMessage> PostAsync(
        Guid conversationId,
        OwnerMessageSender sender,
        string? senderUserId,
        Guid? senderOwnerUserId,
        string? body,
        OwnerFileUpload? attachment,
        CancellationToken cancellationToken)
    {
        var words = Body(body, attachment is not null);
        var conversation = await Conversations().FirstAsync(c => c.Id == conversationId, cancellationToken);
        var now = Now();
        var message = new OwnerMessage
        {
            OrgId = conversation.OrgId,
            ConversationId = conversation.Id,
            Sender = sender,
            SenderUserId = senderUserId,
            SenderOwnerUserId = senderOwnerUserId,
            Body = words,
            CreatedAt = now,
        };

        if (attachment is not null)
        {
            var verdict = Scan(attachment);
            message.AttachmentKey = StorageKeys.OwnerMessageAttachment(conversation.OrgId, conversation.Id, $"{message.Id:N}{verdict.Extension}");
            message.AttachmentFileName = OwnerFileRules.SafeFileName(attachment.FileName, OwnerDocumentVersion.FileNameMaxLength);
            message.AttachmentContentType = verdict.ContentType;
            message.AttachmentSizeBytes = attachment.Content.LongLength;
            message.AttachmentSha256 = Convert.ToHexStringLower(SHA256.HashData(attachment.Content));
            message.AttachmentScanEngine = scanner.Engine;
            await StoreAsync(message.AttachmentKey, attachment.Content, verdict.ContentType!, cancellationToken);
        }

        db.OwnerMessages.Add(message);
        conversation.LastMessageAt = now;
        if (message.AttachmentKey is { } key)
            await SaveOrForgetFileAsync(key, cancellationToken);
        else
            await db.SaveChangesAsync(cancellationToken);

        return message;
    }

    private async Task<OwnerFileContent> AttachmentAsync(OwnerConversation conversation, Guid messageId, CancellationToken cancellationToken)
    {
        var message = await Messages().AsNoTracking()
            .FirstOrDefaultAsync(m => m.Id == messageId && m.ConversationId == conversation.Id && m.AttachmentKey != null, cancellationToken)
            ?? throw AttachmentNotFound(messageId);
        var content = await storage.OpenReadAsync(StorageBucket.Private, message.AttachmentKey!, cancellationToken);
        if (content is null)
        {
            logger.LogError("Owner message {MessageId}: its file is missing from the storage", message.Id);
            throw AttachmentNotFound(messageId);
        }

        return new OwnerFileContent(content, message.AttachmentFileName ?? "file", message.AttachmentContentType ?? "application/octet-stream");
    }

    private async Task<List<OwnerMessageView>> MessageViewsAsync(OwnerConversation conversation, CancellationToken cancellationToken)
    {
        var messages = await Messages().AsNoTracking()
            .Where(m => m.ConversationId == conversation.Id)
            .OrderBy(m => m.CreatedAt)
            .ThenBy(m => m.Id)
            .ToListAsync(cancellationToken);
        var people = await OwnerUserNamesAsync(messages.Where(m => m.SenderOwnerUserId != null).Select(m => m.SenderOwnerUserId!.Value), cancellationToken);
        var agency = await db.Orgs.AsNoTracking().Where(o => o.Id == conversation.OrgId).Select(o => o.Name).FirstOrDefaultAsync(cancellationToken) ?? string.Empty;

        return messages.Select(m => new OwnerMessageView(
            m.Id,
            m.ConversationId,
            m.Sender,
            m.Sender == OwnerMessageSender.Agency ? agency : people.GetValueOrDefault(m.SenderOwnerUserId ?? Guid.Empty, string.Empty),
            m.Body,
            m.AttachmentKey is null ? null : new OwnerAttachmentView(m.AttachmentFileName ?? "file", m.AttachmentContentType ?? string.Empty, m.AttachmentSizeBytes ?? 0),
            m.CreatedAt)).ToList();
    }

    private static OwnerConversationView ConversationView(OwnerConversation c) =>
        new(c.Id, c.OrgId, c.PropertyOwnerId, c.Topic, c.PropertyId, c.OwnerStayRequestId, c.OwnerDocumentId, c.OwnerStatementVersionId, c.Subject, c.CreatedAt, c.LastMessageAt);

    /// <summary>
    /// Tells the owner side that the agency wrote: the owners of the owner profile (an ownership in force) and its active contacts with <c>inbox.use</c>, each
    /// unless it opted out. The e-mail names the agency and links to the conversation: nothing of the message.
    /// </summary>
    private async Task TellOwnerSideAsync(OwnerConversation conversation, CancellationToken cancellationToken)
    {
        if (!links.IsConfigured)
        {
            logger.LogWarning("Owner conversation {ConversationId}: no e-mail, the public address of the web app is not configured", conversation.Id);
            return;
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var owners = await OwnerPortalAccessResolver.InForce(db, now)
            .Where(o => o.OrgId == conversation.OrgId && o.PropertyOwnerId == conversation.PropertyOwnerId && o.OwnerUserId != null)
            .Select(o => o.OwnerUserId!.Value)
            .Distinct()
            .ToListAsync(cancellationToken);
        var contacts = await Contacts().AsNoTracking()
            .Where(c => c.OrgId == conversation.OrgId && c.PropertyOwnerId == conversation.PropertyOwnerId
                        && c.Status == OwnerPortalContactStatus.Active && c.OwnerUserId != null)
            .Select(c => new { c.OwnerUserId, c.Permissions })
            .ToListAsync(cancellationToken);
        var people = owners
            .Concat(contacts.Where(c => c.Permissions.HasFlag(OwnerPortalPermission.InboxUse)).Select(c => c.OwnerUserId!.Value))
            .Distinct()
            .ToList();
        var addresses = await db.OwnerUsers.AsNoTracking()
            .Where(u => people.Contains(u.Id) && !u.InboxEmailOptOut)
            .Select(u => u.Email)
            .Distinct()
            .ToListAsync(cancellationToken);

        var agency = await db.Orgs.AsNoTracking().Where(o => o.Id == conversation.OrgId).Select(o => o.Name).FirstOrDefaultAsync(cancellationToken) ?? string.Empty;
        var content = EmailTemplates.OwnerInboxMessage(new CultureInfo("it-IT"), agency, links.OwnerPortalConversation(conversation.Id));
        foreach (var address in addresses)
            emailQueue.Enqueue(address, content, EmailTemplates.Names.OwnerInboxMessage);
    }

    /// <summary>Tells the agency that an owner wrote: one e-mail to the contact address of the org, in its language, with the link to the conversation.</summary>
    private async Task TellAgencyAsync(OwnerConversation conversation, CancellationToken cancellationToken)
    {
        if (!links.IsConfigured)
        {
            logger.LogWarning("Owner conversation {ConversationId}: no e-mail, the public address of the web app is not configured", conversation.Id);
            return;
        }

        var org = await db.Orgs.AsNoTracking()
            .Where(o => o.Id == conversation.OrgId)
            .Select(o => new { o.ContactEmail, o.DefaultLanguage })
            .FirstOrDefaultAsync(cancellationToken);
        if (org is null || string.IsNullOrWhiteSpace(org.ContactEmail))
            return;

        var content = EmailTemplates.OwnerInboxMessageToAgency(OrgInvitationEmails.CultureOf(org.DefaultLanguage), links.AgencyOwnerConversation(conversation.Id));
        emailQueue.Enqueue(org.ContactEmail, content, EmailTemplates.Names.OwnerInboxMessageToAgency);
    }

    private static DomainRuleException ConversationInvalid(string field) =>
        new(OwnerCollaborationErrors.ConversationInvalid, OwnerCollaborationErrors.ConversationInvalidMessageKey, field);

    private static NotFoundException ReferenceNotFound() =>
        new("Owner conversation reference not found")
        {
            Code = OwnerCollaborationErrors.ConversationReferenceNotFound,
            MessageKey = OwnerCollaborationErrors.ConversationReferenceNotFoundMessageKey,
        };

    private static NotFoundException ConversationNotFound(Guid conversationId) =>
        new($"Owner conversation {conversationId} not found")
        {
            Code = OwnerCollaborationErrors.ConversationNotFound,
            MessageKey = OwnerCollaborationErrors.ConversationNotFoundMessageKey,
        };

    private static NotFoundException AttachmentNotFound(Guid messageId) =>
        new($"Owner message attachment {messageId} not found")
        {
            Code = OwnerCollaborationErrors.AttachmentNotFound,
            MessageKey = OwnerCollaborationErrors.AttachmentNotFoundMessageKey,
        };
}
