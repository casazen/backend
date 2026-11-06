using System.Text.Json;
using System.Text.Json.Serialization;
using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// E07/F01/US02/T03 (NF-88, NF-20): the proofs of the sync of the CRM with the bookings. A retry never duplicates a profile or a contact, and the
/// guests of a registration never become profiles or contacts. On EF InMemory; the sync runs on a context of its own, as in the job, so the booker
/// is read from the database and nothing is tracked beforehand.
/// </summary>
public class CrmProfileSyncProofTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    /// <summary>
    /// Data rule: the sync is idempotent, so a retry never duplicates a profile or a contact. A guest who comes back (two records of the same
    /// person, a stay over and one to come at two houses) is synced; syncing both bookings again, an hour later, changes no row at all, and the
    /// person is still one profile with one stay counted, two houses and one contact for each address and number.
    /// </summary>
    [Fact]
    public async Task CrmProfileSync_Twice_ChangesNothing_Test()
    {
        var store = CrmTestWorld.NewStore();
        var orgId = Guid.NewGuid();
        var villa = Guid.NewGuid();
        var casa = Guid.NewGuid();
        var birth = new DateTime(1982, 9, 30, 0, 0, 0, DateTimeKind.Utc);
        Guid pastBooking;
        Guid nextBooking;
        await using (var seed = CrmTestWorld.Open(store))
        {
            var past = CrmTestWorld.AddGuest(seed, orgId, "Sofia", "Marchetti", "sofia@example.com", phone: "333 0000555", dateOfBirth: birth);
            past.MarketingConsent = true;
            past.MarketingConsentDate = CrmTestWorld.Day(-100);
            var pastStay = CrmTestWorld.AddBooking(seed, past, villa, BookingStatus.CheckedOut, BookingSource.Direct, -70, 3, 450m);
            seed.MarketingConsentRecords.Add(new MarketingConsentRecord
            {
                OrgId = orgId,
                BookingId = pastStay.Id,
                GuestId = past.Id,
                MarketingEmail = "sofia@example.com",
                ConsentEmail = true,
                EmailConfirmed = true,
                ConsentTimestamp = CrmTestWorld.Day(-99),
                ConsentTextVersion = "consent-v1",
            });
            var next = CrmTestWorld.AddGuest(seed, orgId, "Sofia", "Marchetti", "Sofia@Example.com", phone: "+39 333 0000555", dateOfBirth: birth);
            var nextStay = CrmTestWorld.AddBooking(seed, next, casa, BookingStatus.Confirmed, BookingSource.Direct, 30, 3, 500m);
            await seed.SaveChangesAsync(Ct);
            pastBooking = pastStay.Id;
            nextBooking = nextStay.Id;
        }

        var clock = CrmTestWorld.Clock();
        await RunAsync(store, clock, orgId, pastBooking, nextBooking);
        var once = await SnapshotAsync(store);

        clock.Advance(TimeSpan.FromHours(1));
        await RunAsync(store, clock, orgId, pastBooking, nextBooking);
        await RunAsync(store, clock, orgId, nextBooking, pastBooking);
        var twice = await SnapshotAsync(store);

        Assert.Equal(once, twice);

        await using var db = CrmTestWorld.Open(store);
        var profile = Assert.Single(await db.GuestCrmProfiles.AsNoTracking().ToListAsync(Ct));
        Assert.Equal(1, profile.TotalStays);
        Assert.Equal(450m, profile.TotalSpend);
        var properties = JsonSerializer.Deserialize<List<Guid>>(profile.PropertyIdsJson)!;
        Assert.Equal(new[] { villa, casa }.Order(), properties.Order());
        Assert.All(await db.Guests.AsNoTracking().ToListAsync(Ct), guest => Assert.Equal(profile.Id, guest.GuestCrmProfileId));
        Assert.Equal(2, await db.GuestContacts.CountAsync(Ct));
        Assert.Empty(await db.GuestMergeCandidates.ToListAsync(Ct));
    }

    /// <summary>
    /// Privacy rule: the co-guests of the Alloggiati list never become profiles or contacts. The booking has the booker and two more guests in its
    /// registration, with their names and birth dates; only the booker is synced: one profile, the booker's own contacts, no new guest record, and
    /// nothing of the other two in any row of the CRM.
    /// </summary>
    [Fact]
    public async Task CrmProfileSync_IgnoresCoGuestsOfTheRegistration_Test()
    {
        var store = CrmTestWorld.NewStore();
        var orgId = Guid.NewGuid();
        Guid bookingId;
        await using (var seed = CrmTestWorld.Open(store))
        {
            var booker = CrmTestWorld.AddGuest(seed, orgId, "Mario", "Rossi", "mario.rossi@example.com", phone: "333 0000333");
            var booking = CrmTestWorld.AddBooking(seed, booker, Guid.NewGuid(), BookingStatus.CheckedIn, BookingSource.Direct, -2, 5, 800m);
            seed.StayGuests.Add(new StayGuest
            {
                BookingId = booking.Id,
                OrgId = orgId,
                GuestId = booker.Id,
                Position = 0,
                Type = StayGuestType.HeadOfFamily,
                FirstName = "Mario",
                LastName = "Rossi",
                DateOfBirth = new DateTime(1975, 2, 2, 0, 0, 0, DateTimeKind.Utc),
            });
            seed.StayGuests.Add(new StayGuest
            {
                BookingId = booking.Id,
                OrgId = orgId,
                Position = 1,
                Type = StayGuestType.FamilyMember,
                FirstName = "Fedora",
                LastName = "Lisi",
                DateOfBirth = new DateTime(1985, 3, 3, 0, 0, 0, DateTimeKind.Utc),
            });
            seed.StayGuests.Add(new StayGuest
            {
                BookingId = booking.Id,
                OrgId = orgId,
                Position = 2,
                Type = StayGuestType.FamilyMember,
                FirstName = "Ugo",
                LastName = "Pini",
                DateOfBirth = new DateTime(2012, 7, 7, 0, 0, 0, DateTimeKind.Utc),
            });
            await seed.SaveChangesAsync(Ct);
            bookingId = booking.Id;
        }

        await using var db = CrmTestWorld.Open(store);
        var outcome = await CrmTestWorld.Sync(db, CrmTestWorld.Clock()).SyncBookingAsync(orgId, bookingId, Ct);

        Assert.Equal(CrmSyncOutcome.Synced, outcome);
        var profile = Assert.Single(await db.GuestCrmProfiles.AsNoTracking().ToListAsync(Ct));
        Assert.Single(await db.Guests.AsNoTracking().ToListAsync(Ct));
        var contacts = await db.GuestContacts.AsNoTracking().ToListAsync(Ct);
        Assert.Equal(2, contacts.Count);
        Assert.All(contacts, contact => Assert.Equal(profile.Id, contact.GuestCrmProfileId));
        Assert.Empty(await db.GuestMergeCandidates.ToListAsync(Ct));

        var everything = JsonSerializer.Serialize(
            new { Profiles = await db.GuestCrmProfiles.AsNoTracking().ToListAsync(Ct), Contacts = contacts },
            new JsonSerializerOptions { ReferenceHandler = ReferenceHandler.IgnoreCycles });
        string[] coGuests = ["Fedora", "Lisi", "Ugo", "Pini", "19850303", "20120707", "1985-03-03", "2012-07-07"];
        Assert.All(coGuests, value => Assert.DoesNotContain(value, everything, StringComparison.Ordinal));
    }

    /// <summary>Syncs the bookings in order, each on a context of its own, as the job does (a booking that fails does not leave its tracker to the next).</summary>
    private static async Task RunAsync(DbContextOptions<AppDbContext> store, TimeProvider clock, Guid orgId, params Guid[] bookingIds)
    {
        foreach (var bookingId in bookingIds)
        {
            await using var db = CrmTestWorld.Open(store);
            var outcome = await CrmTestWorld.Sync(db, clock).SyncBookingAsync(orgId, bookingId, Ct);
            Assert.Equal(CrmSyncOutcome.Synced, outcome);
        }
    }

    /// <summary>Every row of the CRM and the link of every guest, as text: two equal snapshots are two runs that changed nothing, update times included.</summary>
    private static async Task<string> SnapshotAsync(DbContextOptions<AppDbContext> store)
    {
        await using var db = CrmTestWorld.Open(store);
        return JsonSerializer.Serialize(
            new
            {
                Profiles = await db.GuestCrmProfiles.AsNoTracking().OrderBy(p => p.Id).ToListAsync(Ct),
                Contacts = await db.GuestContacts.AsNoTracking().OrderBy(c => c.Id).ToListAsync(Ct),
                Proposals = await db.GuestMergeCandidates.AsNoTracking().OrderBy(c => c.Id).ToListAsync(Ct),
                Guests = await db.Guests.AsNoTracking().OrderBy(g => g.Id).Select(g => new { g.Id, g.GuestCrmProfileId, g.UpdatedAt }).ToListAsync(Ct),
            },
            new JsonSerializerOptions { ReferenceHandler = ReferenceHandler.IgnoreCycles });
    }
}
