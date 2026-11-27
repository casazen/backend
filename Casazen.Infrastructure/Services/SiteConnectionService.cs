using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Core.SiteConnections;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// <see cref="ISiteConnectionService"/> (S022, PLG-BE-CONNECT, site integration plan §10.3-§10.4): codes and credentials of 256 random bits, stored as their
/// SHA-256 only (<see cref="SiteConnectionTokens"/>), the rules of the request in <see cref="SiteConnectionRules"/>.
/// </summary>
/// <remarks>
/// <para><b>Tenant.</b> The console calls (<see cref="AuthorizeAsync"/>, <see cref="ListAsync"/>, the org's <see cref="RevokeAsync(Guid, Guid, CancellationToken)"/>)
/// run for a signed-in billing administrator: the tenant filter applies and every query names the org as well. The exchange and the check of a credential run
/// with no signed-in user and before the tenant of the request is known: they read with <c>IgnoreQueryFilters([TenantQueryFilter])</c> and an explicit
/// predicate (the unique hash of the secret, then the org of the row that came back), never a global bypass.</para>
/// <para><b>Atomic steps.</b> On a relational database the code is spent with one conditional update (<c>UsedAt IS NULL</c> in its condition), the previous
/// connections of the site are revoked in the transaction that creates the new one, and the time of use is written with one conditional update; the in-memory
/// database of the unit tests, which has no such statements, does the same with the tracked entities (one request at a time).</para>
/// <para><b>Logs</b> carry the org, the connection and the host of the site; never a code, a verifier, a challenge, a credential, an account or an address
/// with its query.</para>
/// </remarks>
public sealed class SiteConnectionService(
    AppDbContext db,
    TimeProvider clock,
    ILogger<SiteConnectionService> logger) : ISiteConnectionService
{
    /// <inheritdoc />
    public async Task<SiteConnectionAuthorization?> AuthorizeAsync(
        Guid orgId,
        string userId,
        string? siteUrl,
        string? returnUrl,
        string? state,
        string? codeChallenge,
        string? codeChallengeMethod,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        if (!SiteConnectionRules.TryNormalizeSiteUrl(siteUrl, out var site)
            || !SiteConnectionRules.TryParseReturnUrl(returnUrl, site, out var returnUri)
            || !SiteConnectionRules.IsValidState(state)
            || !SiteConnectionRules.IsValidCodeChallenge(codeChallenge)
            || !SiteConnectionRules.IsS256(codeChallengeMethod))
        {
            // Nothing of the request in the log: the addresses may carry a query, the state and the challenge are the plugin's.
            logger.LogInformation("Site connection request of org {OrgId} refused: it breaks a rule of the request", orgId);
            return null;
        }

        var now = UtcNow();
        await DeleteLongExpiredCodesAsync(now, cancellationToken);

        var code = SiteConnectionTokens.NewCode();
        var row = new SiteConnectionCode
        {
            OrgId = orgId,
            CreatedByUserId = userId,
            SiteUrl = site,
            CodeHash = SiteConnectionTokens.Hash(code),
            CodeChallenge = codeChallenge,
            ExpiresAt = now + SiteConnectionRules.CodeLifetime,
            CreatedAt = now,
        };
        db.SiteConnectionCodes.Add(row);
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Site connection authorized for org {OrgId} on site host {SiteHost}: a one-time code until {ExpiresAt}",
            orgId,
            SiteConnectionRules.HostOf(site),
            row.ExpiresAt);
        return new SiteConnectionAuthorization(SiteConnectionRules.RedirectUrl(returnUri, code, state), site, row.ExpiresAt);
    }

    /// <inheritdoc />
    public async Task<SiteConnectionGrant?> ExchangeAsync(
        string? code,
        string? codeVerifier,
        string? siteUrl,
        CancellationToken cancellationToken = default)
    {
        if (!SiteConnectionTokens.IsCode(code)
            || !SiteConnectionRules.IsValidCodeVerifier(codeVerifier)
            || !SiteConnectionRules.TryNormalizeSiteUrl(siteUrl, out var site))
        {
            return Refused(orgId: null, "malformed");
        }

        var now = UtcNow();
        var codeHash = SiteConnectionTokens.Hash(code);
        var proof = SiteConnectionTokens.S256(codeVerifier);

        // One atomic step first: the code is spent whatever follows, so of two requests with one code only one goes on, and a wrong verifier burns it.
        if (!await SpendCodeAsync(codeHash, now, cancellationToken))
            return Refused(orgId: null, "unknown or already used");

        var spent = await AllCodes().AsNoTracking().FirstOrDefaultAsync(c => c.CodeHash == codeHash, cancellationToken);
        if (spent is null)
            return Refused(orgId: null, "gone");

        if (spent.ExpiresAt <= now)
            return Refused(spent.OrgId, "expired");

        if (!string.Equals(spent.SiteUrl, site, StringComparison.Ordinal))
            return Refused(spent.OrgId, "another site");

        if (!SiteConnectionTokens.ProofMatches(spent.CodeChallenge, proof))
            return Refused(spent.OrgId, "wrong verifier");

        var org = await db.Orgs.AsNoTracking()
            .Where(o => o.Id == spent.OrgId && o.IsActive)
            .Select(o => new { o.Id, o.Slug, o.DisplayName, o.Name })
            .FirstOrDefaultAsync(cancellationToken);
        if (org is null)
            return Refused(spent.OrgId, "org not active");

        var credential = SiteConnectionTokens.NewCredential();
        var (connection, revoked) = await ReplaceConnectionAsync(spent, SiteConnectionTokens.Hash(credential), now, cancellationToken);
        logger.LogInformation(
            "Site connection {ConnectionId} created for org {OrgId} on site host {SiteHost}; {Revoked} earlier connection(s) of the site revoked",
            connection.Id,
            org.Id,
            SiteConnectionRules.HostOf(spent.SiteUrl),
            revoked);

        var displayName = string.IsNullOrWhiteSpace(org.DisplayName) ? org.Name : org.DisplayName;
        return new SiteConnectionGrant(credential, connection.Id, new SiteConnectionOrg(org.Id, org.Slug, displayName));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SiteConnectionSummary>> ListAsync(Guid orgId, CancellationToken cancellationToken = default) =>
        await db.SiteConnections.AsNoTracking()
            .Where(c => c.OrgId == orgId && c.RevokedAt == null)
            .OrderByDescending(c => c.CreatedAt)
            .Select(c => new SiteConnectionSummary(c.Id, c.SiteUrl, c.CreatedAt, c.LastUsedAt))
            .ToListAsync(cancellationToken);

    /// <inheritdoc />
    public async Task<bool> RevokeAsync(Guid orgId, Guid connectionId, CancellationToken cancellationToken = default)
    {
        var connection = await db.SiteConnections.FirstOrDefaultAsync(c => c.Id == connectionId && c.OrgId == orgId, cancellationToken);
        if (connection is null)
            return false;

        await MarkRevokedAsync(connection, "console", cancellationToken);
        return true;
    }

    /// <inheritdoc />
    public async Task<bool> RevokeAsync(Guid connectionId, CancellationToken cancellationToken = default)
    {
        // The id comes from the credential of the request, whose org is the tenant of the request (the scheme SiteToken sets it).
        var connection = await db.SiteConnections.FirstOrDefaultAsync(c => c.Id == connectionId, cancellationToken);
        if (connection is null)
            return false;

        await MarkRevokedAsync(connection, "plugin", cancellationToken);
        return true;
    }

    /// <inheritdoc />
    public async Task<SiteConnectionPrincipal?> AuthenticateAsync(string? credential, CancellationToken cancellationToken = default)
    {
        if (!SiteConnectionTokens.IsCredential(credential))
            return null;

        // No tenant yet: the credential is what names the org. Only an active connection of an active org opens.
        var tokenHash = SiteConnectionTokens.Hash(credential);
        var found = await AllConnections().AsNoTracking()
            .Where(c => c.TokenHash == tokenHash && c.RevokedAt == null && db.Orgs.Any(o => o.Id == c.OrgId && o.IsActive))
            .Select(c => new { c.Id, c.OrgId, c.TokenHash, c.LastUsedAt })
            .FirstOrDefaultAsync(cancellationToken);
        if (found is null || !SiteConnectionTokens.HashMatches(found.TokenHash, credential))
            return null;

        var now = UtcNow();
        if (found.LastUsedAt is not { } lastUsed || now - lastUsed >= SiteConnectionRules.LastUsedResolution)
            await RecordUseAsync(found.Id, now, cancellationToken);

        return new SiteConnectionPrincipal(found.Id, found.OrgId);
    }

    /// <summary>Spends the code with the hash <paramref name="codeHash"/>: true for the one request that did, false when it is unknown or was spent already.</summary>
    private async Task<bool> SpendCodeAsync(string codeHash, DateTime now, CancellationToken cancellationToken)
    {
        if (db.Database.IsRelational())
        {
            var spent = await AllCodes()
                .Where(c => c.CodeHash == codeHash && c.UsedAt == null)
                .ExecuteUpdateAsync(setters => setters.SetProperty(c => c.UsedAt, (DateTime?)now), cancellationToken);
            return spent == 1;
        }

        var code = await AllCodes().FirstOrDefaultAsync(c => c.CodeHash == codeHash && c.UsedAt == null, cancellationToken);
        if (code is null)
            return false;

        code.UsedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>
    /// Creates the connection of the spent code and revokes the active ones of the same org and site: the newest wins. Two connections of one site at the
    /// same moment race on the filtered unique index; the loser tries once more, which revokes the winner.
    /// </summary>
    private async Task<(SiteConnection Connection, int Revoked)> ReplaceConnectionAsync(
        SiteConnectionCode spent,
        string tokenHash,
        DateTime now,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            var connection = new SiteConnection
            {
                OrgId = spent.OrgId,
                SiteUrl = spent.SiteUrl,
                TokenHash = tokenHash,
                CreatedByUserId = spent.CreatedByUserId,
                CreatedAt = now,
            };

            try
            {
                return (connection, await SaveReplacingAsync(connection, now, cancellationToken));
            }
            catch (DbUpdateException exception) when (attempt == 1 && IsActiveSiteConflict(exception))
            {
                db.Entry(connection).State = EntityState.Detached;
                logger.LogInformation(
                    "Site connection of org {OrgId} on site host {SiteHost} met another one of the same site at the same moment: trying once more",
                    spent.OrgId,
                    SiteConnectionRules.HostOf(spent.SiteUrl));
            }
        }
    }

    /// <summary>Revokes the active connections of the org and site of <paramref name="connection"/> and adds it, in one transaction; the number revoked.</summary>
    private async Task<int> SaveReplacingAsync(SiteConnection connection, DateTime now, CancellationToken cancellationToken)
    {
        var active = AllConnections()
            .Where(c => c.OrgId == connection.OrgId && c.SiteUrl == connection.SiteUrl && c.RevokedAt == null);

        if (!db.Database.IsRelational())
        {
            var previous = await active.ToListAsync(cancellationToken);
            foreach (var row in previous)
                row.RevokedAt = now;

            db.SiteConnections.Add(connection);
            await db.SaveChangesAsync(cancellationToken);
            return previous.Count;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var revoked = await active.ExecuteUpdateAsync(setters => setters.SetProperty(c => c.RevokedAt, (DateTime?)now), cancellationToken);
        db.SiteConnections.Add(connection);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return revoked;
    }

    /// <summary>Writes the time of use of a credential, unless another request wrote it in the last <see cref="SiteConnectionRules.LastUsedResolution"/>.</summary>
    private async Task RecordUseAsync(Guid connectionId, DateTime now, CancellationToken cancellationToken)
    {
        var staleBefore = now - SiteConnectionRules.LastUsedResolution;
        if (db.Database.IsRelational())
        {
            await AllConnections()
                .Where(c => c.Id == connectionId && (c.LastUsedAt == null || c.LastUsedAt <= staleBefore))
                .ExecuteUpdateAsync(setters => setters.SetProperty(c => c.LastUsedAt, (DateTime?)now), cancellationToken);
            return;
        }

        var connection = await AllConnections().FirstOrDefaultAsync(c => c.Id == connectionId, cancellationToken);
        if (connection is null)
            return;

        connection.LastUsedAt = now;
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task MarkRevokedAsync(SiteConnection connection, string revokedBy, CancellationToken cancellationToken)
    {
        if (connection.RevokedAt is not null)
            return;

        connection.RevokedAt = UtcNow();
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation(
            "Site connection {ConnectionId} of org {OrgId} on site host {SiteHost} revoked by the {RevokedBy}",
            connection.Id,
            connection.OrgId,
            SiteConnectionRules.HostOf(connection.SiteUrl),
            revokedBy);
    }

    /// <summary>
    /// Deletes the codes of every org expired for more than <see cref="SiteConnectionRules.ExpiredCodeRetention"/>: such a code opens nothing and holds no
    /// personal data, and there is no job to do it. On the in-memory database the rows go with the next save.
    /// </summary>
    private async Task DeleteLongExpiredCodesAsync(DateTime now, CancellationToken cancellationToken)
    {
        var cutoff = now - SiteConnectionRules.ExpiredCodeRetention;
        var expired = AllCodes().Where(c => c.ExpiresAt < cutoff);
        if (db.Database.IsRelational())
        {
            await expired.ExecuteDeleteAsync(cancellationToken);
            return;
        }

        db.SiteConnectionCodes.RemoveRange(await expired.ToListAsync(cancellationToken));
    }

    private SiteConnectionGrant? Refused(Guid? orgId, string reason)
    {
        // The reason stays in the log; the caller gets one answer whatever it is.
        logger.LogInformation("Site connection exchange refused ({Reason}) for org {OrgId}", reason, orgId);
        return null;
    }

    private IQueryable<SiteConnectionCode> AllCodes() => db.SiteConnectionCodes.IgnoreQueryFilters([AppDbContext.TenantQueryFilter]);

    private IQueryable<SiteConnection> AllConnections() => db.SiteConnections.IgnoreQueryFilters([AppDbContext.TenantQueryFilter]);

    private static bool IsActiveSiteConflict(DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } postgres
        && string.Equals(postgres.ConstraintName, SiteConnection.ActiveSiteIndexName, StringComparison.Ordinal);

    private DateTime UtcNow() => clock.GetUtcNow().UtcDateTime;
}
