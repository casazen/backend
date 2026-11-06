using Casazen.Core.Entities;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="ICrmProfileSyncService"/>
/// <remarks>
/// <list type="bullet">
/// <item><b>Closed with the module</b>: <see cref="IsAvailable"/> is the flag <c>Features:CrmMarketing</c> and the key of the index
/// (<see cref="CrmProfileSyncGate"/>); without either nothing is read or written and the outcome is <see cref="CrmSyncOutcome.NotAvailable"/>.</item>
/// <item><b>Order</b>: the profile first (<see cref="IGuestMatchingService.FindOrCreateProfileAsync"/>, so the contacts have a profile to belong
/// to), then the contacts, then the aggregates. Each step saves what it changed and nothing when nothing changed, so a run that stops half way
/// leaves what it did and the next run finishes it, and a run of an unchanged guest writes nothing.</item>
/// <item><b>Privacy</b>: the guest is read with the columns the matching and the contacts use (name, e-mail, phone, birth date, country, the
/// consent flag) and the flags that say the guest is erased: no document, no place of birth, no other Alloggiati field, and never
/// <see cref="StayGuest"/>. The guests of a registration are not the booker: they never become profiles or contacts.</item>
/// <item><b>Tenant</b>: every query names the org; run it in a scope under that org (the event bus does, and so does the job).</item>
/// </list>
/// </remarks>
public sealed class CrmProfileSyncService(
    AppDbContext db,
    IGuestMatchingService matching,
    ICrmContactService contacts,
    ICrmContactIndex contactIndex,
    IFeatureFlags featureFlags,
    TimeProvider timeProvider,
    ILogger<CrmProfileSyncService> logger) : ICrmProfileSyncService
{
    public bool IsAvailable => CrmProfileSyncGate.IsOpen(featureFlags, contactIndex);

    public async Task<CrmSyncOutcome> SyncBookingAsync(Guid orgId, Guid bookingId, CancellationToken cancellationToken = default)
    {
        if (!IsAvailable)
            return CrmSyncOutcome.NotAvailable;

        var guestId = await db.Bookings
            .AsNoTracking()
            .Where(b => b.OrgId == orgId && b.Id == bookingId)
            .Select(b => (Guid?)b.GuestId)
            .FirstOrDefaultAsync(cancellationToken);
        if (guestId is null)
        {
            logger.LogInformation("CRM sync: booking {BookingId} of org {OrgId} was not found", bookingId, orgId);
            return CrmSyncOutcome.BookingNotFound;
        }

        return await SyncGuestAsync(orgId, guestId.Value, cancellationToken);
    }

    public async Task<CrmSyncOutcome> SyncGuestAsync(Guid orgId, Guid guestId, CancellationToken cancellationToken = default)
    {
        if (!IsAvailable)
            return CrmSyncOutcome.NotAvailable;

        var guest = await LoadGuestAsync(orgId, guestId, cancellationToken);
        if (guest is null || guest.DataAnonymizedDate is not null || guest.IsDeleted || guest.ErasureRequested)
        {
            logger.LogInformation("CRM sync: guest {GuestId} of org {OrgId} is not there or is erased: nothing to keep", guestId, orgId);
            return CrmSyncOutcome.GuestNotSyncable;
        }

        var profile = await matching.FindOrCreateProfileAsync(guest, cancellationToken);
        await contacts.SyncFromGuestAsync(guest.Id, cancellationToken);
        await matching.UpdateProfileAggregatesAsync(profile.Id, cancellationToken);
        await RefreshLanguageAsync(profile, cancellationToken);
        return CrmSyncOutcome.Synced;
    }

    /// <summary>
    /// The guest of the org: the one the context already tracks, else a record with the columns the CRM uses and nothing else (it is attached
    /// by the matching, which only changes the link to the profile).
    /// </summary>
    private async Task<Guest?> LoadGuestAsync(Guid orgId, Guid guestId, CancellationToken cancellationToken)
    {
        var tracked = db.Guests.Local.FirstOrDefault(g => g.Id == guestId && g.OrgId == orgId);
        if (tracked is not null)
            return tracked;

        return await db.Guests
            .AsNoTracking()
            .Where(g => g.OrgId == orgId && g.Id == guestId)
            .Select(g => new Guest
            {
                Id = g.Id,
                OrgId = g.OrgId,
                FirstName = g.FirstName,
                LastName = g.LastName,
                Email = g.Email,
                PhoneNumber = g.PhoneNumber,
                Country = g.Country,
                DateOfBirth = g.DateOfBirth,
                GuestCrmProfileId = g.GuestCrmProfileId,
                DataAnonymizedDate = g.DataAnonymizedDate,
                IsDeleted = g.IsDeleted,
                ErasureRequested = g.ErasureRequested,
            })
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>The language of the profile is the language of the contact that was written to last in a page the person was on.</summary>
    private async Task RefreshLanguageAsync(GuestCrmProfile profile, CancellationToken cancellationToken)
    {
        var language = await db.GuestContacts
            .AsNoTracking()
            .Where(c => c.OrgId == profile.OrgId && c.GuestCrmProfileId == profile.Id && c.Language != string.Empty)
            .OrderByDescending(c => c.UpdatedAt)
            .ThenByDescending(c => c.Id)
            .Select(c => c.Language)
            .FirstOrDefaultAsync(cancellationToken);
        if (string.IsNullOrEmpty(language) || string.Equals(profile.Language, language, StringComparison.Ordinal))
            return;

        profile.Language = language;
        profile.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(cancellationToken);
    }
}
