using System.Text.RegularExpressions;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.OrgTeam;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IAlloggiatiDelegationService" />
/// <remarks>
/// The delegation lives on the <see cref="Org"/> row (the <c>AlloggiatiDelegation*</c> columns). The activity line is staged in the unit of
/// work of the change (<see cref="IActivityLog.Record"/>) and saved with it, or not at all. The line holds the org as its subject, the
/// account as its actor and, for a grant, the version when it is a short code of the log: never a name, an e-mail or the text.
/// </remarks>
public sealed partial class AlloggiatiDelegationService(
    AppDbContext db,
    IActivityLog activityLog,
    TimeProvider? timeProvider = null) : IAlloggiatiDelegationService
{
    /// <summary>The error code of a version of the delegation text that is not a code of at most 50 characters.</summary>
    public const string VersionInvalidCode = "alloggiati_delegation_version_invalid";

    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<AlloggiatiDelegationInfo> GetAsync(Guid orgId, CancellationToken ct = default)
    {
        var org = await db.Orgs.AsNoTracking().FirstOrDefaultAsync(o => o.Id == orgId, ct) ?? throw OrgNotFound(orgId);
        return Info(org);
    }

    public async Task<AlloggiatiDelegationInfo> GrantAsync(Guid orgId, string userId, string textVersion, CancellationToken ct = default)
    {
        RequireUser(userId);
        var version = Version(textVersion);
        var org = await db.Orgs.FirstOrDefaultAsync(o => o.Id == orgId, ct) ?? throw OrgNotFound(orgId);
        if (IsValid(org) && string.Equals(org.AlloggiatiDelegationTextVersion, version, StringComparison.Ordinal))
            return Info(org); // The version already granted, once more: nothing changes, nothing is recorded.

        // A new grant, or a new version of the text: who, when and which version; a revocation before it is over.
        var now = Now();
        org.AlloggiatiDelegationGrantedAt = now;
        org.AlloggiatiDelegationGrantedByUserId = userId;
        org.AlloggiatiDelegationTextVersion = version;
        org.AlloggiatiDelegationRevokedAt = null;
        org.AlloggiatiDelegationRevokedByUserId = null;
        org.UpdatedAt = now;
        activityLog.Record(OrgActivityCatalog.IsSafeDetailValue(version)
            ? OrgActivity.Of(org.Id, OrgActivityType.AlloggiatiDelegationGranted, userId, org.Id.ToString(), (OrgActivityDetailKeys.DelegationVersion, version))
            : OrgActivity.Of(org.Id, OrgActivityType.AlloggiatiDelegationGranted, userId, org.Id.ToString()));
        await db.SaveChangesAsync(ct);
        return Info(org);
    }

    public async Task<AlloggiatiDelegationInfo> RevokeAsync(Guid orgId, string userId, CancellationToken ct = default)
    {
        RequireUser(userId);
        var org = await db.Orgs.FirstOrDefaultAsync(o => o.Id == orgId, ct) ?? throw OrgNotFound(orgId);
        if (!IsValid(org))
            return Info(org); // Never granted, or already revoked: nothing changes, nothing is recorded.

        // The grant stays as it was (who, when, which version): the revocation is added to it.
        var now = Now();
        org.AlloggiatiDelegationRevokedAt = now;
        org.AlloggiatiDelegationRevokedByUserId = userId;
        org.UpdatedAt = now;
        activityLog.Record(OrgActivity.Of(org.Id, OrgActivityType.AlloggiatiDelegationRevoked, userId, org.Id.ToString()));
        await db.SaveChangesAsync(ct);
        return Info(org);
    }

    /// <summary>Granted and not revoked.</summary>
    private static bool IsValid(Org org) =>
        org.AlloggiatiDelegationGrantedAt is not null && org.AlloggiatiDelegationRevokedAt is null;

    private static AlloggiatiDelegationInfo Info(Org org) =>
        new(
            IsValid(org),
            org.AlloggiatiDelegationGrantedAt,
            org.AlloggiatiDelegationGrantedByUserId,
            org.AlloggiatiDelegationTextVersion,
            org.AlloggiatiDelegationRevokedAt);

    /// <summary>The version of the delegation text, trimmed: a code of at most 50 characters, or a 422.</summary>
    private static string Version(string? textVersion)
    {
        var version = textVersion?.Trim();
        if (string.IsNullOrEmpty(version) || !VersionPattern().IsMatch(version))
            throw new DomainRuleException(VersionInvalidCode, "AlloggiatiDelegationVersionInvalid");

        return version;
    }

    /// <summary>The account that acts: an id the web layer gives (the Auth0 subject), never empty, at most 255 characters.</summary>
    private static void RequireUser(string userId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        if (userId.Length > Org.AlloggiatiDelegationUserIdMaxLength)
            throw new ArgumentException("The user id of a delegation is an id of at most 255 characters.", nameof(userId));
    }

    private static NotFoundException OrgNotFound(Guid orgId) =>
        new($"Org {orgId} not found.") { Code = "org_not_found", MessageKey = "OrganizationNotFound" };

    /// <summary>Now, cut to the microsecond PostgreSQL keeps, so what is written and what is read back compare equal.</summary>
    private DateTime Now() => UtcDateTime.TruncateToMicroseconds(_clock.GetUtcNow().UtcDateTime);

    /// <summary>
    /// A code of the legal documents (<see cref="Org.AlloggiatiDelegationTextVersionMaxLength"/> = 50 characters at most): a letter or
    /// a digit, then letters, digits, <c>.</c>, <c>_</c> or <c>-</c>. No space, no <c>@</c>, no sentence.
    /// </summary>
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,49}$")]
    private static partial Regex VersionPattern();
}
