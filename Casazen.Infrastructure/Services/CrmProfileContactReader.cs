using Casazen.Core.Entities;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// The contacts of the CRM profiles as the host screens may show them (E07/F01/US03): the detail of a profile, the list and the summary of a
/// merge proposal read them here, so they all show the same contacts. A contact given by a guest that is erased is not shown: the erasure does
/// not reach the contacts yet (<c>crm-profiles.md</c> § 1), so what the guest asked to be forgotten must not come back through the CRM. A contact
/// with no guest (a lead from the welcome page, a manual entry) is shown. The values come out decrypted by the model, as every read of a contact.
/// </summary>
internal static class CrmProfileContactReader
{
    /// <summary>The contacts of the profiles <paramref name="profileIds"/> of the org, without those given by an erased guest.</summary>
    public static async Task<List<GuestContact>> LoadAsync(
        AppDbContext db,
        Guid orgId,
        List<Guid> profileIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(profileIds);

        var contacts = await db.GuestContacts
            .AsNoTracking()
            .Where(contact => contact.OrgId == orgId
                && contact.GuestCrmProfileId != null
                && profileIds.Contains(contact.GuestCrmProfileId.Value))
            .ToListAsync(cancellationToken);

        var guestIds = contacts.Where(contact => contact.GuestId != null).Select(contact => contact.GuestId!.Value).Distinct().ToList();
        if (guestIds.Count == 0)
            return contacts;

        var erased = await db.Guests
            .AsNoTracking()
            .Where(guest => guest.OrgId == orgId
                && guestIds.Contains(guest.Id)
                && (guest.DataAnonymizedDate != null || guest.IsDeleted || guest.ErasureRequested))
            .Select(guest => guest.Id)
            .ToListAsync(cancellationToken);
        if (erased.Count == 0)
            return contacts;

        var hidden = erased.ToHashSet();
        return contacts.Where(contact => contact.GuestId is null || !hidden.Contains(contact.GuestId.Value)).ToList();
    }
}
