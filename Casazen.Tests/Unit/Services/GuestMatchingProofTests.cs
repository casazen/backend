using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// E07/F01/US02/T01 (NF-88, NF-20, ADR-021): the proofs of the matching of a guest to a CRM profile. One person is one profile; two people are
/// never merged by a job; a forward address of an online travel agency is never a key; nothing of the document or of the registration reaches the
/// profile. On EF InMemory, the real matching service over the real keyed index of the CRM. The e-mail contact a guest is found by is written here
/// by hand, as the contact service of the next task writes it: the matching only reads it.
/// </summary>
public class GuestMatchingProofTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    /// <summary>
    /// Data rule: one person is one profile. A second record of the guest with the same address (spelled otherwise) is linked to the profile of the
    /// address; a record with another address but the same name (the words the other way round, an accent) and the same birth date is linked to
    /// the profile of the name and birth date; someone else is not.
    /// </summary>
    [Fact]
    public async Task GuestMatching_SameEmailOrNameBirth_LinksTheSameProfile_Test()
    {
        await using var db = CrmTestWorld.Open(CrmTestWorld.NewStore());
        var clock = CrmTestWorld.Clock();
        var orgId = Guid.NewGuid();
        var propertyId = Guid.NewGuid();
        var birth = new DateTime(1980, 5, 12, 0, 0, 0, DateTimeKind.Utc);

        var first = CrmTestWorld.AddGuest(db, orgId, "Mario", "Rossi", "mario.rossi@example.com", dateOfBirth: birth);
        CrmTestWorld.AddBooking(db, first, propertyId, BookingStatus.CheckedOut, BookingSource.Direct, -30, 2, 200m);
        var sameAddress = CrmTestWorld.AddGuest(db, orgId, "Mario", "Rossi", "  MARIO.Rossi@Example.COM ");
        CrmTestWorld.AddBooking(db, sameAddress, propertyId, BookingStatus.Confirmed, BookingSource.Direct, 20, 2, 220m);
        var sameNameAndBirth = CrmTestWorld.AddGuest(db, orgId, "ROSSI,", "Mário", "m.rossi@lavoro.example.com", dateOfBirth: birth);
        CrmTestWorld.AddBooking(db, sameNameAndBirth, propertyId, BookingStatus.Confirmed, BookingSource.Direct, 40, 2, 240m);
        var someoneElse = CrmTestWorld.AddGuest(
            db, orgId, "Luca", "Bianchi", "luca.bianchi@example.com", dateOfBirth: new DateTime(1990, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        CrmTestWorld.AddBooking(db, someoneElse, propertyId, BookingStatus.Confirmed, BookingSource.Direct, 60, 2, 260m);
        // The same person with the same address and birth date in another org: a profile never crosses the tenant.
        var otherOrgId = Guid.NewGuid();
        var elsewhere = CrmTestWorld.AddGuest(db, otherOrgId, "Mario", "Rossi", "mario.rossi@example.com", dateOfBirth: birth);
        CrmTestWorld.AddBooking(db, elsewhere, Guid.NewGuid(), BookingStatus.Confirmed, BookingSource.Direct, 80, 2, 280m);
        await db.SaveChangesAsync(Ct);

        var matching = CrmTestWorld.Matching(db, clock);

        var profile = await matching.FindOrCreateProfileAsync(first, Ct);
        // The address of the first record becomes a contact: the key the second record is found by.
        await AddEmailContactAsync(db, first, GuestContactOrigin.DirectBooking);
        var byAddress = await matching.FindOrCreateProfileAsync(sameAddress, Ct);
        var byNameAndBirth = await matching.FindOrCreateProfileAsync(sameNameAndBirth, Ct);
        var other = await matching.FindOrCreateProfileAsync(someoneElse, Ct);
        var inAnotherOrg = await matching.FindOrCreateProfileAsync(elsewhere, Ct);

        Assert.Equal(profile.Id, byAddress.Id);
        Assert.Equal(profile.Id, byNameAndBirth.Id);
        Assert.NotEqual(profile.Id, other.Id);
        Assert.Equal(profile.Id, first.GuestCrmProfileId);
        Assert.Equal(profile.Id, sameAddress.GuestCrmProfileId);
        Assert.Equal(profile.Id, sameNameAndBirth.GuestCrmProfileId);
        Assert.Equal(2, await db.GuestCrmProfiles.CountAsync(p => p.OrgId == orgId, Ct));
        Assert.NotEqual(profile.Id, inAnotherOrg.Id);
        Assert.Equal(otherOrgId, inAnotherOrg.OrgId);
        Assert.Equal(1, await db.GuestCrmProfiles.CountAsync(p => p.OrgId == otherOrgId, Ct));
        Assert.Empty(await db.GuestMergeCandidates.ToListAsync(Ct));
    }

    /// <summary>
    /// Data rule: two different people are never merged by a job. A guest who shares only a phone number with another gets a profile of its
    /// own and a proposal for a person (score 80, reason <c>same_phone</c>, the smaller id first); nothing is merged, and running the matching
    /// again proposes nothing more.
    /// </summary>
    [Fact]
    public async Task GuestMatching_PhoneOnly_ProposesNeverMerges_Test()
    {
        await using var db = CrmTestWorld.Open(CrmTestWorld.NewStore());
        var clock = CrmTestWorld.Clock();
        var orgId = Guid.NewGuid();

        var anna = CrmTestWorld.AddGuest(db, orgId, "Anna", "Verdi", "anna.verdi@example.com", phone: "333 0000111");
        CrmTestWorld.AddBooking(db, anna, Guid.NewGuid(), BookingStatus.CheckedOut, BookingSource.Direct, -30, 2, 200m);
        var luca = CrmTestWorld.AddGuest(db, orgId, "Luca", "Neri", "luca.neri@example.com", phone: "+39 333 0000111");
        CrmTestWorld.AddBooking(db, luca, Guid.NewGuid(), BookingStatus.Confirmed, BookingSource.Direct, 20, 2, 220m);
        await db.SaveChangesAsync(Ct);

        var matching = CrmTestWorld.Matching(db, clock);
        var annaProfile = await matching.FindOrCreateProfileAsync(anna, Ct);
        var lucaProfile = await matching.FindOrCreateProfileAsync(luca, Ct);
        // The same guest again: it keeps its profile and nothing is proposed twice.
        await matching.FindOrCreateProfileAsync(luca, Ct);

        Assert.NotEqual(annaProfile.Id, lucaProfile.Id);
        var proposal = Assert.Single(await db.GuestMergeCandidates.AsNoTracking().ToListAsync(Ct));
        Assert.Equal(GuestMatchRules.SamePhoneScore, proposal.Score);
        Assert.Equal(GuestMergeStatus.Proposed, proposal.Status);
        Assert.Contains(GuestMatchRules.SamePhone, proposal.ReasonsJson, StringComparison.Ordinal);
        Assert.True(proposal.ProfileAId.CompareTo(proposal.ProfileBId) < 0, "A pair is stored with the smaller id first.");
        Assert.Equal(
            new[] { annaProfile.Id, lucaProfile.Id }.Order(),
            new[] { proposal.ProfileAId, proposal.ProfileBId }.Order());
        Assert.All(await db.GuestCrmProfiles.AsNoTracking().ToListAsync(Ct), profile => Assert.Null(profile.MergedIntoProfileId));
        Assert.NotEqual(anna.GuestCrmProfileId, luca.GuestCrmProfileId);
    }

    /// <summary>
    /// Data rule: two syncs of one guest make one profile. The event handler and the job can sync the same guest at the same moment; the one that
    /// comes second must find, once the first has written, that the guest is linked, and use that profile, instead of making another one that
    /// the second update of the link would leave with no guest. Here both syncs read the guest before either writes (neither sees a link); the
    /// second ends with the profile of the first, the guest linked to it, and one profile in the org.
    /// </summary>
    [Fact]
    public async Task GuestMatching_TwoSyncsOfOneGuest_MakeOneProfile_Test()
    {
        var store = CrmTestWorld.NewStore();
        var clock = CrmTestWorld.Clock();
        var orgId = Guid.NewGuid();
        Guid guestId;
        await using (var seed = CrmTestWorld.Open(store))
        {
            var booker = CrmTestWorld.AddGuest(seed, orgId, "Elisa", "Fontana", "elisa.fontana@example.com");
            CrmTestWorld.AddBooking(seed, booker, Guid.NewGuid(), BookingStatus.Confirmed, BookingSource.Direct, 15, 2, 260m);
            await seed.SaveChangesAsync(Ct);
            guestId = booker.Id;
        }

        // Each sync has a context of its own, as in the job and in the event bus, and has read the guest while it had no link.
        await using var firstDb = CrmTestWorld.Open(store);
        await using var secondDb = CrmTestWorld.Open(store);
        var first = await firstDb.Guests.SingleAsync(g => g.Id == guestId, Ct);
        var second = await secondDb.Guests.SingleAsync(g => g.Id == guestId, Ct);
        Assert.Null(first.GuestCrmProfileId);
        Assert.Null(second.GuestCrmProfileId);

        var madeByFirst = await CrmTestWorld.Matching(firstDb, clock).FindOrCreateProfileAsync(first, Ct);
        var madeBySecond = await CrmTestWorld.Matching(secondDb, clock).FindOrCreateProfileAsync(second, Ct);

        Assert.Equal(madeByFirst.Id, madeBySecond.Id);
        Assert.Equal(madeByFirst.Id, second.GuestCrmProfileId);
        await using var verify = CrmTestWorld.Open(store);
        var profile = Assert.Single(await verify.GuestCrmProfiles.AsNoTracking().ToListAsync(Ct));
        Assert.Equal(madeByFirst.Id, profile.Id);
        Assert.Equal(profile.Id, (await verify.Guests.AsNoTracking().SingleAsync(g => g.Id == guestId, Ct)).GuestCrmProfileId);
        Assert.Empty(await verify.GuestMergeCandidates.AsNoTracking().ToListAsync(Ct));
    }

    /// <summary>
    /// Privacy rule: a forward address of an online travel agency cannot join two guests. Two different people whose bookings come from two
    /// channels with the same forward address, a direct booking that brings an address already stored as a forward, two direct bookings with the
    /// same address on a relay domain of the list, and two bookings of a portal with no value of its own (<c>OtherChannel</c>, an OTA for the code)
    /// whose address the contact service stored as a forward: seven guests, seven profiles, and no proposal.
    /// </summary>
    [Fact]
    public async Task GuestMatching_OtaForwardAddress_IsNeverAKey_Test()
    {
        // One definition of an OTA in the code: the forward rule follows it, OtherChannel included (it once listed six channels and left it out).
        Assert.All(Enum.GetValues<BookingSource>(), source => Assert.Equal(FiscalCopy.IsOtaBookingSource(source), OtaForwardAddress.IsOtaSource(source)));
        Assert.True(OtaForwardAddress.IsOtaSource(BookingSource.OtherChannel));

        await using var db = CrmTestWorld.Open(CrmTestWorld.NewStore());
        var clock = CrmTestWorld.Clock();
        var options = new CrmOptions { OtaForwardDomains = ["relay.example.org"] };
        var orgId = Guid.NewGuid();
        var propertyId = Guid.NewGuid();

        var viaAirbnb = CrmTestWorld.AddGuest(db, orgId, "Giulia", "Bianchi", "reservation-4821@guest.example.com");
        CrmTestWorld.AddBooking(db, viaAirbnb, propertyId, BookingStatus.Confirmed, BookingSource.Airbnb, 10, 2, 300m);
        var viaBooking = CrmTestWorld.AddGuest(db, orgId, "Paolo", "Verdi", "reservation-4821@guest.example.com");
        CrmTestWorld.AddBooking(db, viaBooking, propertyId, BookingStatus.Confirmed, BookingSource.BookingCom, 30, 2, 320m);
        var storedForward = CrmTestWorld.AddGuest(db, orgId, "Anna", "Rosa", "reservation-4821@guest.example.com");
        CrmTestWorld.AddBooking(db, storedForward, propertyId, BookingStatus.Confirmed, BookingSource.Direct, 45, 2, 330m);
        var onRelayOne = CrmTestWorld.AddGuest(db, orgId, "Elena", "Gialli", "abc123@relay.example.org");
        CrmTestWorld.AddBooking(db, onRelayOne, propertyId, BookingStatus.Confirmed, BookingSource.Direct, 50, 2, 340m);
        var onRelayTwo = CrmTestWorld.AddGuest(db, orgId, "Marta", "Neri", "abc123@relay.example.org");
        CrmTestWorld.AddBooking(db, onRelayTwo, propertyId, BookingStatus.Confirmed, BookingSource.Direct, 70, 2, 360m);
        // A portal of Octorate with no value of its own (OtherChannel) is an OTA for the code: two people it gives one address are two guests.
        var viaPortal = CrmTestWorld.AddGuest(db, orgId, "Sara", "Blu", "portal-2290@guest.example.com");
        CrmTestWorld.AddBooking(db, viaPortal, propertyId, BookingStatus.Confirmed, BookingSource.OtherChannel, 90, 2, 380m);
        var viaPortalToo = CrmTestWorld.AddGuest(db, orgId, "Nico", "Viola", "portal-2290@guest.example.com");
        CrmTestWorld.AddBooking(db, viaPortalToo, propertyId, BookingStatus.Confirmed, BookingSource.OtherChannel, 110, 2, 400m);
        await db.SaveChangesAsync(Ct);

        var matching = CrmTestWorld.Matching(db, clock, options);
        var contacts = CrmTestWorld.Contacts(db, clock, options);
        var profiles = new List<Guid>();
        foreach (var guest in new[] { viaAirbnb, viaBooking, storedForward, onRelayOne, onRelayTwo, viaPortal, viaPortalToo })
        {
            profiles.Add((await matching.FindOrCreateProfileAsync(guest, Ct)).Id);

            // The first guest of each address leaves it as a contact, an OTA forward as the contact service stores it: if it were a key, the next
            // guest would be found by it. The address of the portal is stored by the contact service itself, which must know the portal as a channel.
            if (guest == viaAirbnb || guest == onRelayOne)
                await AddEmailContactAsync(db, guest, GuestContactOrigin.OtaForward);
            else if (guest == viaPortal)
                await contacts.SyncFromGuestAsync(guest.Id, Ct);
        }

        Assert.Equal(7, profiles.Distinct().Count());
        Assert.Equal(7, await db.GuestCrmProfiles.CountAsync(p => p.OrgId == orgId, Ct));
        Assert.Equal(GuestContactOrigin.OtaForward, (await db.GuestContacts.AsNoTracking().SingleAsync(c => c.GuestId == viaPortal.Id, Ct)).Origin);
        Assert.Empty(await db.GuestMergeCandidates.ToListAsync(Ct));
    }

    /// <summary>
    /// Privacy rule: nothing of the document or of the registration reaches the profile. The booker has a document, a place of birth and a
    /// citizenship, and the booking has a second guest in the Alloggiati list; after the matching no row of the CRM holds any of it, the second guest
    /// is no profile, and the birth date is only inside the keyed hash.
    /// </summary>
    [Fact]
    public async Task GuestMatching_Profile_NeverHoldsAlloggiatiData_Test()
    {
        var store = CrmTestWorld.NewStore();
        var orgId = Guid.NewGuid();
        Guid guestId;
        await using (var seed = CrmTestWorld.Open(store))
        {
            var booker = CrmTestWorld.AddGuest(
                seed,
                orgId,
                "Mario",
                "Rossi",
                "mario.rossi@example.com",
                phone: "333 0000222",
                dateOfBirth: new DateTime(1980, 5, 12, 0, 0, 0, DateTimeKind.Utc));
            booker.PlaceOfBirth = "Roma (RM)";
            booker.Nationality = "Italiana";
            booker.Gender = Gender.Male;
            booker.DocumentType = GuestDocumentType.Passport;
            booker.DocumentNumber = "AB1234567";
            booker.DocumentIssuingCountry = "Questura di Roma";
            booker.DocumentScanUrl = "guest-documents/scan-mario-rossi.jpg";
            var booking = CrmTestWorld.AddBooking(seed, booker, Guid.NewGuid(), BookingStatus.CheckedOut, BookingSource.Direct, -30, 2, 200m);
            seed.StayGuests.Add(new StayGuest
            {
                BookingId = booking.Id,
                OrgId = orgId,
                Position = 1,
                Type = StayGuestType.FamilyMember,
                FirstName = "Fedora",
                LastName = "Lisi",
                DateOfBirth = new DateTime(1985, 3, 3, 0, 0, 0, DateTimeKind.Utc),
                BirthComuneName = "Torino",
                CitizenshipName = "Italiana",
                DocumentNumber = "CO9988776",
            });
            await seed.SaveChangesAsync(Ct);
            guestId = booker.Id;
        }

        // A context of its own, as in a job: the booker is read from the database.
        await using var db = CrmTestWorld.Open(store);
        var matching = CrmTestWorld.Matching(db, CrmTestWorld.Clock());
        var guest = await db.Guests.SingleAsync(g => g.Id == guestId, Ct);
        var created = await matching.FindOrCreateProfileAsync(guest, Ct);
        await matching.UpdateProfileAggregatesAsync(created.Id, Ct);

        db.ChangeTracker.Clear();
        var profile = Assert.Single(await db.GuestCrmProfiles.AsNoTracking().ToListAsync(Ct));
        Assert.Equal(64, profile.NameBirthKeyHash!.Length);
        Assert.Equal(1, profile.TotalStays);
        Assert.Single(await db.Guests.AsNoTracking().ToListAsync(Ct));

        var everything = JsonSerializer.Serialize(
            new
            {
                Profiles = await db.GuestCrmProfiles.AsNoTracking().ToListAsync(Ct),
                Contacts = await db.GuestContacts.AsNoTracking().ToListAsync(Ct),
                Proposals = await db.GuestMergeCandidates.AsNoTracking().ToListAsync(Ct),
            },
            new JsonSerializerOptions { ReferenceHandler = ReferenceHandler.IgnoreCycles });
        string[] neverThere =
        [
            "AB1234567",
            "Roma (RM)",
            "Italiana",
            "Questura di Roma",
            "guest-documents",
            "Fedora",
            "Lisi",
            "CO9988776",
            "Torino",
            "19800512",
            "1980-05-12",
            "19850303",
            "1985-03-03",
        ];
        Assert.All(neverThere, secret => Assert.DoesNotContain(secret, everything, StringComparison.Ordinal));

        // The model has no place for any of it.
        string[] alloggiatiFields =
        [
            nameof(Guest.DateOfBirth),
            nameof(Guest.PlaceOfBirth),
            nameof(Guest.Nationality),
            nameof(Guest.DocumentType),
            nameof(Guest.DocumentNumber),
            nameof(Guest.DocumentScanUrl),
            nameof(Guest.DocumentIssuingCountry),
            nameof(Guest.Gender),
        ];
        foreach (var type in new[] { typeof(GuestCrmProfile), typeof(GuestContact), typeof(GuestMergeCandidate) })
        {
            var names = type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(property => property.Name).ToList();
            Assert.All(alloggiatiFields, field => Assert.DoesNotContain(field, names));
        }
    }

    /// <summary>The e-mail contact of a guest as the contact service stores it: the keyed hash of the normalized address, on the profile of the guest.</summary>
    private static async Task AddEmailContactAsync(AppDbContext db, Guest guest, GuestContactOrigin origin)
    {
        var address = CrmNormalizer.NormalizeEmail(guest.Email);
        db.GuestContacts.Add(new GuestContact
        {
            OrgId = guest.OrgId,
            GuestCrmProfileId = guest.GuestCrmProfileId,
            GuestId = guest.Id,
            Channel = GuestContactChannel.Email,
            Value = address,
            ValueHash = CrmTestIndex.Create().Hash(address),
            Origin = origin,
            LegalBasis = GuestContactLegalBasis.ServiceOnly,
        });
        await db.SaveChangesAsync(Ct);
    }
}
