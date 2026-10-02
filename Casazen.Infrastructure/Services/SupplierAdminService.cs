using System.Linq.Expressions;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Email.Templates;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// Platform admin management of suppliers and supplier invites (SU-12, A4-29). Every change and its audit entry are
/// saved together. Who may call it is decided by the web layer (<c>AdminOnly</c>); the actor is only recorded.
/// </summary>
public sealed class SupplierAdminService(
    AppDbContext db,
    IEmailQueue emailQueue,
    PublicSiteLinks publicSiteLinks,
    ISupplierPilotComuni pilotComuni,
    IComuneDirectory comuneDirectory,
    TimeProvider timeProvider,
    ILogger<SupplierAdminService> logger) : ISupplierAdminService
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    /// <summary>Requests of a supplier that still wait for its work: the ones that count when suspending it.</summary>
    private static readonly ServiceRequestStatus[] OpenStatuses =
    [
        ServiceRequestStatus.Richiesto,
        ServiceRequestStatus.PresoInCarico,
        ServiceRequestStatus.InCorso,
    ];

    public async Task<(IReadOnlyList<AdminSupplierItem> Items, int Total)> ListAsync(
        AdminSupplierListQuery query,
        CancellationToken cancellationToken = default)
    {
        var page = Math.Max(query.Page, 1);
        var pageSize = Math.Clamp(query.PageSize, 1, 100);

        // SupplierProfile is listed across every supplier org on purpose: this is the platform admin's view (the
        // endpoint is AdminOnly), and the profile has no tenant filter (see the TN-2 allow-list).
        var profiles = db.SupplierProfiles.AsNoTracking().AsQueryable();

        if (query.Status is { } status)
            profiles = profiles.Where(sp => sp.Status == status);

        if (LikePattern(query.Search) is { } pattern)
        {
            profiles = profiles.Where(sp =>
                EF.Functions.ILike(sp.LegalName, pattern, LikeEscape) ||
                EF.Functions.ILike(sp.Email, pattern, LikeEscape));
        }

        var total = await profiles.CountAsync(cancellationToken);

        var rows = await profiles
            .OrderByDescending(sp => sp.CreatedAt)
            .ThenBy(sp => sp.OrgId)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(sp => new SupplierRow(
                sp.OrgId,
                sp.LegalName,
                sp.Email,
                sp.Phone,
                sp.Status,
                sp.CategoriesJson,
                sp.ComuniJson,
                sp.CreatedAt,
                sp.SuspendedAt,
                sp.SuspensionReason,
                db.ServiceRequests.Count(r => r.SupplierOrgId == sp.OrgId && OpenStatuses.Contains(r.Status))))
            .ToListAsync(cancellationToken);

        return (rows.Select(row => ToItem(row)).ToList(), total);
    }

    public async Task<AdminSupplierItem> SuspendAsync(
        Guid orgId,
        string actorUserId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorUserId);
        // The API requires a reason of at most 500 characters (400 validation_error): a blank one is a bug here.
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        var profile = await GetProfileOrThrowAsync(orgId, cancellationToken);
        if (profile.Status == SupplierStatus.Suspended)
        {
            throw new DomainConflictException(
                SupplierAdminErrorCodes.AlreadySuspended, SupplierAdminErrorCodes.AlreadySuspendedMessageKey);
        }

        var now = Now();
        var previous = profile.Status;
        profile.Status = SupplierStatus.Suspended;
        profile.SuspendedAt = now;
        profile.SuspensionReason = reason.Trim();
        profile.UpdatedAt = now;

        db.SupplierAdminAuditEntries.Add(new SupplierAdminAuditEntry
        {
            Action = SupplierAdminAuditAction.Suspended,
            SupplierOrgId = orgId,
            ActorUserId = actorUserId,
            OccurredAt = now,
            Reason = profile.SuspensionReason,
            PreviousStatus = previous,
            NewStatus = SupplierStatus.Suspended,
        });
        await db.SaveChangesAsync(cancellationToken);

        logger.LogWarning(
            "Supplier {SupplierOrgId} suspended by admin {ActorUserId} (was {PreviousStatus})",
            orgId, actorUserId, previous);
        return await ToItemAsync(profile, cancellationToken);
    }

    public async Task<AdminSupplierItem> ReactivateAsync(
        Guid orgId,
        string actorUserId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorUserId);

        var profile = await GetProfileOrThrowAsync(orgId, cancellationToken);
        if (profile.Status != SupplierStatus.Suspended)
        {
            throw new DomainConflictException(
                SupplierAdminErrorCodes.NotSuspended, SupplierAdminErrorCodes.NotSuspendedMessageKey);
        }

        // Only the activation wizard sets the terms acceptance together with Active: a supplier that never accepted the
        // terms was never active, and goes back to the wizard instead of skipping it.
        var restored = profile.TosAcceptedAt.HasValue ? SupplierStatus.Active : SupplierStatus.Pending;
        var now = Now();
        profile.Status = restored;
        profile.SuspendedAt = null;
        profile.SuspensionReason = null;
        profile.UpdatedAt = now;

        db.SupplierAdminAuditEntries.Add(new SupplierAdminAuditEntry
        {
            Action = SupplierAdminAuditAction.Reactivated,
            SupplierOrgId = orgId,
            ActorUserId = actorUserId,
            OccurredAt = now,
            PreviousStatus = SupplierStatus.Suspended,
            NewStatus = restored,
        });
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Supplier {SupplierOrgId} reactivated by admin {ActorUserId} ({NewStatus})", orgId, actorUserId, restored);
        return await ToItemAsync(profile, cancellationToken);
    }

    public async Task<IReadOnlyList<SupplierAdminAuditItem>> GetAuditAsync(
        Guid orgId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var exists = await db.SupplierProfiles.AsNoTracking().AnyAsync(sp => sp.OrgId == orgId, cancellationToken);
        if (!exists)
            throw SupplierNotFound(orgId);

        var entries = await db.SupplierAdminAuditEntries
            .AsNoTracking()
            .Where(e => e.SupplierOrgId == orgId)
            .OrderByDescending(e => e.OccurredAt)
            .ThenByDescending(e => e.Id)
            .Take(Math.Clamp(limit, 1, 200))
            .ToListAsync(cancellationToken);

        // User is not tenant-filtered (identity table): the admin's name is read by the actor's own id.
        var actorIds = entries.Select(e => e.ActorUserId).Distinct().ToList();
        var actors = await db.Users.AsNoTracking()
            .Where(u => actorIds.Contains(u.Id))
            .Select(u => new { u.Id, u.FirstName, u.LastName, u.Email })
            .ToDictionaryAsync(u => u.Id, cancellationToken);

        return entries
            .Select(e => new SupplierAdminAuditItem(
                e.Id,
                e.Action,
                e.SupplierOrgId,
                e.InviteId,
                e.ActorUserId,
                actors.TryGetValue(e.ActorUserId, out var actor) ? ActorName(actor.FirstName, actor.LastName, actor.Email) : null,
                e.OccurredAt,
                e.Reason,
                e.PreviousStatus,
                e.NewStatus))
            .ToList();
    }

    public async Task<(IReadOnlyList<AdminInviteItem> Items, int Total)> ListInvitesAsync(
        AdminInviteListQuery query,
        CancellationToken cancellationToken = default)
    {
        var page = Math.Max(query.Page, 1);
        var pageSize = Math.Clamp(query.PageSize, 1, 100);
        var now = Now();

        // Invites have no org: the admin lists them all (the endpoint is AdminOnly).
        var invites = db.SupplierInviteRecords.AsNoTracking().AsQueryable();

        if (query.State is { } state)
            invites = invites.Where(StatePredicate(state, now));

        if (LikePattern(query.Search) is { } pattern)
            invites = invites.Where(i => EF.Functions.ILike(i.Email, pattern, LikeEscape));

        var total = await invites.CountAsync(cancellationToken);
        var rows = await invites
            .OrderByDescending(i => i.CreatedAt)
            .ThenBy(i => i.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return (rows.Select(invite => ToInviteItem(invite, now)).ToList(), total);
    }

    public async Task<SupplierInvite> ResendInviteAsync(
        Guid inviteId,
        string actorUserId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorUserId);

        var invite = await GetInviteOrThrowAsync(inviteId, cancellationToken);
        var now = Now();
        if (invite.IsUsed || invite.RevokedAt is not null)
        {
            throw new DomainConflictException(
                SupplierAdminErrorCodes.InviteNotResendable, SupplierAdminErrorCodes.InviteNotResendableMessageKey);
        }

        // Another pending invite for the email would leave two working links: the admin revokes or waits for it.
        var anotherPending = await db.SupplierInviteRecords.AsNoTracking()
            .Where(StatePredicate(SupplierInviteState.Pending, now))
            .AnyAsync(i => i.Id != inviteId && i.Email == invite.Email, cancellationToken);
        if (anotherPending)
        {
            throw new DomainConflictException(
                SupplierAdminErrorCodes.DuplicateInvite, SupplierAdminErrorCodes.DuplicateInviteMessageKey);
        }

        // A profile with the email since the invite was written: it could never be accepted (same rule as the creation).
        var normalizedEmail = SupplierProfileEmailIndex.Normalize(invite.Email);
        if (normalizedEmail.Length > 0 &&
            await db.SupplierProfiles.AsNoTracking().AnyAsync(sp => sp.Email.Trim().ToLower() == normalizedEmail, cancellationToken))
        {
            throw new DomainConflictException("supplier_email_taken", "SupplierEmailTaken");
        }

        // A new token: the one of the first email is only a hash, and the old link must stop working.
        var token = SupplierInviteTokens.Generate();
        invite.TokenHash = SupplierInviteTokens.Hash(token);
        invite.ExpiresAt = now.Add(SupplierInviteTokens.Validity);

        // Rendered before saving: a missing App:PublicSiteBaseUrl is a configuration error, not an invite with a wrong link.
        var comuneName = await SupplierInviteEmails.ResolveComuneNameAsync(pilotComuni, comuneDirectory, invite.ComuneCode, cancellationToken);
        var email = SupplierInviteEmails.Build(publicSiteLinks, invite, token, comuneName);

        db.SupplierAdminAuditEntries.Add(new SupplierAdminAuditEntry
        {
            Action = SupplierAdminAuditAction.InviteResent,
            InviteId = invite.Id,
            ActorUserId = actorUserId,
            OccurredAt = now,
        });
        await db.SaveChangesAsync(cancellationToken);

        if (!emailQueue.Enqueue(invite.Email, email, EmailTemplates.Names.SupplierInvite))
            logger.LogWarning("Supplier invite {InviteId} resent but its email was not queued", invite.Id);

        logger.LogInformation(
            "Supplier invite {InviteId} for {MaskedEmail} resent by admin {ActorUserId}, expires {ExpiresAt}",
            invite.Id, LogRedaction.MaskEmail(invite.Email), actorUserId, invite.ExpiresAt);
        return new SupplierInvite(invite.Id, invite.ExpiresAt);
    }

    public async Task RevokeInviteAsync(
        Guid inviteId,
        string actorUserId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorUserId);

        var invite = await GetInviteOrThrowAsync(inviteId, cancellationToken);
        var now = Now();
        if (ToState(invite, now) != SupplierInviteState.Pending)
        {
            throw new DomainConflictException(
                SupplierAdminErrorCodes.InviteNotPending, SupplierAdminErrorCodes.InviteNotPendingMessageKey);
        }

        invite.RevokedAt = now;
        db.SupplierAdminAuditEntries.Add(new SupplierAdminAuditEntry
        {
            Action = SupplierAdminAuditAction.InviteRevoked,
            InviteId = invite.Id,
            ActorUserId = actorUserId,
            OccurredAt = now,
        });
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Supplier invite {InviteId} for {MaskedEmail} revoked by admin {ActorUserId}",
            invite.Id, LogRedaction.MaskEmail(invite.Email), actorUserId);
    }

    // ─── helpers ───

    private DateTime Now() => timeProvider.GetUtcNow().UtcDateTime;

    private async Task<SupplierProfile> GetProfileOrThrowAsync(Guid orgId, CancellationToken cancellationToken) =>
        await db.SupplierProfiles.FirstOrDefaultAsync(sp => sp.OrgId == orgId, cancellationToken)
        ?? throw SupplierNotFound(orgId);

    private async Task<SupplierInviteRecord> GetInviteOrThrowAsync(Guid inviteId, CancellationToken cancellationToken) =>
        await db.SupplierInviteRecords.FirstOrDefaultAsync(i => i.Id == inviteId, cancellationToken)
        ?? throw new NotFoundException($"Supplier invite {inviteId} not found")
        {
            Code = SupplierAdminErrorCodes.InviteNotFound,
            MessageKey = SupplierAdminErrorCodes.InviteNotFoundMessageKey,
        };

    private static NotFoundException SupplierNotFound(Guid orgId) => new($"Supplier {orgId} not found")
    {
        Code = SupplierAdminErrorCodes.SupplierNotFound,
        MessageKey = SupplierAdminErrorCodes.SupplierNotFoundMessageKey,
    };

    private async Task<AdminSupplierItem> ToItemAsync(SupplierProfile profile, CancellationToken cancellationToken)
    {
        var open = await db.ServiceRequests.AsNoTracking()
            .CountAsync(r => r.SupplierOrgId == profile.OrgId && OpenStatuses.Contains(r.Status), cancellationToken);

        return ToItem(new SupplierRow(
            profile.OrgId,
            profile.LegalName,
            profile.Email,
            profile.Phone,
            profile.Status,
            profile.CategoriesJson,
            profile.ComuniJson,
            profile.CreatedAt,
            profile.SuspendedAt,
            profile.SuspensionReason,
            open));
    }

    private static AdminSupplierItem ToItem(SupplierRow row) => new(
        row.OrgId,
        row.LegalName,
        row.Email,
        row.Phone,
        row.Status,
        DeserializeStrings(row.CategoriesJson),
        DeserializeStrings(row.ComuniJson),
        row.CreatedAt,
        row.SuspendedAt,
        row.SuspensionReason,
        row.OpenRequests);

    private static AdminInviteItem ToInviteItem(SupplierInviteRecord invite, DateTime now) => new(
        invite.Id,
        invite.Email,
        invite.ComuneCode,
        DeserializeStrings(invite.CategoriesJson),
        invite.Message,
        ToState(invite, now),
        invite.CreatedAt,
        invite.ExpiresAt,
        invite.RevokedAt);

    /// <summary>The state of an invite at <paramref name="now"/>: the in-memory twin of <see cref="StatePredicate"/>.</summary>
    private static SupplierInviteState ToState(SupplierInviteRecord invite, DateTime now)
    {
        if (invite.IsUsed) return SupplierInviteState.Used;
        if (invite.RevokedAt is not null) return SupplierInviteState.Revoked;
        return invite.TokenHash is null || invite.ExpiresAt <= now ? SupplierInviteState.Expired : SupplierInviteState.Pending;
    }

    /// <summary>
    /// The SQL form of <see cref="ToState"/>: an invite created before SU-01 has no token hash and can no longer be
    /// accepted, so it counts as expired (it can be sent again).
    /// </summary>
    private static Expression<Func<SupplierInviteRecord, bool>> StatePredicate(SupplierInviteState state, DateTime now) =>
        state switch
        {
            SupplierInviteState.Used => i => i.IsUsed,
            SupplierInviteState.Revoked => i => !i.IsUsed && i.RevokedAt != null,
            SupplierInviteState.Expired => i => !i.IsUsed && i.RevokedAt == null && (i.TokenHash == null || i.ExpiresAt <= now),
            _ => i => !i.IsUsed && i.RevokedAt == null && i.TokenHash != null && i.ExpiresAt > now,
        };

    private const string LikeEscape = "\\";

    /// <summary>
    /// <c>%text%</c> with the LIKE wildcards of the text escaped, or null for no search: a search for <c>50%</c> or
    /// <c>a_b</c> finds those characters, not everything.
    /// </summary>
    private static string? LikePattern(string? search)
    {
        var text = search?.Trim();
        if (string.IsNullOrEmpty(text))
            return null;

        var escaped = text.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);
        return $"%{escaped}%";
    }

    private static string? ActorName(string? firstName, string? lastName, string? email)
    {
        var name = $"{firstName} {lastName}".Trim();
        if (name.Length > 0) return name;
        return string.IsNullOrWhiteSpace(email) ? null : email.Trim();
    }

    private static IReadOnlyList<string> DeserializeStrings(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        try
        {
            return JsonSerializer.Deserialize<string[]>(json, JsonOpts) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private sealed record SupplierRow(
        Guid OrgId,
        string LegalName,
        string Email,
        string Phone,
        SupplierStatus Status,
        string CategoriesJson,
        string ComuniJson,
        DateTime CreatedAt,
        DateTime? SuspendedAt,
        string? SuspensionReason,
        int OpenRequests);
}
