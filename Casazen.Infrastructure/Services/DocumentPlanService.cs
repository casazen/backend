using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IDocumentPlanService"/>
/// <remarks>
/// <list type="bullet">
/// <item><b>Who owns on the day.</b> The ownerships of the property in force on the check-out day (started on or before it, not ended on it: the definition of the
/// owner ledger), so the receipt names the owner the ledger credits the stay to.</item>
/// <item><b>Who signs for several co-owners</b> (⚖ I6): the co-owner whose mandate is in force on the day (<see cref="OwnerMandateRules.InForce"/>); among several, or
/// when none has one, the largest share, then the oldest ownership, then the smallest id: always the same one.</item>
/// <item><b>A profile that can issue</b> is a complete one (<see cref="OwnerIssuerProfileRules.MissingFields"/>, with the regime code of the rule in force on the
/// day of the stay): a profile that misses a field counts as none.</item>
/// <item>Reads through the tenant filter of its caller and names the org of the booking in every query; it writes nothing.</item>
/// </list>
/// </remarks>
public sealed class DocumentPlanService(AppDbContext db, IFiscalTaxRuleResolver taxRules) : IDocumentPlanService
{
    public async Task<DocumentPlan> PlanAsync(Guid bookingId, CancellationToken ct = default)
    {
        var booking = await db.Bookings
            .AsNoTracking()
            .Where(b => b.Id == bookingId)
            .Select(b => new { b.Id, b.OrgId, b.PropertyId, b.BasePrice, b.CleaningFee, b.CheckOutDate, b.Property.Country })
            .FirstOrDefaultAsync(ct)
            ?? throw new NotFoundException($"Booking {bookingId} was not found") { Code = "booking_not_found", MessageKey = "BookingNotFound" };

        var organization = await db.OrgFiscalIssuerProfiles
            .AsNoTracking()
            .OfOrganization(booking.OrgId)
            .Select(p => new { p.Id, p.CleaningFeeTreatment })
            .FirstOrDefaultAsync(ct)
            ?? throw new DomainRuleException(FiscalDocumentErrors.ProfileIncomplete, DocumentIssuerErrorCodes.MessageKeyOf(DocumentIssuerErrorCodes.ProfileIncomplete));

        var day = DateOnly.FromDateTime(booking.CheckOutDate);
        var everything = new DocumentPlan(booking.Id, [new PlannedDocument(organization.Id, null, false, DocumentPlanRules.AllLineTypes, [])], []);
        var country = booking.Country?.Trim().ToUpperInvariant();
        var italian = country == FiscalDocumentRules.Italy;
        if (!italian && country != FiscalDocumentRules.Switzerland)
            return everything;

        var ownerships = await db.PropertyOwnerships
            .AsNoTracking()
            .Where(o => o.OrgId == booking.OrgId && o.PropertyId == booking.PropertyId)
            .Select(o => new { o.Id, o.PropertyOwnerId, o.SharePercent, o.OwnershipStart, o.OwnershipEnd })
            .ToListAsync(ct);
        var active = ownerships
            .Where(o => DateOnly.FromDateTime(o.OwnershipStart) <= day && (o.OwnershipEnd is not { } end || DateOnly.FromDateTime(end) > day))
            .ToList();
        if (active.Count == 0)
            return everything;

        var ownerIds = active.Select(o => o.PropertyOwnerId).Distinct().ToList();
        var mandates = await db.OwnerMandates
            .AsNoTracking()
            .Where(m => m.OrgId == booking.OrgId && ownerIds.Contains(m.PropertyOwnerId))
            .ToListAsync(ct);
        var withMandate = active.Where(o => OwnerMandateRules.InForce(mandates, o.PropertyOwnerId, o.Id, day) is not null).ToList();
        var signatory = (withMandate.Count > 0 ? withMandate : active)
            .OrderByDescending(o => o.SharePercent)
            .ThenBy(o => o.OwnershipStart)
            .ThenBy(o => o.PropertyOwnerId)
            .First();

        var profile = await db.OrgFiscalIssuerProfiles
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.OrgId == booking.OrgId && p.PropertyOwnerId == signatory.PropertyOwnerId, ct);
        var ready = profile is not null && CanIssue(profile, day);

        if (!italian)
        {
            // ⚖ C1: in Switzerland the agency issues, unless it chose «emette il proprietario» for this owner and the owner has a Swiss profile that can issue.
            if (!ready || !profile!.StayIssuedByOwner || !FiscalDocumentRules.IsSwiss(profile.CountryCode))
                return everything;
        }
        else if (!ready)
        {
            return new DocumentPlan(booking.Id, [], [signatory.PropertyOwnerId]);
        }

        var coOwners = active.Count > 1 ? await CoOwnersAsync(booking.OrgId, active.Select(o => (o.PropertyOwnerId, o.SharePercent)).ToList(), ct) : [];
        var services = DocumentPlanRules.AgencyServiceLineTypes(organization.CleaningFeeTreatment);
        var stay = new PlannedDocument(profile!.Id, signatory.PropertyOwnerId, false, DocumentPlanRules.AllLineTypes.Except(services).ToList(), coOwners);

        // The agency's own document only when it bills something: the cleaning of the booking as its service (the model has no extras of a booking yet).
        var cleaning = Math.Clamp(booking.CleaningFee, 0m, Math.Max(booking.BasePrice, 0m));
        return services.Contains(FiscalLineType.Cleaning) && cleaning > 0m
            ? new DocumentPlan(booking.Id, [stay, new PlannedDocument(organization.Id, null, true, services, [])], [])
            : new DocumentPlan(booking.Id, [stay], []);
    }

    /// <summary>The profile has everything an issue on <paramref name="day"/> needs (the regime code of the rule in force then when it has none of its own).</summary>
    private bool CanIssue(OrgFiscalIssuerProfile profile, DateOnly day)
    {
        var rule = taxRules.TryResolve(profile.CountryCode, profile.DocumentRegime, profile.StayVatTreatment, profile.CleaningFeeTreatment, day);
        return OwnerIssuerProfileRules.MissingFields(profile, rule?.FatturaPaRegimeCode).Count == 0;
    }

    /// <summary>The co-owners as the document prints them: the legal name, else «First Last», with the share; the largest share first.</summary>
    private async Task<IReadOnlyList<PlannedCoOwner>> CoOwnersAsync(Guid orgId, IReadOnlyList<(Guid OwnerId, decimal Share)> shares, CancellationToken ct)
    {
        var ids = shares.Select(s => s.OwnerId).Distinct().ToList();
        var names = await db.PropertyOwners
            .AsNoTracking()
            .Where(o => o.OrgId == orgId && ids.Contains(o.Id))
            .Select(o => new { o.Id, o.LegalName, o.FirstName, o.LastName })
            .ToDictionaryAsync(o => o.Id, ct);
        return shares
            .GroupBy(s => s.OwnerId)
            .Select(g => new PlannedCoOwner(
                g.Key,
                names.TryGetValue(g.Key, out var owner)
                    ? (string.IsNullOrWhiteSpace(owner.LegalName) ? $"{owner.FirstName} {owner.LastName}".Trim() : owner.LegalName.Trim())
                    : string.Empty,
                g.Sum(s => s.Share)))
            .OrderByDescending(c => c.SharePercent)
            .ThenBy(c => c.Name, StringComparer.Ordinal)
            .ToList();
    }
}
