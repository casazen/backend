using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IShowcaseHoldReader"/>
/// <remarks>
/// <para><b>Tenancy.</b> <c>ShowcaseBookingHolds</c> is keyed by the supplier org and not tenant-filtered (the TN-2 allow-list says
/// why: the customer is anonymous and the supplier-only account has no <c>User.OrgId</c>). The only statement here goes through
/// <see cref="LiveHoldsOf"/>, which carries the explicit <c>OrgId</c> predicate; <c>ShowcaseBookingTenancyTests</c> forbids any
/// other code from using the table.</para>
/// <para>It reads the hours and the expiry of a hold and nothing else: not the payload, not the code, not the e-mail index.</para>
/// </remarks>
public sealed class ShowcaseHoldReader(AppDbContext db) : IShowcaseHoldReader
{
    public async Task<IReadOnlyList<ShowcaseHoldInterval>> ListLiveAsync(
        Guid supplierOrgId,
        DateTime fromUtc,
        DateTime toUtc,
        DateTime nowUtc,
        CancellationToken cancellationToken = default) =>
        await LiveIntervalsOf(db, supplierOrgId, fromUtc, toUtc, nowUtc).ToListAsync(cancellationToken);

    /// <summary>
    /// <see cref="LiveHoldsOf"/> reduced to what the planner needs: the hours and the expiry, three columns of the table (the
    /// payload and the e-mail index are not selected). Static and internal so a test can read the SQL it becomes.
    /// </summary>
    internal static IQueryable<ShowcaseHoldInterval> LiveIntervalsOf(
        AppDbContext db,
        Guid supplierOrgId,
        DateTime fromUtc,
        DateTime toUtc,
        DateTime nowUtc) =>
        LiveHoldsOf(db, supplierOrgId, fromUtc, toUtc, nowUtc)
            .Select(h => new ShowcaseHoldInterval(h.StartUtc, h.EndUtc, h.ExpiresAt));

    /// <summary>
    /// The holds of <paramref name="supplierOrgId"/> that overlap <c>[fromUtc, toUtc)</c> and are alive at
    /// <paramref name="nowUtc"/>: not expired (<c>ExpiresAt &gt; now</c>) and not consumed. Static and internal so a test can read
    /// the SQL it becomes on the PostgreSQL provider without a server.
    /// </summary>
    internal static IQueryable<ShowcaseBookingHold> LiveHoldsOf(
        AppDbContext db,
        Guid supplierOrgId,
        DateTime fromUtc,
        DateTime toUtc,
        DateTime nowUtc) =>
        db.ShowcaseBookingHolds
            .AsNoTracking()
            .Where(h => h.OrgId == supplierOrgId
                        && h.ConsumedAt == null
                        && h.ExpiresAt > nowUtc
                        && h.StartUtc < toUtc
                        && h.EndUtc > fromUtc)
            .OrderBy(h => h.StartUtc)
            .ThenBy(h => h.Id);
}
