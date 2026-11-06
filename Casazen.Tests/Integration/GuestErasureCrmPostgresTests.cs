using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Integration.Postgres;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// The erasure of the CRM data of a guest (E07/F06/US02/T01, NF-95, NF-20) on a real PostgreSQL database, where the rules that matter are made by the
/// foreign keys and not by EF: the contacts of a profile are deleted with it (<c>ON DELETE CASCADE</c>), a guest cannot point at a profile that goes
/// (<c>NO ACTION</c>), and a pair of profiles has a check on the order of its ids. So the opt-out of the guest, a contact that has to survive, is
/// proved to survive the deletion of its profile, and the order of the save (the guest unlinked and the opt-out taken off the profile before the
/// profile is deleted) to be one the database accepts. The rest of the rules is proved on every provider by
/// <c>GdprCrmProofTests</c>.
/// </summary>
public class GuestErasureCrmPostgresTests
{
    // Two profiles whose ids differ in the last byte only: the smaller first, as the check of the pair of a proposal of merge wants it.
    private static readonly Guid ErasedProfileId = Guid.Parse("c0de0000-0000-4000-8000-00000000a001");
    private static readonly Guid OtherProfileId = Guid.Parse("c0de0000-0000-4000-8000-00000000a002");

    // The two profiles merged into the first one (E07/F01/US03/T02), a chain: the first tombstone points to the profile, the second to the first.
    private static readonly Guid FirstTombstoneId = Guid.Parse("c0de0000-0000-4000-8000-00000000a003");
    private static readonly Guid SecondTombstoneId = Guid.Parse("c0de0000-0000-4000-8000-00000000a004");

    private static readonly DateTime ConsentAt = new(2026, 10, 1, 9, 30, 0, DateTimeKind.Utc);
    private static readonly DateTime OptOutAt = new(2026, 10, 15, 18, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime ErasedAt = new(2026, 11, 6, 9, 0, 0, DateTimeKind.Utc);

    // The keyed hash of the address that unsubscribed: a fixed text of the right length, the database does not look into it.
    private static readonly string OptOutHash = new('f', GuestContact.ValueHashLength);

    /// <summary>
    /// A profile that only the first guest is linked to: the foreign key from the contacts to the profile cascades, so the opt-out (a suppressed contact)
    /// survives the deletion of the profile only because the erasure took it off the profile first. The contacts the guest gave and the lead of the profile
    /// are gone, the proposal of merge with them, the guest is unlinked, and the row that stays holds nothing but the keyed hash, the flag and its date.
    /// </summary>
    [PostgresFact]
    public async Task GdprErasure_OnPostgres_KeepsTheOptOutWhenTheProfileIsDeleted_Test()
    {
        await using var database = PostgresTestDatabase.CreateMigrated("crmerase");
        var provider = new EphemeralDataProtectionProvider();
        var seeded = await SeedAsync(database, provider, withSecondGuest: false);

        GuestErasureOutcome outcome;
        await using (var db = Open(database, provider))
        {
            var guest = await db.Guests.SingleAsync(g => g.Id == seeded.FirstGuestId);
            outcome = await NewEraser(db).AnonymizeAsync(guest, ErasedAt, CancellationToken.None);
            await db.SaveChangesAsync();
        }

        Assert.True(outcome.Changed);
        Assert.Equal(2, outcome.CrmContactsRemoved);
        Assert.Equal(1, outcome.CrmOptOutsKept);
        Assert.True(outcome.CrmProfileRemoved);

        await using var verify = Open(database, provider);
        Assert.False(await verify.GuestCrmProfiles.AnyAsync(p => p.Id == ErasedProfileId));
        Assert.True(await verify.GuestCrmProfiles.AnyAsync(p => p.Id == OtherProfileId));
        Assert.Equal(0, await verify.GuestMergeCandidates.CountAsync(m => m.OrgId == seeded.OrgId));
        Assert.Null((await verify.Guests.AsNoTracking().SingleAsync(g => g.Id == seeded.FirstGuestId)).GuestCrmProfileId);

        var marker = Assert.Single(await verify.GuestContacts.AsNoTracking().Where(c => c.OrgId == seeded.OrgId).ToListAsync());
        AssertBareOptOut(marker);

        // The table itself holds an empty value, not a payload: nothing of the address is left in the row.
        var stored = await verify.Database.SqlQueryRaw<string>("SELECT \"Value\" AS \"Value\" FROM \"GuestContacts\"").ToListAsync();
        Assert.Equal(new[] { string.Empty }, stored);
    }

    /// <summary>
    /// A profile that two guests are linked to: the erasure of the first never deletes it. Its opt-out leaves the profile (the foreign key from the
    /// contact to the profile is null), the other guest stays linked with its contact, the lead of the profile stays, and the profile loses the keyed
    /// hashes, the tags and the country that may be the erased guest's.
    /// </summary>
    [PostgresFact]
    public async Task GdprErasure_OnPostgres_ProfileOfOtherGuests_SurvivesAndTheOptOutLeavesIt_Test()
    {
        await using var database = PostgresTestDatabase.CreateMigrated("crmerase");
        var provider = new EphemeralDataProtectionProvider();
        var seeded = await SeedAsync(database, provider, withSecondGuest: true);

        await using (var db = Open(database, provider))
        {
            var guest = await db.Guests.SingleAsync(g => g.Id == seeded.FirstGuestId);
            var outcome = await NewEraser(db).AnonymizeAsync(guest, ErasedAt, CancellationToken.None);
            await db.SaveChangesAsync();
            Assert.False(outcome.CrmProfileRemoved);
        }

        await using var verify = Open(database, provider);
        var profile = await verify.GuestCrmProfiles.AsNoTracking().SingleAsync(p => p.Id == ErasedProfileId);
        Assert.Null(profile.PhoneKeyHash);
        Assert.Null(profile.NameBirthKeyHash);
        Assert.Equal(string.Empty, profile.CompositeKeyHash);
        Assert.Equal("[]", profile.TagsJson);
        Assert.Equal(string.Empty, profile.Country);
        Assert.Null((await verify.Guests.AsNoTracking().SingleAsync(g => g.Id == seeded.FirstGuestId)).GuestCrmProfileId);
        Assert.Equal(ErasedProfileId, (await verify.Guests.AsNoTracking().SingleAsync(g => g.Id == seeded.SecondGuestId)).GuestCrmProfileId);
        Assert.Equal(1, await verify.GuestMergeCandidates.CountAsync(m => m.OrgId == seeded.OrgId));

        // The opt-out of the first guest, the contact of the second and the lead: three rows, the first one bare and off the profile.
        var contacts = await verify.GuestContacts.AsNoTracking().Where(c => c.OrgId == seeded.OrgId).ToListAsync();
        Assert.Equal(3, contacts.Count);
        AssertBareOptOut(Assert.Single(contacts, c => c.ValueHash == OptOutHash));
        Assert.Equal(ErasedProfileId, Assert.Single(contacts, c => c.GuestId == seeded.SecondGuestId).GuestCrmProfileId);
        Assert.Equal(ErasedProfileId, Assert.Single(contacts, c => c.Value == "lead@example.com").GuestCrmProfileId);
    }

    /// <summary>
    /// The two guests of one profile erased in the same run and saved once (the retention does so for a chunk of guests): the first leaves the profile, the
    /// last one takes it, with its proposal and the lead. The database accepts the save in the order EF chooses (both guests unlinked and the opt-out off the
    /// profile before the profile is deleted), nothing is left with no guest, and the opt-out of the first guest is the one row that stays.
    /// </summary>
    [PostgresFact]
    public async Task GdprErasure_OnPostgres_GuestsOfOneProfileErasedInTheSameRun_TakeTheProfileWithThem_Test()
    {
        await using var database = PostgresTestDatabase.CreateMigrated("crmerase");
        var provider = new EphemeralDataProtectionProvider();
        var seeded = await SeedAsync(database, provider, withSecondGuest: true);

        await using (var db = Open(database, provider))
        {
            var eraser = NewEraser(db);
            var first = await db.Guests.SingleAsync(g => g.Id == seeded.FirstGuestId);
            var second = await db.Guests.SingleAsync(g => g.Id == seeded.SecondGuestId);

            var afterFirst = await eraser.AnonymizeAsync(first, ErasedAt, CancellationToken.None);
            var afterSecond = await eraser.AnonymizeAsync(second, ErasedAt, CancellationToken.None);
            await db.SaveChangesAsync();

            Assert.False(afterFirst.CrmProfileRemoved);
            Assert.True(afterSecond.CrmProfileRemoved);
        }

        await using var verify = Open(database, provider);
        Assert.False(await verify.GuestCrmProfiles.AnyAsync(p => p.Id == ErasedProfileId));
        Assert.Equal(0, await verify.GuestMergeCandidates.CountAsync(m => m.OrgId == seeded.OrgId));
        Assert.All(await verify.Guests.AsNoTracking().Where(g => g.OrgId == seeded.OrgId).ToListAsync(), g => Assert.Null(g.GuestCrmProfileId));
        AssertBareOptOut(Assert.Single(await verify.GuestContacts.AsNoTracking().Where(c => c.OrgId == seeded.OrgId).ToListAsync()));
    }

    /// <summary>
    /// A profile that only the first guest is linked to, with two profiles merged into it (a chain of tombstones) and the opt-out of the guest left by the
    /// merge on the second tombstone: the foreign key from the contacts to that tombstone cascades too, so the opt-out survives only because the erasure
    /// took it off the tombstone before the tombstone goes. The survivor, both tombstones and the proposals that decided the merges are deleted, and the
    /// profile that has nothing to do with them stays.
    /// </summary>
    [PostgresFact]
    public async Task GdprErasure_OnPostgres_ProfileWithMergedProfiles_TakesTheTombstonesAndKeepsTheOptOutLeftOnOne_Test()
    {
        await using var database = PostgresTestDatabase.CreateMigrated("crmerase");
        var provider = new EphemeralDataProtectionProvider();
        var seeded = await SeedAsync(database, provider, withSecondGuest: false, withMergedProfiles: true);

        await using (var db = Open(database, provider))
        {
            var guest = await db.Guests.SingleAsync(g => g.Id == seeded.FirstGuestId);
            var outcome = await NewEraser(db).AnonymizeAsync(guest, ErasedAt, CancellationToken.None);
            await db.SaveChangesAsync();
            Assert.True(outcome.CrmProfileRemoved);
        }

        await using var verify = Open(database, provider);
        Assert.Equal(OtherProfileId, Assert.Single(await verify.GuestCrmProfiles.AsNoTracking().Where(p => p.OrgId == seeded.OrgId).ToListAsync()).Id);
        Assert.Equal(0, await verify.GuestMergeCandidates.CountAsync(m => m.OrgId == seeded.OrgId));
        Assert.Null((await verify.Guests.AsNoTracking().SingleAsync(g => g.Id == seeded.FirstGuestId)).GuestCrmProfileId);
        AssertBareOptOut(Assert.Single(await verify.GuestContacts.AsNoTracking().Where(c => c.OrgId == seeded.OrgId).ToListAsync()));
    }

    /// <summary>
    /// The same profile with two guests, and the tombstones of its chain: the erasure of the first guest leaves the profile, and the tombstones, which hold
    /// what cannot be told from the other guest's data, are emptied row by row (tags, country, language, every keyed hash, the primary guest, the aggregates)
    /// with the pointer of the chain intact; the opt-out leaves its tombstone and the other guest keeps its link and its contact.
    /// </summary>
    [PostgresFact]
    public async Task GdprErasure_OnPostgres_ProfileOfOtherGuestsWithMergedProfiles_EmptiesTheTombstonesAndKeepsThePointer_Test()
    {
        await using var database = PostgresTestDatabase.CreateMigrated("crmerase");
        var provider = new EphemeralDataProtectionProvider();
        var seeded = await SeedAsync(database, provider, withSecondGuest: true, withMergedProfiles: true);

        await using (var db = Open(database, provider))
        {
            var guest = await db.Guests.SingleAsync(g => g.Id == seeded.FirstGuestId);
            var outcome = await NewEraser(db).AnonymizeAsync(guest, ErasedAt, CancellationToken.None);
            await db.SaveChangesAsync();
            Assert.False(outcome.CrmProfileRemoved);
        }

        await using var verify = Open(database, provider);
        var profiles = await verify.GuestCrmProfiles.AsNoTracking().Where(p => p.OrgId == seeded.OrgId).ToListAsync();
        Assert.Equal(4, profiles.Count);
        Assert.Null(Assert.Single(profiles, p => p.Id == ErasedProfileId).MergedIntoProfileId);

        foreach (var (id, pointsTo) in new[] { (FirstTombstoneId, ErasedProfileId), (SecondTombstoneId, FirstTombstoneId) })
        {
            var tombstone = Assert.Single(profiles, p => p.Id == id);
            Assert.Equal(pointsTo, tombstone.MergedIntoProfileId);
            Assert.Equal("[]", tombstone.TagsJson);
            Assert.Equal(string.Empty, tombstone.Country);
            Assert.Equal(string.Empty, tombstone.Language);
            Assert.Null(tombstone.PhoneKeyHash);
            Assert.Null(tombstone.NameBirthKeyHash);
            Assert.Equal(string.Empty, tombstone.CompositeKeyHash);
            Assert.Null(tombstone.PrimaryGuestId);
            Assert.Equal(0, tombstone.TotalStays);
            Assert.Equal(0m, tombstone.TotalSpend);
        }

        Assert.Equal(3, await verify.GuestMergeCandidates.CountAsync(m => m.OrgId == seeded.OrgId));
        Assert.Equal(ErasedProfileId, (await verify.Guests.AsNoTracking().SingleAsync(g => g.Id == seeded.SecondGuestId)).GuestCrmProfileId);
        var contacts = await verify.GuestContacts.AsNoTracking().Where(c => c.OrgId == seeded.OrgId).ToListAsync();
        Assert.Equal(3, contacts.Count);
        AssertBareOptOut(Assert.Single(contacts, c => c.ValueHash == OptOutHash));
    }

    private sealed record Seeded(Guid OrgId, Guid FirstGuestId, Guid SecondGuestId);

    private static AppDbContext Open(PostgresTestDatabase database, IDataProtectionProvider provider) =>
        new(database.CreateOptions(), tenantContext: null, provider);

    private static GuestDataEraser NewEraser(AppDbContext db) => new(db, Mock.Of<IFileStorage>(), NullLogger<GuestDataEraser>.Instance);

    /// <summary>What an opt-out is once its guest is erased: the org, the channel, the keyed hash, the flag, its date and its source, and nothing else.</summary>
    private static void AssertBareOptOut(GuestContact marker)
    {
        Assert.Equal(OptOutHash, marker.ValueHash);
        Assert.True(marker.IsSuppressed);
        Assert.Equal(OptOutAt, marker.SuppressedAt);
        Assert.Equal("unsubscribe", marker.SuppressedSource);
        Assert.Equal(string.Empty, marker.Value);
        Assert.Null(marker.DisplayName);
        Assert.Null(marker.ConsentIpAddress);
        Assert.Null(marker.GuestId);
        Assert.Null(marker.GuestCrmProfileId);
        Assert.Equal(GuestContactLegalBasis.None, marker.LegalBasis);
        Assert.Equal(string.Empty, marker.BasisVersion);
    }

    /// <summary>
    /// One org, a profile (with a proposal of merge to another profile) that the first guest is linked to and, with <paramref name="withSecondGuest"/>,
    /// a second one too. The first guest gave an e-mail address on consent and an old one that unsubscribed; the profile has a lead nobody owns; the
    /// second guest gave an address of its own. With <paramref name="withMergedProfiles"/> two profiles were merged into the profile (a chain of
    /// tombstones, with the proposals that decided the merges) and the old address that unsubscribed stayed on the second tombstone.
    /// </summary>
    private static async Task<Seeded> SeedAsync(PostgresTestDatabase database, IDataProtectionProvider provider, bool withSecondGuest, bool withMergedProfiles = false)
    {
        await using var db = Open(database, provider);
        var org = new OrgEntity
        {
            Name = "Casa Cancellazione CRM",
            Slug = $"crm-erase-{Guid.NewGuid():N}"[..28],
            DisplayName = "Casa Cancellazione CRM",
            ContactEmail = "crm-erase@example.com",
            OrgType = OrgType.Host,
            PlanTier = PlanTier.Starter,
        };
        var first = NewGuest(org.Id, "Mario", "Rossi", "mario.rossi@example.com");
        var second = NewGuest(org.Id, "Luisa", "Verdi", "luisa.verdi@example.com");
        var profile = NewProfile(org.Id, ErasedProfileId, 'a');
        var other = NewProfile(org.Id, OtherProfileId, 'b');
        first.GuestCrmProfileId = profile.Id;
        second.GuestCrmProfileId = withSecondGuest ? profile.Id : null;

        db.Orgs.Add(org);
        db.GuestCrmProfiles.AddRange(profile, other);
        db.Guests.Add(first);
        if (withSecondGuest)
            db.Guests.Add(second);

        // The profiles merged into the first one: two tombstones in a chain, each still holding what it held, and the proposals that decided the merges.
        var optOutProfileId = profile.Id;
        if (withMergedProfiles)
        {
            var firstTombstone = NewProfile(org.Id, FirstTombstoneId, 'c');
            firstTombstone.MergedIntoProfileId = profile.Id;
            firstTombstone.TagsJson = """["tag-del-primo"]""";
            firstTombstone.Country = "Francia";
            firstTombstone.Language = "fr";
            var secondTombstone = NewProfile(org.Id, SecondTombstoneId, 'd');
            secondTombstone.MergedIntoProfileId = firstTombstone.Id;
            secondTombstone.TagsJson = """["tag-del-secondo"]""";
            secondTombstone.Country = "Spagna";
            secondTombstone.Language = "es";
            secondTombstone.PrimaryGuestId = first.Id;
            db.GuestCrmProfiles.AddRange(firstTombstone, secondTombstone);
            db.GuestMergeCandidates.AddRange(
                NewProposal(org.Id, profile.Id, firstTombstone.Id, GuestMergeStatus.Merged),
                NewProposal(org.Id, firstTombstone.Id, secondTombstone.Id, GuestMergeStatus.Merged));

            // What a merge leaves behind when it moves the contacts of a profile and one stays: the opt-out on the second tombstone.
            optOutProfileId = secondTombstone.Id;
        }

        db.GuestContacts.AddRange(
            NewContact(org.Id, first.Id, profile.Id, "mario.rossi@example.com", '1', suppressed: false),
            NewContact(org.Id, first.Id, optOutProfileId, "mario.old@example.com", 'f', suppressed: true),
            NewContact(org.Id, null, profile.Id, "lead@example.com", '3', suppressed: false));
        if (withSecondGuest)
            db.GuestContacts.Add(NewContact(org.Id, second.Id, profile.Id, "luisa.verdi@example.com", '2', suppressed: false));

        db.GuestMergeCandidates.Add(NewProposal(org.Id, profile.Id, other.Id, GuestMergeStatus.Proposed));
        await db.SaveChangesAsync();
        return new Seeded(org.Id, first.Id, second.Id);
    }

    /// <summary>A proposal of merge of two profiles whose ids are in the order the check of the table wants (the smaller first).</summary>
    private static GuestMergeCandidate NewProposal(Guid orgId, Guid smallerProfileId, Guid largerProfileId, GuestMergeStatus status) => new()
    {
        OrgId = orgId,
        ProfileAId = smallerProfileId,
        ProfileBId = largerProfileId,
        Score = 90,
        ReasonsJson = """["same_phone"]""",
        Status = status,
    };

    private static Guest NewGuest(Guid orgId, string firstName, string lastName, string email) => new()
    {
        OrgId = orgId,
        FirstName = firstName,
        LastName = lastName,
        Email = email,
        PhoneNumber = "+393331234567",
        DataProcessingPurpose = "Booking Management",
    };

    private static GuestCrmProfile NewProfile(Guid orgId, Guid id, char hashChar) => new()
    {
        Id = id,
        OrgId = orgId,
        CompositeKeyHash = new string(hashChar, GuestCrmProfile.KeyHashLength),
        Language = "it",
        Country = "Italia",
        TotalStays = 2,
        TotalSpend = 450m,
        TagsJson = """["vip","famiglia"]""",
        PropertyIdsJson = "[]",
        FirstStayAt = ConsentAt,
        LastStayAt = ConsentAt,
        PhoneKeyHash = new string(hashChar, GuestCrmProfile.KeyHashLength),
        NameBirthKeyHash = new string(hashChar, GuestCrmProfile.KeyHashLength),
    };

    private static GuestContact NewContact(Guid orgId, Guid? guestId, Guid profileId, string value, char hashChar, bool suppressed) => new()
    {
        OrgId = orgId,
        GuestId = guestId,
        GuestCrmProfileId = profileId,
        Channel = GuestContactChannel.Email,
        Value = value,
        ValueHash = new string(hashChar, GuestContact.ValueHashLength),
        DisplayName = "Mario Rossi",
        Language = "it",
        Origin = GuestContactOrigin.CheckIn,
        Confirmation = GuestContactConfirmation.Confirmed,
        ConfirmedAt = ConsentAt,
        LegalBasis = suppressed ? GuestContactLegalBasis.ServiceOnly : GuestContactLegalBasis.Consent,
        BasisVersion = "marketing-v2",
        BasisRecordedAt = ConsentAt,
        ConsentSource = GuestConsentSource.GuestPortal,
        ConsentIpAddress = "203.0.113.7",
        IsSuppressed = suppressed,
        SuppressedAt = suppressed ? OptOutAt : null,
        SuppressedSource = suppressed ? "unsubscribe" : null,
        CreatedAt = ConsentAt,
        UpdatedAt = ConsentAt,
    };
}
