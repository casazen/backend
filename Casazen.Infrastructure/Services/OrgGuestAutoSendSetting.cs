using Casazen.Core.Entities;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IOrgGuestAutoSendSetting" />
/// <remarks>
/// The org is the one named, bound in SQL: the setting of another org is never read or written by what the caller says about a stay. A handler or a
/// job has no request org, so the id of the org that owns the stay is what scopes the read. Logs carry the org and the Auth0 subject, never a name.
/// </remarks>
public sealed class OrgGuestAutoSendSetting(
    AppDbContext db,
    ILogger<OrgGuestAutoSendSetting> logger,
    TimeProvider? timeProvider = null) : IOrgGuestAutoSendSetting
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<bool> GetAsync(Guid orgId, CancellationToken ct) =>
        orgId != Guid.Empty
        && await db.Orgs
            .AsNoTracking()
            .Where(o => o.Id == orgId)
            .Select(o => o.AutoSendGuestsToOctorate)
            .FirstOrDefaultAsync(ct);

    public async Task<bool> SetAsync(Guid orgId, bool enabled, string userId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        if (userId.Length > Org.AlloggiatiDelegationUserIdMaxLength)
            throw new ArgumentException("The user id of a change is an id of at most 255 characters.", nameof(userId));

        var org = await db.Orgs.FirstOrDefaultAsync(o => o.Id == orgId, ct)
                  ?? throw new NotFoundException($"Org {orgId} not found.") { Code = "org_not_found", MessageKey = "OrganizationNotFound" };
        if (org.AutoSendGuestsToOctorate == enabled)
            return false;

        org.AutoSendGuestsToOctorate = enabled;
        org.AutoSendGuestsToOctorateChangedAt = UtcDateTime.TruncateToMicroseconds(_clock.GetUtcNow().UtcDateTime);
        org.AutoSendGuestsToOctorateChangedByUserId = userId;
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Org {OrgId}: the guests written to the channel manager on their own set to {Enabled} by {UserId}", orgId, enabled, userId);
        return true;
    }
}
