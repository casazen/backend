using System.Security.Cryptography;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Services;

public sealed class StorefrontChatService(AppDbContext db, TimeProvider? clock = null)
{
    public const string AccessHeader = "X-Storefront-Chat";

    internal IQueryable<StorefrontChatThread> InHostScope(HostScope scope) =>
        db.StorefrontChatThreads.Where(t => t.OrgId == scope.OrgId).InScope(scope, t => t.Property);

    public async Task<IReadOnlyList<StorefrontChatLine>> GetAsync(Guid orgId, Guid propertyId, string? access, CancellationToken ct)
    {
        var thread = await PublicThreads(orgId, propertyId, Hash(access)).AsNoTracking().SingleOrDefaultAsync(ct)
            ?? throw NotFound();
        return (await LatestAsync(thread, ct)).Select(m => new StorefrontChatLine(m.Id, m.Direction == OtaMessageDirection.Outbound, m.Text, m.SentAt)).ToList();
    }

    public async Task SendAsync(
        Guid orgId, Guid propertyId, string? access, string? idempotencyKey, string? text, string? name, string? email, bool consent, CancellationToken ct)
    {
        var hash = Hash(access);
        var body = ValidateText(text);
        var key = ValidateKey(idempotencyKey);
        await using var transaction = await LockAsync(hash, ct);
        var thread = await PublicThreads(orgId, propertyId, hash).SingleOrDefaultAsync(ct);
        if (thread is null)
        {
            var visitorName = name?.Trim();
            var visitorEmail = email?.Trim();
            if (!consent || string.IsNullOrEmpty(visitorName) || visitorName.Length > 100
                || string.IsNullOrEmpty(visitorEmail) || visitorEmail.Length > 254
                || !System.Net.Mail.MailAddress.TryCreate(visitorEmail, out var address) || address.Address != visitorEmail)
            {
                throw new ValidationException("Provide your name, email and consent to start the chat.");
            }

            var published = await db.Properties.IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
                .Where(PublicListing.IsPublished).AnyAsync(p => p.OrgId == orgId && p.Id == propertyId, ct);
            if (!published)
                throw NotFound();

            thread = new StorefrontChatThread
            {
                Id = Guid.NewGuid(), OrgId = orgId, PropertyId = propertyId, AccessHash = hash,
                VisitorName = visitorName, VisitorEmail = visitorEmail,
            };
            db.StorefrontChatThreads.Add(thread);
        }

        if (await FindMessageAsync(thread, OtaMessageDirection.Inbound, key, ct) is null)
        {
            AddMessage(thread, OtaMessageDirection.Inbound, key, body);
            await db.SaveChangesAsync(ct);
        }

        if (transaction is not null)
            await transaction.CommitAsync(ct);
    }

    internal async Task<MessageThreadView> GetForHostAsync(HostScope scope, MessageThreadKey key, CancellationToken ct)
    {
        var thread = await InHostScope(scope).AsNoTracking().Include(t => t.Property).SingleOrDefaultAsync(t => t.Id == key.Id, ct)
            ?? throw NotFound();
        var messages = await LatestAsync(thread, ct, GuestMessageRules.TranscriptMaxMessages + 1);
        return new MessageThreadView(key, MessageThreadSource.Storefront, MessageHubChannel.Storefront, MessageHubThreadKind.Request,
            MessageHubThreadState.Open, thread.PropertyId, thread.Property.Name, false, thread.VisitorName, thread.UnreadCount,
            thread.NeedsReply, false, null, null, new MessageThreadActions(true, true, false, false, null),
            messages.TakeLast(GuestMessageRules.TranscriptMaxMessages).Select(ToHubMessage).ToList(),
            messages.Count > GuestMessageRules.TranscriptMaxMessages, thread.VisitorEmail);
    }

    internal async Task<MessageHubReadResult> MarkReadAsync(HostScope scope, MessageThreadKey key, CancellationToken ct)
    {
        var hash = await HostHashAsync(scope, key, ct);
        await using var transaction = await LockAsync(hash, ct);
        var thread = await InHostScope(scope).SingleOrDefaultAsync(t => t.Id == key.Id, ct) ?? throw NotFound();
        if (thread.UnreadCount > 0)
        {
            var unread = await Messages(thread).Where(m => m.Direction == OtaMessageDirection.Inbound && m.ReadAt == null).ToListAsync(ct);
            var now = Now();
            foreach (var message in unread)
                message.ReadAt = now;
            thread.UnreadCount = 0;
            await db.SaveChangesAsync(ct);
        }

        if (transaction is not null)
            await transaction.CommitAsync(ct);
        return new MessageHubReadResult(key, 0);
    }

    internal async Task<MessageReplyResult> ReplyAsync(HostScope scope, MessageThreadKey key, string? idempotencyKey, string? text, CancellationToken ct)
    {
        var body = ValidateText(text);
        var messageKey = ValidateKey(idempotencyKey);
        var hash = await HostHashAsync(scope, key, ct);
        await using var transaction = await LockAsync(hash, ct);
        var thread = await InHostScope(scope).SingleOrDefaultAsync(t => t.Id == key.Id, ct) ?? throw NotFound();
        var message = await FindMessageAsync(thread, OtaMessageDirection.Outbound, messageKey, ct);
        var replayed = message is not null;
        if (message is null)
        {
            message = AddMessage(thread, OtaMessageDirection.Outbound, messageKey, body);
            await db.SaveChangesAsync(ct);
        }

        if (transaction is not null)
            await transaction.CommitAsync(ct);
        return new MessageReplyResult(MessageContactChannel.Direct, key, ToHubMessage(message), OtaMessageDeliveryState.Sent, replayed, null);
    }

    private Task<string> HostHashAsync(HostScope scope, MessageThreadKey key, CancellationToken ct) =>
        RequireHashAsync(InHostScope(scope).Where(t => t.Id == key.Id).Select(t => t.AccessHash), ct);

    private static async Task<string> RequireHashAsync(IQueryable<string> hashes, CancellationToken ct) =>
        await hashes.SingleOrDefaultAsync(ct) ?? throw NotFound();

    private IQueryable<StorefrontChatThread> PublicThreads(Guid orgId, Guid propertyId, string hash) =>
        db.StorefrontChatThreads.IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .Where(t => t.OrgId == orgId && t.PropertyId == propertyId && t.AccessHash == hash);

    private IQueryable<StorefrontChatMessage> Messages(StorefrontChatThread thread) =>
        db.StorefrontChatMessages.IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .Where(m => m.OrgId == thread.OrgId && m.ThreadId == thread.Id);

    private async Task<List<StorefrontChatMessage>> LatestAsync(StorefrontChatThread thread, CancellationToken ct,
        int limit = GuestMessageRules.TranscriptMaxMessages)
    {
        var result = await Messages(thread).AsNoTracking().OrderByDescending(m => m.SentAt).ThenByDescending(m => m.Id).Take(limit).ToListAsync(ct);
        result.Reverse();
        return result;
    }

    private Task<StorefrontChatMessage?> FindMessageAsync(StorefrontChatThread thread, OtaMessageDirection direction, string key, CancellationToken ct) =>
        Messages(thread).SingleOrDefaultAsync(m => m.Direction == direction && m.IdempotencyKey == key, ct);

    private StorefrontChatMessage AddMessage(StorefrontChatThread thread, OtaMessageDirection direction, string key, string body)
    {
        var now = Now();
        var message = new StorefrontChatMessage
        {
            Id = Guid.NewGuid(), OrgId = thread.OrgId, ThreadId = thread.Id, Direction = direction,
            IdempotencyKey = key, Text = body, SentAt = now,
        };
        db.StorefrontChatMessages.Add(message);
        thread.LastMessageAt = now;
        thread.NeedsReply = direction == OtaMessageDirection.Inbound;
        if (thread.NeedsReply)
            thread.UnreadCount++;
        return message;
    }

    private Task<Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction?> LockAsync(string hash, CancellationToken ct) =>
        PostgresAdvisoryLocks.BeginLockedTransactionAsync(db, ct, (PostgresAdvisoryLocks.Scope.StorefrontChat, hash));

    private static string Hash(string? access)
    {
        if (access is not { Length: 64 } || !access.All(Uri.IsHexDigit))
            throw NotFound();
        return Convert.ToHexString(SHA256.HashData(Convert.FromHexString(access)));
    }

    private DateTime Now() => (clock ?? TimeProvider.System).GetUtcNow().UtcDateTime;

    private static string ValidateText(string? text) =>
        GuestMessageRules.NormalizeText(text) is { Length: <= GuestMessageRules.TextMaxLength } body
            ? body : throw new DomainRuleException(MessageChannelRules.TextInvalidCode, "GuestMessageTextInvalid");

    private static string ValidateKey(string? key) =>
        GuestMessageRules.NormalizeIdempotencyKey(key) ?? throw new DomainRuleException("idempotency_key_invalid", "IdempotencyKeyInvalid");

    private static NotFoundException NotFound() => new("Storefront conversation not found.") { Code = "thread_not_found" };

    private static MessageHubMessage ToHubMessage(StorefrontChatMessage m) =>
        new(m.Id, m.Direction, m.Direction == OtaMessageDirection.Inbound ? MessageOrigin.Guest : MessageOrigin.Person,
            null, null, m.Text, m.SentAt, m.ReadAt, null, null);
}

public sealed record StorefrontChatLine(Guid Id, bool FromHost, string Text, DateTime SentAt);
