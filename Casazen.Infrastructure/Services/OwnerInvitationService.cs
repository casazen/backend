using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Email.Templates;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// Owner invitation service implementation (ADR-011).
/// Generates tokens, enforces rate limits, sends emails, links Auth0 users to OwnerUser.
/// </summary>
public class OwnerInvitationService(
    AppDbContext dbContext,
    IEmailQueue emailQueue,
    ILogger<OwnerInvitationService> logger) : IOwnerInvitationService
{
    private const int MaxInvitationsPerDay = 100;
    private const int InvitationValidityDays = 7;

    public async Task<Guid> CreateInvitationAsync(
        Guid orgId,
        Guid propertyOwnerId,
        string actorUserId,
        CancellationToken cancellationToken = default)
    {
        // Verify PropertyOwner exists and belongs to this org, and fetch org for email template
        var propertyOwner = await dbContext.PropertyOwners
            .Include(po => po.Org)
            .FirstOrDefaultAsync(po => po.Id == propertyOwnerId && po.OrgId == orgId, cancellationToken);

        if (propertyOwner == null)
        {
            throw new NotFoundException("Property owner not found or does not belong to this organization")
            {
                Code = OwnerInvitationErrors.PropertyOwnerNotFound,
                MessageKey = "PropertyOwnerNotFound"
            };
        }

        // Check rate limit: count invitations sent in the last 24 hours across all invitations in this org
        var yesterday = DateTime.UtcNow.AddDays(-1);
        var allOrgInvitations = await dbContext.OwnerInvitations
            .Where(i => i.OrgId == orgId)
            .ToListAsync(cancellationToken);

        var recentInvitationCount = allOrgInvitations
            .SelectMany(i => i.SendLog)
            .Count(d => d > yesterday);

        if (recentInvitationCount >= MaxInvitationsPerDay)
        {
            throw new DomainTooManyRequestsException(
                OwnerInvitationErrors.DailyLimitReached,
                "OwnerInvitationDailyLimitReached",
                MaxInvitationsPerDay);
        }

        // Generate token and hash
        var token = GenerateToken();
        var tokenHash = HashToken(token);

        // Create invitation
        var invitation = new OwnerInvitation
        {
            OrgId = orgId,
            PropertyOwnerId = propertyOwnerId,
            Email = propertyOwner.Email.Trim().ToLowerInvariant(),
            TokenHash = tokenHash,
            Status = OwnerInvitationStatus.Pending,
            ExpiresAt = DateTime.UtcNow.AddDays(InvitationValidityDays),
            SendLog = [DateTime.UtcNow],
            InvitedByUserId = actorUserId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        dbContext.OwnerInvitations.Add(invitation);
        await dbContext.SaveChangesAsync(cancellationToken);

        // Queue invitation email using template
        var culture = new CultureInfo("it-IT");
        var ownerName = $"{propertyOwner.FirstName} {propertyOwner.LastName}";
        var agencyName = propertyOwner.Org.Name;
        var inviteLink = $"https://app.casazen.com/portale-proprietari/invito?token={token}";

        var emailContent = EmailTemplates.OwnerInvitation(
            culture,
            ownerName,
            agencyName,
            inviteLink,
            invitation.ExpiresAt);

        emailQueue.Enqueue(propertyOwner.Email, emailContent, EmailTemplates.Names.OwnerInvitation);

        logger.LogInformation(
            "Owner invitation created for PropertyOwner {PropertyOwnerId} in org {OrgId}",
            propertyOwnerId, orgId);

        return invitation.Id;
    }

    public async Task<Guid> AcceptInvitationAsync(
        string token,
        string auth0Sub,
        string email,
        string firstName,
        string lastName,
        CancellationToken cancellationToken = default)
    {
        // Normalize token
        if (!TryNormalizeToken(token, out var normalizedToken))
        {
            throw new DomainGoneException(OwnerInvitationErrors.Invalid, "OwnerInvitationInvalid");
        }

        var tokenHash = HashToken(normalizedToken);
        var normalizedEmail = email.Trim().ToLowerInvariant();

        // Find invitation by hash
        var invitation = await dbContext.OwnerInvitations
            .Include(i => i.PropertyOwner)
            .FirstOrDefaultAsync(i => i.TokenHash == tokenHash, cancellationToken);

        if (invitation == null || !TokenMatches(invitation.TokenHash, normalizedToken))
        {
            throw new DomainGoneException(OwnerInvitationErrors.Invalid, "OwnerInvitationInvalid");
        }

        // Check invitation status
        if (invitation.Status == OwnerInvitationStatus.Accepted)
        {
            // Idempotent: if already accepted, return the existing OwnerUserId
            if (invitation.OwnerUserId.HasValue)
            {
                logger.LogInformation(
                    "Invitation {InvitationId} already accepted, returning existing OwnerUser {OwnerUserId}",
                    invitation.Id, invitation.OwnerUserId.Value);
                return invitation.OwnerUserId.Value;
            }

            throw new DomainGoneException(OwnerInvitationErrors.Used, "OwnerInvitationUsed");
        }

        if (invitation.Status == OwnerInvitationStatus.Revoked)
        {
            throw new DomainGoneException(OwnerInvitationErrors.Revoked, "OwnerInvitationRevoked");
        }

        if (invitation.Status == OwnerInvitationStatus.Expired || invitation.ExpiresAt < DateTime.UtcNow)
        {
            throw new DomainGoneException(OwnerInvitationErrors.Expired, "OwnerInvitationExpired");
        }

        // Verify email matches
        if (invitation.Email != normalizedEmail)
        {
            throw new DomainForbiddenException(OwnerInvitationErrors.EmailMismatch, "OwnerInvitationEmailMismatch");
        }

        // Find or create OwnerUser
        var ownerUser = await dbContext.OwnerUsers
            .FirstOrDefaultAsync(ou => ou.Auth0Sub == auth0Sub, cancellationToken);

        if (ownerUser == null)
        {
            ownerUser = new OwnerUser
            {
                Auth0Sub = auth0Sub,
                Email = normalizedEmail,
                FirstName = firstName,
                LastName = lastName,
                CreatedAt = DateTime.UtcNow
            };
            dbContext.OwnerUsers.Add(ownerUser);
        }

        ownerUser.LastLoginAt = DateTime.UtcNow;

        // Update invitation
        invitation.Status = OwnerInvitationStatus.Accepted;
        invitation.AcceptedAt = DateTime.UtcNow;
        invitation.OwnerUserId = ownerUser.Id;
        invitation.ClosedAt = DateTime.UtcNow;
        invitation.UpdatedAt = DateTime.UtcNow;

        await dbContext.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Invitation {InvitationId} accepted by OwnerUser {OwnerUserId} for PropertyOwner {PropertyOwnerId}",
            invitation.Id, ownerUser.Id, invitation.PropertyOwnerId);

        return ownerUser.Id;
    }

    private static string GenerateToken()
    {
        return Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
    }

    private static string HashToken(string token)
    {
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    }

    private static bool TryNormalizeToken(string? value, out string token)
    {
        token = value?.Trim().ToLowerInvariant() ?? string.Empty;
        return token.Length == 64 && token.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'));
    }

    private static bool TokenMatches(string storedHash, string token)
    {
        if (string.IsNullOrEmpty(storedHash))
            return false;

        var computedHash = HashToken(token);
        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(storedHash),
            Encoding.ASCII.GetBytes(computedHash));
    }
}
