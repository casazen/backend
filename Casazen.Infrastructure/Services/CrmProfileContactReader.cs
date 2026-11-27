using Casazen.Core.Entities;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// The contacts of the CRM profiles as the host screens may show them (E07/F01/US03): the detail of a profile, the list and the summary of a
/// merge proposal read them here, so they all show the same contacts. A contact given by a guest that is erased is not shown: the erasure does
/// not reach the contacts yet (<c>crm-profiles.md</c> § 1), so what the guest asked to be forgotten must not come back through the CRM. A contact
/// with no guest (a lead from the welcome page, a manual entry) is shown. The values come out decrypted by the model, as every read of a contact.
/// <para>The rule is one query, <see cref="Visible"/>: what a screen shows, what the filter «reachable by e-mail» counts and what the search by an
/// exact e-mail address or phone number finds are the same contacts, so the filter never keeps a profile whose item says it cannot be written to,
/// and the search never finds the address of a guest that asked to be forgotten.</para>
/// </summary>
internal static class CrmProfileContactReader
{
    /// <summary>
    /// The contacts of the org that a screen may show, search by or count: all but those given by an erased guest (one whose data were
    /// anonymized, that was deleted or that asked for the erasure). A query, so that it composes into the statement of the caller.
    /// </summary>
    public static IQueryable<GuestContact> Visible(AppDbContext db, Guid orgId)
    {
        ArgumentNullException.ThrowIfNull(db);

        var erased = db.Guests.Where(guest => guest.OrgId == orgId && (guest.DataAnonymizedDate != null || guest.IsDeleted || guest.ErasureRequested));
        return db.GuestContacts.Where(contact => contact.OrgId == orgId && !erased.Any(guest => guest.Id == contact.GuestId));
    }

    /// <summary>The contacts of the profiles <paramref name="profileIds"/> of the org, without those given by an erased guest.</summary>
    public static async Task<List<GuestContact>> LoadAsync(
        AppDbContext db,
        Guid orgId,
        List<Guid> profileIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(profileIds);

        return await Visible(db, orgId)
            .AsNoTracking()
            .Where(contact => contact.GuestCrmProfileId != null && profileIds.Contains(contact.GuestCrmProfileId.Value))
            .ToListAsync(cancellationToken);
    }
}
