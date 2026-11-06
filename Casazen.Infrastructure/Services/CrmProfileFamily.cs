using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// The profiles that belong together after the merges of duplicates (E07/F01/US03/T02): the living profile (the survivor) and every tombstone that
/// leads to it, chains of merges included. A merge keeps the profile that goes as a tombstone (<c>MergedIntoProfileId</c>) with all it held, and moves
/// its guests and its contacts to the survivor; the rights of a guest (export, erasure, retention: E07/F06/US02/T01) cover the whole family, or a
/// tombstone would keep the tags, the country, the language and the keyed hashes of a guest that was erased.
/// </summary>
internal static class CrmProfileFamily
{
    /// <summary>The most hops followed from a profile to its survivor, as the matching and the merge service follow them.</summary>
    internal const int MaxMergeHops = 10;

    /// <summary>The deepest level of tombstones read below a survivor: a bound against a loop, far beyond any chain of merges.</summary>
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
        // Up: the living profile at the end of the merges that start at the profile of the guest.
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

        // Down: every tombstone that points to the survivor, and every tombstone that points to one of them.
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
