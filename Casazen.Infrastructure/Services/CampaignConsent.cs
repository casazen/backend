using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// The one reading of «this guest agreed to marketing» that a message of a routine marked as a campaign needs (<c>isCampaign</c>, the guardrail of ADR-022-R11): the flag the campaigns themselves read
/// (<c>Guest.MarketingConsent</c>, set once the e-mail is confirmed), and none for a guest who was erased, anonymized or asked for the erasure. The engine reads it when the routine fires
/// (<c>RoutineService</c>) and the scheduled messages read it where a message is planned and again where it is sent, so the two can never disagree on who has the consent.
/// </summary>
internal static class CampaignConsent
{
    /// <summary>Whether the guest of the organization gave the marketing consent. The organization is named in the query, so a guest of another organization has none.</summary>
    internal static Task<bool> IsGrantedAsync(AppDbContext db, Guid orgId, Guid guestId, CancellationToken cancellationToken) =>
        db.Guests
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .AsNoTracking()
            .AnyAsync(
                g => g.Id == guestId
                     && g.OrgId == orgId
                     && g.MarketingConsent
                     && !g.IsDeleted
                     && !g.ErasureRequested
                     && g.DataAnonymizedDate == null,
                cancellationToken);
}
