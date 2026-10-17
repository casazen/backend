using Casazen.Core.Authorization;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// The landlord data the wizard fills in (LR-12, <see cref="ILongRentLandlordDefaultsService"/>): the first landlord of the most
/// recent lease the caller reaches whose data were not anonymized, or else the fiscal data of the org and the name and e-mail of the
/// caller. The leases are the ones <see cref="HostScope"/> allows, in SQL, so a collaborator limited to some properties never reads
/// the landlord of a lease of another one. Not masked: it is the caller's own landlord data, sent only to who may create a lease.
/// </summary>
public sealed class LongRentLandlordDefaultsService(AppDbContext db) : ILongRentLandlordDefaultsService
{
    public async Task<LandlordDefaultsDto> GetAsync(HostScope scope, string userId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        var leases = db.LeaseContracts.AsNoTracking().Where(l => l.OrgId == scope.OrgId).InScope(scope);
        var last = await (
                from l in leases
                join p in db.Parties.AsNoTracking() on l.Id equals p.LeaseContractId
                where p.Role == PartyRole.Landlord && p.AnonymizedAt == null && p.FirstName != string.Empty && p.FiscalCode != string.Empty
                orderby l.CreatedAt descending, l.Id, p.Position
                select new { p.FirstName, p.LastName, p.FiscalCode, p.Residence, p.ContactEmail })
            .FirstOrDefaultAsync(cancellationToken);
        if (last is not null)
        {
            return new LandlordDefaultsDto(
                last.FirstName,
                last.LastName,
                last.FiscalCode,
                last.Residence,
                string.IsNullOrWhiteSpace(last.ContactEmail) ? null : last.ContactEmail);
        }

        // No lease yet: what the org declared for the fiscal side and who is asking. Every field can stay empty.
        var org = await db.Orgs.AsNoTracking().Where(o => o.Id == scope.OrgId)
            .Select(o => new { o.FiscalCode, o.PartitaIvaNumber })
            .FirstOrDefaultAsync(cancellationToken);
        var user = await db.Users.AsNoTracking().Where(u => u.Id == userId)
            .Select(u => new { u.FirstName, u.LastName, u.Email })
            .FirstOrDefaultAsync(cancellationToken);

        return new LandlordDefaultsDto(
            NullIfBlank(user?.FirstName),
            NullIfBlank(user?.LastName),
            NullIfBlank(org?.FiscalCode) ?? NullIfBlank(org?.PartitaIvaNumber),
            Residence: null,
            NullIfBlank(user?.Email));
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
