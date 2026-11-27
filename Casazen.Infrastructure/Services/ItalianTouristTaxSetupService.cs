using Casazen.Core.Entities;
using Casazen.Core.Exceptions;
using Casazen.Core.Regulatory;
using Casazen.Core.Services;
using Casazen.Core.TouristTax;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IItalianTouristTaxSetup"/>
/// <remarks>
/// <list type="bullet">
/// <item><b>The read writes nothing.</b> A proposal is a suggestion for the host to confirm, and the hint changes no document and raises no «To do». The only write is
/// <see cref="ConfirmComuniAsync"/>, the confirmation of a person.</item>
/// <item><b>The proposals</b> come from the ISTAT codes of the properties of the organization (<see cref="ItalianPortalProposals"/>: Milano and Firenze only) and only while the organization has
/// not saved its own list of the portals that collect it everywhere: what the host saved is its answer and is never proposed over. A proposal the host confirmed for its comune is no
/// proposal any more (<see cref="ItalianTouristTaxSetupView.ConfirmedComuni"/>).</item>
/// <item><b>The confirmation is per comune.</b> It is kept in the existing column of the profile of the organization (<c>PlatformCollectedTouristTaxChannels</c>, a jsonb: no migration) apart from
/// the list for every comune, which it never touches, and counts only for a property in that comune (<see cref="ItalianPortalTouristTax.Decide"/>).</item>
/// <item><b>The hint</b> looks at the stays that are active (<c>AlloggiatiStatusRules</c>, the one definition: a request, a cancelled stay or a no-show has no tax) in Italy and ended no longer than
/// <see cref="ConflictWindowMonths"/> months ago (an older stay is nothing the host can still put right), and compares the collector their record gives (the host's declaration, else the one of the
/// calculation it confirmed: <see cref="ItalianPortalTouristTax.Contradicts"/>) with the organization's rule for the channel in the comune of the property.</item>
/// <item><b>Tenant.</b> Every read names the organization explicitly on top of the tenant filter; it reads counts of the organization as a whole, so the controller gives it to a caller that reaches the
/// whole organization only. Nothing personal is read: no guest, no name, no amount.</item>
/// </list>
/// </remarks>
public sealed class ItalianTouristTaxSetupService(AppDbContext db, TimeProvider timeProvider, ILogger<ItalianTouristTaxSetupService>? logger = null) : IItalianTouristTaxSetup
{
    /// <summary>The stays that ended longer ago than this are not looked at for the hint: the host can no longer put them right.</summary>
    public const int ConflictWindowMonths = 12;

    /// <summary>The most (portal, comune) pairs an organization can confirm: a bound, never a meaning.</summary>
    public const int MaxConfirmedComuni = 200;

    /// <summary>The field the refusals of <see cref="ConfirmComuniAsync"/> name.</summary>
    public const string ConfirmedComuniField = "portalTouristTaxComuni";

    public async Task<ItalianTouristTaxSetupView> ReadAsync(
        Guid orgId,
        IReadOnlyList<BookingSource> organizationChannels,
        bool organizationConfirmed,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(organizationChannels);

        // The portals the organization confirmed for one comune each: they are no proposals any more, and they count only for a property in that comune.
        var stored = await db.OrgFiscalIssuerProfiles
            .AsNoTracking()
            .OfOrganization(orgId)
            .Select(p => p.PlatformCollectedTouristTaxChannelsJson)
            .FirstOrDefaultAsync(ct);
        var confirmed = OrgFiscalIssuerProfile.PortalComuniOf(stored);

        var proposals = organizationConfirmed ? [] : await ProposalsAsync(orgId, confirmed, ct);
        var conflict = await ChannelDataConflictAsync(orgId, organizationChannels, confirmed, ct);
        return new ItalianTouristTaxSetupView(proposals, conflict, [.. confirmed.Select(comune => new ItalianPortalConfirmation(comune.ComuneIstat, comune.Channel.ToString()))]);
    }

    public async Task ConfirmComuniAsync(Guid orgId, IReadOnlyList<PortalComune> comuni, string? updatedByUserId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(comuni);

        // A portal and the six digits of a comune, nothing else, and a bound: an empty list is an answer (none confirmed), never «every comune».
        var failures = new List<InvoiceDataFailure>();
        if (comuni.Count > MaxConfirmedComuni
            || comuni.Any(comune => comune is null || !FiscalCopy.IsOtaBookingSource(comune.Channel) || !OrgFiscalIssuerProfile.IsIstatCode(comune.ComuneIstat)))
        {
            failures.Add(new InvoiceDataFailure(DocumentIssuerErrorCodes.FieldInvalid, ConfirmedComuniField));
        }

        if (failures.Count > 0)
            throw DocumentIssuerService.FieldsInvalid(failures);

        // The profile of the organization itself, tracked: there is nothing to confirm on before the issuer step of the setup created it.
        var profile = await db.OrgFiscalIssuerProfiles.OfOrganization(orgId).FirstOrDefaultAsync(ct)
            ?? throw new DomainRuleException(DocumentIssuerErrorCodes.ProfileIncomplete, DocumentIssuerErrorCodes.MessageKeyOf(DocumentIssuerErrorCodes.ProfileIncomplete))
            {
                Extensions = new Dictionary<string, object?> { ["fields"] = new[] { IssuerFields.LegalName } },
            };

        // The proposals are Italian: a Swiss issuer has no comune to confirm.
        if (!GuestDocumentLineBuilder.IsItalian(profile.CountryCode))
            throw DocumentIssuerService.FieldsInvalid([new InvoiceDataFailure(DocumentIssuerErrorCodes.FieldInvalid, ConfirmedComuniField)]);

        profile.ConfirmPortalTouristTaxComuni(comuni);
        profile.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;
        profile.UpdatedByUserId = updatedByUserId is { Length: > 0 }
            ? updatedByUserId[..Math.Min(updatedByUserId.Length, OrgFiscalIssuerProfile.UpdatedByUserIdMaxLength)]
            : null;
        await db.SaveChangesAsync(ct);

        logger?.LogInformation("Portals confirmed for a comune by org {OrgId}: {Count} (nothing of the list for every comune is touched)", orgId, comuni.Count);
    }

    /// <summary>
    /// The proposals for the comuni of the properties of the organization (the ISTAT code the host saved on the property; none, no proposal), without the ones it already confirmed for that
    /// comune.
    /// </summary>
    private async Task<IReadOnlyList<ItalianPortalProposal>> ProposalsAsync(Guid orgId, IReadOnlyList<PortalComune> confirmed, CancellationToken ct)
    {
        var comuni = await db.Properties
            .AsNoTracking()
            .Where(p => p.OrgId == orgId && p.ComuneIstatCode != null)
            .Select(p => p.ComuneIstatCode)
            .Distinct()
            .ToListAsync(ct);
        return [.. ItalianPortalProposals.For(comuni).Where(proposal => !confirmed.Any(comune => comune.ComuneIstat == proposal.ComuneIstat && comune.Channel.ToString() == proposal.Channel))];
    }

    /// <summary>True when the record of one recent stay of a channel says the opposite of the organization's rule for that channel in the comune of the property.</summary>
    private async Task<bool> ChannelDataConflictAsync(
        Guid orgId,
        IReadOnlyList<BookingSource> organizationChannels,
        IReadOnlyList<PortalComune> confirmed,
        CancellationToken ct)
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
            .Join(stays, t => t.BookingId, b => b.Id, (t, b) => new { b.Source, b.Property.ComuneIstatCode, t.DeclaredCollector, t.Collector })
            .ToListAsync(ct);

        return records.Any(r => ItalianPortalTouristTax.Contradicts(r.Source, r.DeclaredCollector ?? r.Collector, organizationChannels, confirmed, r.ComuneIstatCode));
    }
}
