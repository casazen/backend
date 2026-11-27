using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Services;

internal static class CrmProfileFamily
{
    internal const int MaxMergeHops = 10;

    internal const int MaxDepth = 50;

    /// <summary>
    /// The family of <paramref name="profileId"/>, the profile a guest is linked to (a link kept from before a merge may still point to a tombstone):
    /// the survivor at the end of the merges that start there, and the ids of every profile of the org that leads to it, the survivor first. The profile
    /// asked for is always in the list, even when the chain is broken (a pointer to a profile that does not exist) or loops.
    /// </summary>
    internal static async Task<(Guid SurvivorId, List<Guid> ProfileIds)> OfAsync(
        AppDbContext db,
        Guid orgId,
        Guid profileId,
        CancellationToken cancellationToken)
    {
        var survivorId = profileId;
        var visited = new HashSet<Guid> { profileId };
        for (var hop = 0; hop < MaxMergeHops; hop++)
        {
            var next = await db.GuestCrmProfiles
                .AsNoTracking()
                .Where(p => p.OrgId == orgId && p.Id == survivorId)
                .Select(p => p.MergedIntoProfileId)
                .FirstOrDefaultAsync(cancellationToken);
            if (next is not { } target || !visited.Add(target))
                break;

            survivorId = target;
        }

        var ids = new List<Guid> { survivorId };
        var seen = new HashSet<Guid> { survivorId };
        var level = new List<Guid> { survivorId };
        for (var depth = 0; depth < MaxDepth && level.Count > 0; depth++)
        {
            var below = await db.GuestCrmProfiles
                .AsNoTracking()
                .Where(p => p.OrgId == orgId && p.MergedIntoProfileId != null && level.Contains(p.MergedIntoProfileId.Value))
                .Select(p => p.Id)
                .ToListAsync(cancellationToken);
            level = below.Where(seen.Add).ToList();
            ids.AddRange(level);
        }

        if (seen.Add(profileId))
            ids.Add(profileId);

        return (survivorId, ids);
    }
}
