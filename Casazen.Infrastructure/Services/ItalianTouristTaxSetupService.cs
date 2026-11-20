using Casazen.Core.Entities;
using Casazen.Core.Regulatory;
using Casazen.Core.Services;
using Casazen.Core.TouristTax;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IItalianTouristTaxSetup"/>
/// <remarks>
/// <list type="bullet">
/// <item><b>Read only.</b> It writes nothing: a proposal is a suggestion for the host to confirm by saving its own list of portals, and the hint changes no document and raises no «To do».</item>
/// <item><b>The proposals</b> come from the ISTAT codes of the properties of the organization (<see cref="ItalianPortalProposals"/>: Milano and Firenze only) and only while the organization has
/// not saved its own list: what the host saved is its answer and is never proposed over.</item>
/// <item><b>The hint</b> looks at the stays that are active (<c>AlloggiatiStatusRules</c>, the one definition: a request, a cancelled stay or a no-show has no tax) in Italy and ended no longer than
/// <see cref="ConflictWindowMonths"/> months ago (an older stay is nothing the host can still put right), and compares the collector their record gives (the host's declaration, else the one of the
/// calculation it confirmed: <see cref="ItalianPortalTouristTax.Contradicts"/>) with the organization's rule.</item>
/// <item><b>Tenant.</b> Every read names the organization explicitly on top of the tenant filter; it reads counts of the organization as a whole, so the controller gives it to a caller that reaches the
/// whole organization only. Nothing personal is read: no guest, no name, no amount.</item>
/// </list>
/// </remarks>
public sealed class ItalianTouristTaxSetupService(AppDbContext db, TimeProvider timeProvider) : IItalianTouristTaxSetup
{
    /// <summary>The stays that ended longer ago than this are not looked at for the hint: the host can no longer put them right.</summary>
    public const int ConflictWindowMonths = 12;

    public async Task<ItalianTouristTaxSetupView> ReadAsync(
        Guid orgId,
        IReadOnlyList<BookingSource> organizationChannels,
        bool organizationConfirmed,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(organizationChannels);

        var proposals = organizationConfirmed ? [] : await ProposalsAsync(orgId, ct);
        var conflict = await ChannelDataConflictAsync(orgId, organizationChannels, ct);
        return new ItalianTouristTaxSetupView(proposals, conflict);
    }

    /// <summary>The proposals for the comuni of the properties of the organization (the ISTAT code the host saved on the property; none, no proposal).</summary>
    private async Task<IReadOnlyList<ItalianPortalProposal>> ProposalsAsync(Guid orgId, CancellationToken ct)
    {
        var comuni = await db.Properties
            .AsNoTracking()
            .Where(p => p.OrgId == orgId && p.ComuneIstatCode != null)
            .Select(p => p.ComuneIstatCode)
            .Distinct()
            .ToListAsync(ct);
        return ItalianPortalProposals.For(comuni);
    }

    /// <summary>True when the record of one recent stay of a channel says the opposite of the organization's rule for that channel.</summary>
    private async Task<bool> ChannelDataConflictAsync(Guid orgId, IReadOnlyList<BookingSource> organizationChannels, CancellationToken ct)
    {
        var since = timeProvider.GetUtcNow().UtcDateTime.AddMonths(-ConflictWindowMonths);

        // The active stays of the Italian properties that are not over for more than a year (the one definition of an active booking: ActiveBookingStatuses and no no-show).
        var stays = db.Bookings
            .AsNoTracking()
            .Where(b => b.OrgId == orgId && b.CheckOutDate >= since && AlloggiatiStatusRules.ActiveBookingStatuses.Contains(b.Status) && b.NoShowAt == null)
            .Where(AlloggiatiStatusRules.ItalianProperties);

        // A record that says who collected: the host's declaration for the stay, or the collector of the calculation it confirmed.
        var records = await db.StayTouristTaxes
            .AsNoTracking()
            .Where(t => t.OrgId == orgId && (t.DeclaredCollector != null || (t.ConfirmedAt != null && t.Collector != null)))
            .Join(stays, t => t.BookingId, b => b.Id, (t, b) => new { b.Source, t.DeclaredCollector, t.Collector })
            .ToListAsync(ct);

        return records.Any(r => ItalianPortalTouristTax.Contradicts(r.Source, r.DeclaredCollector ?? r.Collector, organizationChannels));
    }
}
