using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Tests.Integration;

/// <summary>
/// Test data of a host who went through the onboarding (PL-02): <c>OnboardingCompletedAt</c> set and the Terms, Privacy
/// notice and DPA of the current versions (<see cref="ILegalDocumentService"/>) recorded for the org. Without it the host
/// contexts are withheld and the host endpoints answer 403 <c>onboarding_required</c>.
/// </summary>
public static class HostOnboardingSeed
{
    /// <summary>The consent rows the onboarding records for <paramref name="orgId"/>, at the current versions.</summary>
    public static IReadOnlyList<ConsentRecord> CurrentConsents(string userId, Guid orgId, ILegalDocumentService legal) =>
    [
        new() { UserId = userId, OrgId = orgId, Type = ConsentType.Tos, Version = legal.GetTos().Version },
        new() { UserId = userId, OrgId = orgId, Type = ConsentType.Privacy, Version = legal.GetPrivacy().Version },
        new() { UserId = userId, OrgId = orgId, Type = ConsentType.Dpa, Version = legal.GetDpa().Version },
        new() { UserId = userId, OrgId = orgId, Type = ConsentType.SubprocessorsAck, Version = legal.GetSubprocessors().Version },
    ];

    /// <summary>
    /// Marks <paramref name="user"/> (tracked or added to <paramref name="db"/>) as onboarded and adds the current
    /// consents for <paramref name="orgId"/> that are missing. The caller saves.
    /// </summary>
    public static async Task MarkOnboardedAsync(AppDbContext db, User user, Guid orgId, ILegalDocumentService legal)
    {
        user.OnboardingCompletedAt ??= DateTime.UtcNow;

        // IgnoreQueryFilters: seeding runs outside a request, the tenant filter would hide every consent row.
        var existing = await db.ConsentRecords.IgnoreQueryFilters()
            .Where(c => c.UserId == user.Id && c.OrgId == orgId)
            .Select(c => new { c.Type, c.Version })
            .ToListAsync();

        foreach (var consent in CurrentConsents(user.Id, orgId, legal))
        {
            if (!existing.Any(e => e.Type == consent.Type && e.Version == consent.Version))
                db.ConsentRecords.Add(consent);
        }
    }
}
