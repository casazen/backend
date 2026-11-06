using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Options;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.Octorate;

/// <summary>
/// The OAuth <c>state</c> of the consent (section 9.1, E02/F01/US02/T01): 256 random bits, valid for
/// <see cref="OctorateOptions.StateLifetimeMinutes"/>, bound to the org, the user and the redirect address, used once. Only its SHA-256 is
/// stored (<see cref="OctorateOAuthState.StateHash"/>); the plain value is returned once and never logged.
/// </summary>
/// <remarks>
/// Every refusal of <see cref="ConsumeAsync"/> is the same <see cref="OctorateOAuthStateInvalidException"/>: unknown, used, expired, of
/// another org, user or redirect address. The binding is compared in constant time, and the row is deleted in the statement that
/// consumes it, so two requests with the same state never both pass.
/// </remarks>
public sealed class OctorateOAuthStateService(
    AppDbContext db,
    IOptions<OctorateOptions> options,
    IHostEnvironment hostEnvironment,
    TimeProvider timeProvider,
    ILogger<OctorateOAuthStateService> logger)
{
    /// <summary>Random bytes of a state (256 bits).</summary>
    public const int StateBytes = 32;

    /// <summary>A longer state is refused without being looked up.</summary>
    private const int MaxStateLength = 128;

    /// <summary>
    /// A new state for the consent of <paramref name="orgId"/> started by <paramref name="userId"/> with <paramref name="redirectUri"/>:
    /// its hash is stored with its expiry, the plain value is returned once. The expired states of every org are deleted first.
    /// </summary>
    public async Task<string> CreateAsync(Guid orgId, string userId, string redirectUri, CancellationToken ct)
    {
        if (orgId == Guid.Empty)
            throw new ArgumentException("A consent state belongs to an organization.", nameof(orgId));
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentException.ThrowIfNullOrWhiteSpace(redirectUri);
        if (userId.Length > OctorateOAuthState.UserIdMaxLength || redirectUri.Length > OctorateOAuthState.RedirectUriMaxLength)
            throw new ArgumentException("The user id or the redirect address is too long for a consent state.");

        var now = Now();
        await DeleteExpiredAsync(now, ct);

        var state = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(StateBytes));
        db.OctorateOAuthStates.Add(new OctorateOAuthState
        {
            OrgId = orgId,
            UserId = userId,
            StateHash = HashOf(state),
            RedirectUri = redirectUri,
            Environment = OctorateEnvironments.Of(hostEnvironment),
            ExpiresAt = now + options.Value.StateLifetime,
            CreatedAt = now,
        });
        await db.SaveChangesAsync(ct);
        return state;
    }

    /// <summary>
    /// Consumes <paramref name="state"/>: valid only before its expiry, only once, and only for the org, the user and the redirect
    /// address it was created for. Returns the Octorate environment the consent was started in.
    /// </summary>
    /// <exception cref="OctorateOAuthStateInvalidException">Any refusal, always the same.</exception>
    public async Task<OctorateEnvironment> ConsumeAsync(string state, Guid orgId, string userId, string redirectUri, CancellationToken ct)
    {
        var presented = state?.Trim();
        if (string.IsNullOrEmpty(presented) || presented.Length > MaxStateLength)
            throw new OctorateOAuthStateInvalidException();

        // A system read by the hash (unique), then the binding compared in memory, in constant time.
        var hash = HashOf(presented);
        var row = await db.OctorateOAuthStates
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.StateHash == hash, ct);
        if (row is null)
        {
            logger.LogInformation("Octorate consent state refused: unknown or already used");
            throw new OctorateOAuthStateInvalidException();
        }

        var now = Now();
        var bound = Same(row.OrgId.ToString("N"), orgId.ToString("N"))
                    & Same(row.UserId, userId ?? string.Empty)
                    & Same(row.RedirectUri, redirectUri ?? string.Empty);
        var alive = row.ExpiresAt > now;
        if (!bound || !alive)
        {
            logger.LogInformation("Octorate consent state refused (bound {Bound}, before expiry {Alive})", bound, alive);
            throw new OctorateOAuthStateInvalidException();
        }

        // Single use: deleted before the code is used; a parallel request with the same state finds nothing to delete.
        if (!await DeleteAsync(row, now, ct))
        {
            logger.LogInformation("Octorate consent state refused: consumed by another request meanwhile");
            throw new OctorateOAuthStateInvalidException();
        }

        return row.Environment;
    }

    /// <summary>The SHA-256 of a state, lowercase hex: what the table holds.</summary>
    public static string HashOf(string state) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(state)));

    private async Task<bool> DeleteAsync(OctorateOAuthState row, DateTime now, CancellationToken ct)
    {
        if (db.Database.IsRelational())
        {
            // One statement: the row goes only if it is still there and still valid.
            var deleted = await db.OctorateOAuthStates
                .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
                .Where(s => s.Id == row.Id && s.ExpiresAt > now)
                .ExecuteDeleteAsync(ct);
            return deleted == 1;
        }

        // EF InMemory (unit tests): the tracked delete fails when the row is already gone.
        var entity = db.OctorateOAuthStates.Local.FirstOrDefault(s => s.Id == row.Id) ?? row;
        db.OctorateOAuthStates.Remove(entity);
        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            db.Entry(entity).State = EntityState.Detached;
            return false;
        }
    }

    private async Task DeleteExpiredAsync(DateTime now, CancellationToken ct)
    {
        // The expired states of every org: garbage nobody can use any more.
        var expired = db.OctorateOAuthStates
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .Where(s => s.ExpiresAt <= now);
        if (db.Database.IsRelational())
            await expired.ExecuteDeleteAsync(ct);
        else
            db.OctorateOAuthStates.RemoveRange(await expired.ToListAsync(ct));
    }

    private static bool Same(string stored, string presented) =>
        CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(stored)),
            SHA256.HashData(Encoding.UTF8.GetBytes(presented)));

    private DateTime Now() => timeProvider.GetUtcNow().UtcDateTime;
}
