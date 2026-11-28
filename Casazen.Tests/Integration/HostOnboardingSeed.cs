using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Tests.Integration;

public static class HostOnboardingSeed
{
    public static IReadOnlyList<ConsentRecord> CurrentConsents(string userId, Guid orgId, ILegalDocumentService legal) =>
    [
        new() { UserId = userId, OrgId = orgId, Type = ConsentType.Tos, Version = legal.GetTos().Version },
        new() { UserId = userId, OrgId = orgId, Type = ConsentType.Privacy, Version = legal.GetPrivacy().Version },
        new() { UserId = userId, OrgId = orgId, Type = ConsentType.Dpa, Version = legal.GetDpa().Version },
        new() { UserId = userId, OrgId = orgId, Type = ConsentType.SubprocessorsAck, Version = legal.GetSubprocessors().Version },
    ];

    public static async Task MarkOnboardedAsync(AppDbContext db, User user, Guid orgId, ILegalDocumentService legal)
    {
        user.OnboardingCompletedAt ??= DateTime.UtcNow;

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
