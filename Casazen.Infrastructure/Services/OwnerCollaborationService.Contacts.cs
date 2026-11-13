using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
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
/// The delegated contacts of the owners (E07/F07/US01/T02, AC-NF111-4): invited by the owner for one owner profile, accepted with the contact's own account,
/// holding only the permissions given; withdrawn by the owner or the agency, effective at the next request (the access is resolved at every request).
/// </summary>
public sealed partial class OwnerCollaborationService
{
    private static readonly EmailAddressAttribute EmailRule = new();

    public async Task<IReadOnlyList<OwnerContactView>> ListContactsForOwnerAsync(OwnerPortalAccess access, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(access);
        var profiles = OwnProfiles(access);
        var ownerIds = profiles.Select(p => p.PropertyOwnerId).ToList();
        var contacts = await Contacts().AsNoTracking()
            .Where(c => ownerIds.Contains(c.PropertyOwnerId))
            .OrderBy(c => c.CreatedAt)
            .ToListAsync(cancellationToken);
        return contacts.Where(c => profiles.Contains((c.OrgId, c.PropertyOwnerId))).Select(ContactView).ToList();
    }

    public async Task<OwnerContactView> InviteContactAsync(OwnerPortalAccess access, InviteOwnerContact command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(command);
        var (orgId, propertyOwnerId) = ProfileFor(OwnProfiles(access), command.AgencyId);

        var email = (command.Email ?? string.Empty).Trim().ToLowerInvariant();
        RequireContact(email.Length is > 0 and <= OwnerPortalContact.EmailMaxLength && EmailRule.IsValid(email), "email");
        var firstName = (command.FirstName ?? string.Empty).Trim();
        var lastName = (command.LastName ?? string.Empty).Trim();
        RequireContact(firstName.Length is > 0 and <= OwnerPortalContact.NameMaxLength, "firstName");
        RequireContact(lastName.Length is > 0 and <= OwnerPortalContact.NameMaxLength, "lastName");
        var permissions = OwnerPortalPermissions.Parse(command.Permissions);

        // The address is encrypted at rest: it is found, and kept unique for the owner profile, by its keyed hash (never by the address).
        var emailHash = contactIndex.HashEmail(email);

        // The link is built before anything is saved: a missing public address leaves no contact behind.
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var acceptUrl = links.OwnerContactAccept(token);

        var now = Now();
        OwnerPortalContact contact;
        await using (var transaction = await PostgresAdvisoryLocks.BeginLockedTransactionAsync(
                         db, cancellationToken, (PostgresAdvisoryLocks.Scope.OwnerOnboarding, orgId.ToString("N"))))
        {
            var inForce = await Contacts()
                .Where(c => c.OrgId == orgId && c.PropertyOwnerId == propertyOwnerId && c.Status != OwnerPortalContactStatus.Revoked)
                .ToListAsync(cancellationToken);

            // An invitation that lapsed for the same address is closed and replaced; a live one, or an active contact, is the same person already.
            foreach (var lapsed in inForce.Where(c => c.EmailHash == emailHash && c.Status == OwnerPortalContactStatus.Invited && c.InvitationExpiresAt <= now))
                Close(lapsed, now, access.Actor);
            if (inForce.Any(c => c.EmailHash == emailHash && c.Status != OwnerPortalContactStatus.Revoked))
                throw new DomainConflictException(OwnerCollaborationErrors.ContactExists, OwnerCollaborationErrors.ContactExistsMessageKey);
            if (inForce.Count(c => c.Status != OwnerPortalContactStatus.Revoked) >= OwnerCollaborationRules.MaxContactsPerOwner)
            {
                throw new DomainRuleException(
                    OwnerCollaborationErrors.ContactLimitReached, OwnerCollaborationErrors.ContactLimitReachedMessageKey, OwnerCollaborationRules.MaxContactsPerOwner);
            }

            contact = new OwnerPortalContact
            {
                OrgId = orgId,
                PropertyOwnerId = propertyOwnerId,
                Email = email,
                EmailHash = emailHash,
                FirstName = firstName,
                LastName = lastName,
                Permissions = permissions,
                Status = OwnerPortalContactStatus.Invited,
                TokenHash = HashToken(token),
                InvitationExpiresAt = now.AddDays(OwnerCollaborationRules.ContactInvitationDays),
                InvitedByOwnerUserId = access.OwnerUserId,
                CreatedAt = now,
                UpdatedAt = now,
            };

            // Through the DbSet: the Guid of the row is preset, so a row attached any other way would be tracked as modified.
            db.OwnerPortalContacts.Add(contact);
            Audit(orgId, contact.Id, OwnerPortalParts.Contact, OwnerPortalAuditStatuses.Invited, access.Actor);
            await db.SaveChangesAsync(cancellationToken);
            if (transaction is not null)
                await transaction.CommitAsync(cancellationToken);
        }

        // The e-mail is queued after the save; one that cannot be queued withdraws the invitation, so nobody is «invited» who was never written to.
        var agencyName = await db.Orgs.AsNoTracking().Where(o => o.Id == orgId).Select(o => o.Name).FirstOrDefaultAsync(cancellationToken) ?? string.Empty;
        var ownerName = (await OwnerUserNamesAsync([access.OwnerUserId], cancellationToken)).GetValueOrDefault(access.OwnerUserId, string.Empty);
        var content = EmailTemplates.OwnerContactInvitation(
            new CultureInfo("it-IT"), $"{firstName} {lastName}", ownerName, agencyName, acceptUrl, contact.InvitationExpiresAt);
        if (!emailQueue.Enqueue(email, content, EmailTemplates.Names.OwnerContactInvitation))
        {
            Close(contact, Now(), null);
            await db.SaveChangesAsync(cancellationToken);
            throw new DomainRuleException(OwnerCollaborationErrors.ContactEmailNotQueued, OwnerCollaborationErrors.ContactEmailNotQueuedMessageKey);
        }

        logger.LogInformation("Owner contact {ContactId} invited for owner {PropertyOwnerId} of org {OrgId}", contact.Id, propertyOwnerId, orgId);
        return ContactView(contact);
    }

    public async Task<OwnerContactView> SetContactPermissionsAsync(
        OwnerPortalAccess access,
        Guid contactId,
        IReadOnlyCollection<string>? permissions,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(access);
        var contact = await OwnContactAsync(access, contactId, cancellationToken);
        var mask = OwnerPortalPermissions.Parse(permissions);
        if (contact.Status == OwnerPortalContactStatus.Revoked)
            throw new DomainConflictException(OwnerCollaborationErrors.ContactRevoked, OwnerCollaborationErrors.ContactRevokedMessageKey);

        if (contact.Permissions != mask)
        {
            contact.Permissions = mask;
            contact.UpdatedAt = Now();
            Audit(contact.OrgId, contact.Id, OwnerPortalParts.Contact, OwnerPortalAuditStatuses.PermissionsChanged, access.Actor);
            await db.SaveChangesAsync(cancellationToken);
        }

        return ContactView(contact);
    }

    public async Task<OwnerContactView> RevokeContactByOwnerAsync(OwnerPortalAccess access, Guid contactId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(access);
        var contact = await OwnContactAsync(access, contactId, cancellationToken);
        return await RevokeAsync(contact, access.Actor, cancellationToken);
    }

    public async Task<IReadOnlyList<OwnerContactView>> ListContactsForAgencyAsync(Guid orgId, Guid? propertyOwnerId, CancellationToken cancellationToken = default)
    {
        var query = Contacts().AsNoTracking().Where(c => c.OrgId == orgId);
        if (propertyOwnerId is { } ownerId)
            query = query.Where(c => c.PropertyOwnerId == ownerId);

        var contacts = await query.OrderBy(c => c.CreatedAt).Take(OwnerCollaborationRules.MaxList).ToListAsync(cancellationToken);
        return contacts.Select(ContactView).ToList();
    }

    public async Task<OwnerContactView> RevokeContactByAgencyAsync(Guid orgId, Guid contactId, string actorUserId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorUserId);
        var contact = await Contacts().FirstOrDefaultAsync(c => c.Id == contactId && c.OrgId == orgId, cancellationToken)
            ?? throw ContactNotFound(contactId);
        return await RevokeAsync(contact, actorUserId, cancellationToken);
    }

    public async Task<OwnerContactAccepted> AcceptContactAsync(AcceptOwnerContact command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.Auth0Sub);
        var token = (command.Token ?? string.Empty).Trim();
        if (token.Length is < 40 or > 64 || !token.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            throw Gone(OwnerCollaborationErrors.InvitationInvalid, OwnerCollaborationErrors.InvitationInvalidMessageKey);

        var hash = HashToken(token);
        var head = await Contacts().AsNoTracking().Where(c => c.TokenHash == hash).Select(c => new { c.Id, c.OrgId }).FirstOrDefaultAsync(cancellationToken)
            ?? throw Gone(OwnerCollaborationErrors.InvitationInvalid, OwnerCollaborationErrors.InvitationInvalidMessageKey);

        // The locks of the owner invitations: the org of the contact, then the account (one identity per account, whichever invitation it accepts first).
        Guid ownerUserId;
        bool alreadyAccepted;
        await using (var transaction = await PostgresAdvisoryLocks.BeginLockedTransactionAsync(
                         db,
                         cancellationToken,
                         (PostgresAdvisoryLocks.Scope.OwnerOnboarding, head.OrgId.ToString("N")),
                         (PostgresAdvisoryLocks.Scope.OwnerOnboarding, $"owner-user:{command.Auth0Sub}")))
        {
            var contact = await Contacts().FirstAsync(c => c.Id == head.Id, cancellationToken);
            var existing = await db.OwnerUsers.FirstOrDefaultAsync(u => u.Auth0Sub == command.Auth0Sub, cancellationToken);
            var now = Now();
            switch (contact.Status)
            {
                case OwnerPortalContactStatus.Active when existing is not null && contact.OwnerUserId == existing.Id:
                    // A repeat by the same account: the same answer, nothing written.
                    alreadyAccepted = true;
                    ownerUserId = existing.Id;
                    break;
                case OwnerPortalContactStatus.Active:
                    throw Gone(OwnerCollaborationErrors.InvitationUsed, OwnerCollaborationErrors.InvitationUsedMessageKey);
                case OwnerPortalContactStatus.Revoked:
                    throw Gone(OwnerCollaborationErrors.InvitationRevoked, OwnerCollaborationErrors.InvitationRevokedMessageKey);
                default:
                    if (contact.InvitationExpiresAt <= now)
                        throw Gone(OwnerCollaborationErrors.InvitationExpired, OwnerCollaborationErrors.InvitationExpiredMessageKey);
                    if (!command.EmailVerified)
                        throw new DomainForbiddenException(OwnerCollaborationErrors.EmailNotVerified, OwnerCollaborationErrors.EmailNotVerifiedMessageKey);
                    if (!string.Equals(contact.Email, (command.Email ?? string.Empty).Trim().ToLowerInvariant(), StringComparison.Ordinal))
                        throw new DomainForbiddenException(OwnerCollaborationErrors.EmailMismatch, OwnerCollaborationErrors.EmailMismatchMessageKey);

                    // Its own account, never the owner's: an identity of the portal is created for it when it has none.
                    var user = existing;
                    if (user is null)
                    {
                        user = new OwnerUser
                        {
                            Auth0Sub = command.Auth0Sub,
                            Email = contact.Email,
                            FirstName = Cut(string.IsNullOrWhiteSpace(command.FirstName) ? contact.FirstName : command.FirstName.Trim()),
                            LastName = Cut(string.IsNullOrWhiteSpace(command.LastName) ? contact.LastName : command.LastName.Trim()),
                            CreatedAt = now,
                        };
                        db.OwnerUsers.Add(user);
                    }

                    user.LastLoginAt = now;
                    contact.OwnerUserId = user.Id;
                    contact.Status = OwnerPortalContactStatus.Active;
                    contact.AcceptedAt = now;
                    contact.UpdatedAt = now;
                    Audit(contact.OrgId, contact.Id, OwnerPortalParts.Contact, OwnerPortalAuditStatuses.Accepted, command.Auth0Sub);
                    await db.SaveChangesAsync(cancellationToken);
                    alreadyAccepted = false;
                    ownerUserId = user.Id;
                    break;
            }

            if (transaction is not null)
                await transaction.CommitAsync(cancellationToken);
        }

        // The claims of the portal for every ownership and delegation of the account now, handed to the identity provider outside the locks.
        var access = await accessResolver.ResolveAsync(ownerUserId, cancellationToken);
        var agencyIds = access.Grants.Select(g => g.OrgId).Distinct().Order().ToList();
        var propertyIds = access.Grants.Select(g => g.PropertyId).Distinct().Order().ToList();
        var sync = await auth0Management.SetOwnerPortalClaimsAsync(command.Auth0Sub, ownerUserId, agencyIds, propertyIds, cancellationToken);
        var refresh = sync.Status switch
        {
            Auth0SyncStatus.Synced => OwnerClaimsRefresh.Published,
            Auth0SyncStatus.NotConfigured => OwnerClaimsRefresh.NotConfigured,
            _ => OwnerClaimsRefresh.Failed,
        };

        logger.LogInformation("Owner contact {ContactId} accepted by OwnerUser {OwnerUserId}; claims {Refresh}", head.Id, ownerUserId, refresh);
        return new OwnerContactAccepted(ownerUserId, agencyIds, propertyIds, alreadyAccepted, refresh);
    }

    // ---------------------------------------------------------------- shared

    private IQueryable<OwnerPortalContact> Contacts() => db.OwnerPortalContacts.IgnoreQueryFilters([AppDbContext.TenantQueryFilter]);

    /// <summary>The owner profiles the caller owns itself (not through a delegation): only an owner manages its contacts.</summary>
    /// <exception cref="DomainForbiddenException"><see cref="OwnerCollaborationErrors.ContactOwnerOnly"/>.</exception>
    private static HashSet<(Guid OrgId, Guid PropertyOwnerId)> OwnProfiles(OwnerPortalAccess access)
    {
        var profiles = access.Grants.Where(g => g.ContactId is null).Select(g => (g.OrgId, g.PropertyOwnerId)).ToHashSet();
        if (profiles.Count == 0)
            throw new DomainForbiddenException(OwnerCollaborationErrors.ContactOwnerOnly, OwnerCollaborationErrors.ContactOwnerOnlyMessageKey);
        return profiles;
    }

    /// <summary>The owner profile of the agency named, or the only one; 422 when the agency is needed or is not one of the caller's.</summary>
    private static (Guid OrgId, Guid PropertyOwnerId) ProfileFor(IReadOnlyCollection<(Guid OrgId, Guid PropertyOwnerId)> profiles, Guid? agencyId)
    {
        var candidates = agencyId is { } wanted ? profiles.Where(p => p.OrgId == wanted).ToList() : profiles.ToList();
        if (candidates.Count != 1)
            throw new DomainRuleException(OwnerCollaborationErrors.ContactAgencyRequired, OwnerCollaborationErrors.ContactAgencyRequiredMessageKey);
        return candidates[0];
    }

    /// <summary>A contact of an owner profile the caller owns; another owner's, or a missing one, is the same 404.</summary>
    private async Task<OwnerPortalContact> OwnContactAsync(OwnerPortalAccess access, Guid contactId, CancellationToken cancellationToken)
    {
        var profiles = OwnProfiles(access);
        var contact = await Contacts().FirstOrDefaultAsync(c => c.Id == contactId, cancellationToken);
        if (contact is null || !profiles.Contains((contact.OrgId, contact.PropertyOwnerId)))
            throw ContactNotFound(contactId);
        return contact;
    }

    private async Task<OwnerContactView> RevokeAsync(OwnerPortalContact contact, string actor, CancellationToken cancellationToken)
    {
        if (contact.Status != OwnerPortalContactStatus.Revoked)
        {
            Close(contact, Now(), actor);
            await db.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Owner contact {ContactId} withdrawn", contact.Id);
        }

        return ContactView(contact);
    }

    /// <summary>Withdraws a contact (staged, with its line of the activity log): it grants nothing from the next request on.</summary>
    private void Close(OwnerPortalContact contact, DateTime now, string? actor)
    {
        contact.Status = OwnerPortalContactStatus.Revoked;
        contact.RevokedAt = now;
        contact.RevokedBy = actor;
        contact.UpdatedAt = now;
        Audit(contact.OrgId, contact.Id, OwnerPortalParts.Contact, OwnerPortalAuditStatuses.Revoked, actor);
    }

    private OwnerContactView ContactView(OwnerPortalContact contact)
    {
        var status = contact.Status switch
        {
            OwnerPortalContactStatus.Active => "active",
            OwnerPortalContactStatus.Revoked => "revoked",
            _ => contact.InvitationExpiresAt <= Now() ? "expired" : "invited",
        };
        return new OwnerContactView(
            contact.Id,
            contact.OrgId,
            contact.PropertyOwnerId,
            contact.Email,
            contact.FirstName,
            contact.LastName,
            OwnerPortalPermissions.CodesOf(contact.Permissions),
            status,
            contact.CreatedAt,
            contact.InvitationExpiresAt,
            contact.AcceptedAt,
            contact.RevokedAt);
    }

    private static string HashToken(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static string Cut(string value) => value.Length <= OwnerPortalContact.NameMaxLength ? value : value[..OwnerPortalContact.NameMaxLength];

    private static void RequireContact(bool valid, string field)
    {
        if (!valid)
            throw new DomainRuleException(OwnerCollaborationErrors.ContactInvalid, OwnerCollaborationErrors.ContactInvalidMessageKey, field);
    }

    private static DomainGoneException Gone(string code, string messageKey) => new(code, messageKey);

    private static NotFoundException ContactNotFound(Guid contactId) =>
        new($"Owner contact {contactId} not found")
        {
            Code = OwnerCollaborationErrors.ContactNotFound,
            MessageKey = OwnerCollaborationErrors.ContactNotFoundMessageKey,
        };
}
